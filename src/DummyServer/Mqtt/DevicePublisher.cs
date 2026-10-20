using System.Text;
using System.Threading.Channels;
using ConveyorTwin.Shared;
using MQTTnet;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace ConveyorTwin.DummyServer.Mqtt;

/// <summary>보낸 메시지 한 건 (송신 로그 표시용). Note는 장애로 인한 처리 (지연, 미송신, 중복 등)</summary>
public sealed record SentMessage(DateTime Time, string Topic, int Qos, bool Retained, string Payload, string Note = "");

/// <summary>
/// 가상 설비(PLC 게이트웨이 역할)의 MQTT 클라이언트.
/// 내장 브로커에 일반 클라이언트로 접속해 발행한다. 브로커 내부 API로 직접 넣지 않는 이유는
/// 접속 시 등록한 Last Will이 "연결 끊김" 장애에서 실제로 동작해야 하기 때문이다.
/// 헤더(sid, seq, ts)는 여기서만 채운다 (docs/protocol.md 3절).
///
/// 통신 장애(연결 끊김, 전송 지연, 순서 뒤섞기)도 여기서 주입한다. 장애가 있어도 ts는 "생성 시각"이므로
/// 트윈은 수신 시각 - ts로 지연을 잴 수 있다.
/// </summary>
public sealed class DevicePublisher : IAsyncDisposable
{
    // QoS·Retained: 측정값은 SignalSpec, 나머지는 docs/protocol.md 2절 표
    const MqttQualityOfServiceLevel StateQos = MqttQualityOfServiceLevel.AtLeastOnce;

    /// <summary>순서 뒤섞기: 메시지마다 0~이 값(ms)의 무작위 지연 → 가까운 메시지끼리 순서가 바뀐다</summary>
    const int ShuffleJitterMs = 250;
    /// <summary>순서 뒤섞기: 이 확률로 이미 보낸 메시지를 늦게 한 번 더 보낸다 (중복·과거 메시지)</summary>
    const double DuplicateChance = 0.05;

    static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
    };

    readonly MqttClientFactory _factory = new();
    readonly Dictionary<string, long> _seq = new();
    readonly string _deviceId;
    IMqttClient? _client;

    /// <summary>세션 ID. 서버 프로세스가 시작될 때마다 바뀌며, 트윈은 이 값이 바뀌면 seq 기록을 초기화한다.</summary>
    public string Sid { get; } = Guid.NewGuid().ToString("N")[..6];

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>전송 지연 장애: 모든 발행을 이 시간(ms)만큼 늦춘다 (0 = 정상)</summary>
    public int DelayMs { get; set; }

    /// <summary>순서 뒤섞기 장애</summary>
    public bool Shuffle { get; set; }

    public event Action<SentMessage>? Sent;

    public DevicePublisher(string deviceId = Topics.DefaultDeviceId)
    {
        _deviceId = deviceId;
        _sender = Task.Run(() => RunSenderAsync(_senderCts.Token));
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        // Last Will: 비정상 종료 시 브로커가 대신 발행. ts는 발행 시점을 알 수 없어 접속 시각이 들어간다.
        var will = Serialize(new StatusMessage { Online = false, Sid = Sid, Ts = NowMs() });
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(EmbeddedBroker.Host, EmbeddedBroker.Port)
            .WithClientId($"{_deviceId}-device")
            .WithCleanSession()
            .WithWillTopic(Topics.Status(_deviceId))
            .WithWillPayload(Encoding.UTF8.GetBytes(will))
            .WithWillQualityOfServiceLevel(StateQos)
            .WithWillRetain()
            .Build();

        var client = _factory.CreateMqttClient();
        await client.ConnectAsync(options, ct);
        _client = client;
        await PublishStatusAsync(true, ct);
    }

    /// <summary>
    /// 연결 끊김 장애: DISCONNECT 패킷 없이 소켓을 닫는다 ("PLC 쪽 네트워크 케이블이 뽑힘").
    /// 브로커는 비정상 종료로 보고 Last Will(offline)을 발행한다.
    /// </summary>
    public void DropConnection()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
        Sent?.Invoke(new SentMessage(DateTime.Now, Topics.Status(_deviceId), 1, true, "(소켓 강제 종료)", "연결 끊김 → 브로커가 Will 발행"));
    }

    /// <summary>연결 끊김 복구: 새로 접속해 online을 발행한다. 끊긴 동안의 메시지는 유실된 상태다.</summary>
    public Task RestoreConnectionAsync(CancellationToken ct) => ConnectAsync(ct);

    public Task PublishStatusAsync(bool online, CancellationToken ct) =>
        PublishAsync(Topics.Status(_deviceId), new StatusMessage { Online = online, Sid = Sid, Ts = NowMs() },
            StateQos, retained: true, ct);

    public Task PublishRunStateAsync(RunState state, string? reason, CancellationToken ct)
    {
        var topic = Topics.RunState(_deviceId);
        return PublishAsync(topic, Stamp(topic, new RunStateMessage { State = state, Reason = reason }),
            StateQos, retained: true, ct);
    }

    public Task PublishTelemetryAsync(SignalSpec spec, string topic, double value, Quality quality, CancellationToken ct, string note = "") =>
        PublishAsync(topic, Stamp(topic, new TelemetryMessage { Value = Math.Round(value, 3), Unit = spec.Unit, Quality = quality }),
            (MqttQualityOfServiceLevel)spec.Qos, spec.Retained, ct, note);

    /// <summary>광전 센서 엣지. 재접속 시 과거 엣지가 다시 오면 물건이 생겨나므로 retained 금지.</summary>
    public Task PublishEdgeAsync(string topic, bool blocked, CancellationToken ct) =>
        PublishAsync(topic, Stamp(topic, new SensorEdgeMessage { Blocked = blocked }), StateQos, retained: false, ct);

    T Stamp<T>(string topic, T msg) where T : Envelope
    {
        lock (_seq)
        {
            _seq.TryGetValue(topic, out var seq);
            _seq[topic] = ++seq;
            msg.Seq = seq;
        }
        msg.Sid = Sid;
        msg.Ts = NowMs();
        return msg;
    }

    async Task PublishAsync(string topic, object payload, MqttQualityOfServiceLevel qos, bool retained,
        CancellationToken ct, string note = "")
    {
        var json = Serialize(payload);
        if (!IsConnected)
        {
            Sent?.Invoke(new SentMessage(DateTime.Now, topic, (int)qos, retained, json, Join(note, "미송신(연결 끊김)")));
            return;
        }

        if (Shuffle)
        {
            // 순서 뒤섞기: 메시지마다 따로 늦춰 보내므로 가까운 메시지끼리 순서가 바뀐다 (의도한 장애)
            int jitter = DelayMs + Random.Shared.Next(ShuffleJitterMs + 1);
            _ = SendLaterAsync(jitter, topic, json, qos, retained, Join(note, $"뒤섞기 {jitter}ms"), ct);
            if (Random.Shared.NextDouble() < DuplicateChance)
                _ = SendLaterAsync(Random.Shared.Next(300, 900), topic, json, qos, retained, Join(note, "중복 재전송"), ct);
            return;
        }

        if (DelayMs == 0 && Volatile.Read(ref _queued) == 0)
        {
            await SendAsync(topic, json, qos, retained, note, ct);
            return;
        }

        // 전송 지연: 순서를 지키는 송신 큐. MQTT는 TCP 연결 하나로 오가므로 실제 지연에서도
        // 뒤 메시지가 앞 메시지를 앞지르지 못한다. 지연을 꺼도 밀린 메시지가 다 나간 뒤에야 새 메시지가 나간다.
        // 발행 측(시뮬레이션)은 기다리지 않는다. 늦게 보내는 것은 "통신 경로"의 일이다
        Interlocked.Increment(ref _queued);
        _queue.Writer.TryWrite(new Outgoing(topic, json, qos, retained, note, NowMs(), NowMs() + DelayMs));
    }

    readonly record struct Outgoing(string Topic, string Json, MqttQualityOfServiceLevel Qos, bool Retained,
        string Note, long CreatedMs, long DueMs);

    readonly Channel<Outgoing> _queue = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _senderCts = new();
    int _queued;
    Task? _sender;

    /// <summary>송신 큐를 순서대로 비운다 (항목마다 DueMs까지 기다림)</summary>
    async Task RunSenderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _queue.Reader.ReadAllAsync(ct))
            {
                long wait = m.DueMs - NowMs();
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                await SendAsync(m.Topic, m.Json, m.Qos, m.Retained, Join(m.Note, $"지연 {NowMs() - m.CreatedMs}ms"), ct);
                Interlocked.Decrement(ref _queued);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task SendLaterAsync(int delayMs, string topic, string json, MqttQualityOfServiceLevel qos, bool retained,
        string note, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            await SendAsync(topic, json, qos, retained, note, ct);
        }
        catch (OperationCanceledException) { }
    }

    async Task SendAsync(string topic, string json, MqttQualityOfServiceLevel qos, bool retained, string note,
        CancellationToken ct)
    {
        var client = _client;
        if (client == null || !client.IsConnected)
        {
            Sent?.Invoke(new SentMessage(DateTime.Now, topic, (int)qos, retained, json, Join(note, "미송신(연결 끊김)")));
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retained)
            .Build();
        try
        {
            await client.PublishAsync(message, ct);
            Sent?.Invoke(new SentMessage(DateTime.Now, topic, (int)qos, retained, json, note));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 연결 끊김 장애 직후 전송 중이던 메시지
            Sent?.Invoke(new SentMessage(DateTime.Now, topic, (int)qos, retained, json, Join(note, "전송 실패: " + e.GetType().Name)));
        }
    }

    static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + ", " + b;

    static string Serialize(object o) => JsonConvert.SerializeObject(o, JsonSettings);

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        // 밀린 송신 큐는 버린다 (종료 중)
        _queue.Writer.TryComplete();
        _senderCts.Cancel();
        if (_sender != null) await _sender;
        Volatile.Write(ref _queued, 0);

        var client = _client;
        if (client?.IsConnected == true)
        {
            // 정상 종료: Will은 발행되지 않으므로 offline을 직접 남긴다 (지연 장애 중이어도 즉시)
            DelayMs = 0;
            Shuffle = false;
            await PublishStatusAsync(false, CancellationToken.None);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        }
        client?.Dispose();
    }
}
