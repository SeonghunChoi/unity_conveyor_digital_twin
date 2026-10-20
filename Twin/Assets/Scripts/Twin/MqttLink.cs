using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

namespace ConveyorTwin.Twin
{
    /// <summary>수신한 MQTT 메시지 한 건 (백그라운드 스레드 → 메인 스레드 전달용)</summary>
    public readonly struct ReceivedMessage
    {
        public readonly string Topic;
        public readonly string Payload;
        public readonly bool Retained;
        public readonly long ReceivedMs;    // 수신 시각 (Unix ms). 지연 측정과 Will offline 시각에 쓴다

        public ReceivedMessage(string topic, string payload, bool retained, long receivedMs)
        {
            Topic = topic;
            Payload = payload;
            Retained = retained;
            ReceivedMs = receivedMs;
        }
    }

    /// <summary>
    /// 브로커 연결. MQTTnet 콜백은 스레드 풀에서 오므로 메시지는 큐에만 넣고,
    /// 해석과 씬 반영은 메인 스레드(Update)에서 한다.
    /// 연결이 끊기면 1초마다 재접속한다. 재접속 시 retained 값으로 상태가 즉시 복원된다.
    /// </summary>
    public sealed class MqttLink : IDisposable
    {
        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

        readonly IMqttClient _client;
        readonly MqttClientOptions _options;
        readonly MqttClientSubscribeOptions _subscribe;
        readonly ConcurrentQueue<ReceivedMessage> _queue = new ConcurrentQueue<ReceivedMessage>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public bool IsConnected => _client.IsConnected;
        public string LastError { get; private set; }
        public int ConnectCount { get; private set; }

        public MqttLink(string host, int port, string topicFilter)
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();
            _options = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId("twin-" + Guid.NewGuid().ToString("N").Substring(0, 6))
                .WithCleanSession()
                .Build();
            _subscribe = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topicFilter).WithAtLeastOnceQoS())
                .Build();

            _client.ApplicationMessageReceivedAsync += e =>
            {
                var m = e.ApplicationMessage;
                var payload = m.PayloadSegment.Count > 0
                    ? Encoding.UTF8.GetString(m.PayloadSegment.Array, m.PayloadSegment.Offset, m.PayloadSegment.Count)
                    : string.Empty;
                _queue.Enqueue(new ReceivedMessage(m.Topic, payload, m.Retain, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                return Task.CompletedTask;
            };
        }

        public void Start() => Task.Run(() => KeepConnectedAsync(_cts.Token));

        async Task KeepConnectedAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_client.IsConnected)
                {
                    try
                    {
                        await _client.ConnectAsync(_options, ct);
                        await _client.SubscribeAsync(_subscribe, ct);
                        ConnectCount++;
                        LastError = null;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        LastError = e.GetBaseException().Message;
                    }
                }
                try { await Task.Delay(RetryInterval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        public bool TryDequeue(out ReceivedMessage message) => _queue.TryDequeue(out message);

        public void Dispose()
        {
            // 플레이 모드 종료(도메인 리로드) 전에 백그라운드 루프와 소켓을 반드시 정리한다
            _cts.Cancel();
            if (_client.IsConnected)
            {
                try { _client.DisconnectAsync().Wait(500); }
                catch (Exception) { /* 종료 중 오류는 무시 */ }
            }
            _client.Dispose();
            _cts.Dispose();
        }
    }
}
