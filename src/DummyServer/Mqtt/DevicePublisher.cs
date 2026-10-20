using System.Text;
using ConveyorTwin.Shared;
using MQTTnet;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace ConveyorTwin.DummyServer.Mqtt;

/// <summary>보낸 메시지 한 건 (송신 로그 표시용)</summary>
public sealed record SentMessage(DateTime Time, string Topic, int Qos, bool Retained, string Payload);

/// <summary>
/// 가상 설비(PLC 게이트웨이 역할)의 MQTT 클라이언트.
/// 내장 브로커에 일반 클라이언트로 접속해 발행한다. 브로커 내부 API로 직접 넣지 않는 이유는
/// 접속 시 등록한 Last Will이 "연결 끊김" 장애에서 실제로 동작해야 하기 때문이다.
/// 헤더(sid, seq, ts)는 여기서만 채운다 (docs/protocol.md 3절).
/// </summary>
public sealed class DevicePublisher : IAsyncDisposable
{
    // QoS·Retained: 측정값은 SignalSpec, 나머지는 docs/protocol.md 2절 표
    const MqttQualityOfServiceLevel StateQos = MqttQualityOfServiceLevel.AtLeastOnce;

    static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
    };

    readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    readonly Dictionary<string, long> _seq = new();
    readonly string _deviceId;

    /// <summary>세션 ID. 서버 프로세스가 시작될 때마다 바뀌며, 트윈은 이 값이 바뀌면 seq 기록을 초기화한다.</summary>
    public string Sid { get; } = Guid.NewGuid().ToString("N")[..6];

    public event Action<SentMessage>? Sent;

    public DevicePublisher(string deviceId = Topics.DefaultDeviceId)
    {
        _deviceId = deviceId;
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

        await _client.ConnectAsync(options, ct);
        await PublishStatusAsync(true, ct);
    }

    public Task PublishStatusAsync(bool online, CancellationToken ct) =>
        PublishAsync(Topics.Status(_deviceId), new StatusMessage { Online = online, Sid = Sid, Ts = NowMs() },
            StateQos, retained: true, ct);

    public Task PublishRunStateAsync(RunState state, string? reason, CancellationToken ct)
    {
        var topic = Topics.RunState(_deviceId);
        return PublishAsync(topic, Stamp(topic, new RunStateMessage { State = state, Reason = reason }),
            StateQos, retained: true, ct);
    }

    public Task PublishTelemetryAsync(SignalSpec spec, string topic, double value, Quality quality, CancellationToken ct) =>
        PublishAsync(topic, Stamp(topic, new TelemetryMessage { Value = Math.Round(value, 3), Unit = spec.Unit, Quality = quality }),
            (MqttQualityOfServiceLevel)spec.Qos, spec.Retained, ct);

    /// <summary>광전 센서 엣지. 재접속 시 과거 엣지가 다시 오면 물건이 생겨나므로 retained 금지.</summary>
    public Task PublishEdgeAsync(string topic, bool blocked, CancellationToken ct) =>
        PublishAsync(topic, Stamp(topic, new SensorEdgeMessage { Blocked = blocked }), StateQos, retained: false, ct);

    T Stamp<T>(string topic, T msg) where T : Envelope
    {
        _seq.TryGetValue(topic, out var seq);
        _seq[topic] = ++seq;
        msg.Sid = Sid;
        msg.Seq = seq;
        msg.Ts = NowMs();
        return msg;
    }

    async Task PublishAsync(string topic, object payload, MqttQualityOfServiceLevel qos, bool retained, CancellationToken ct)
    {
        var json = Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retained)
            .Build();

        await _client.PublishAsync(message, ct);
        Sent?.Invoke(new SentMessage(DateTime.Now, topic, (int)qos, retained, json));
    }

    static string Serialize(object o) => JsonConvert.SerializeObject(o, JsonSettings);

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            // 정상 종료: Will은 발행되지 않으므로 offline을 직접 남긴다
            await PublishStatusAsync(false, CancellationToken.None);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        }
        _client.Dispose();
    }
}
