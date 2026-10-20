using System.Net;
using MQTTnet.Server;

namespace ConveyorTwin.DummyServer.Mqtt;

/// <summary>
/// 서버 프로세스에 내장한 MQTT 브로커. 127.0.0.1:1883에서만 대기한다.
/// 실제 현장이라면 브로커는 별도 장비(또는 PLC 게이트웨이)에 있지만, 시연 편의상 서버에 함께 둔다.
/// </summary>
public sealed class EmbeddedBroker : IAsyncDisposable
{
    public const string Host = "127.0.0.1";
    public const int Port = 1883;

    readonly MqttServer _server;
    readonly HashSet<string> _clients = new();

    /// <summary>접속 중인 클라이언트 ID 목록이 바뀔 때 (임의 스레드에서 호출)</summary>
    public event Action<IReadOnlyCollection<string>>? ClientsChanged;

    public EmbeddedBroker()
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback)
            .WithDefaultEndpointPort(Port)
            .Build();

        _server = new MqttServerFactory().CreateMqttServer(options);
        _server.ClientConnectedAsync += e => UpdateClients(e.ClientId, true);
        _server.ClientDisconnectedAsync += e => UpdateClients(e.ClientId, false);
    }

    public Task StartAsync() => _server.StartAsync();

    Task UpdateClients(string clientId, bool connected)
    {
        string[] snapshot;
        lock (_clients)
        {
            if (connected) _clients.Add(clientId);
            else _clients.Remove(clientId);
            snapshot = _clients.OrderBy(c => c).ToArray();
        }
        ClientsChanged?.Invoke(snapshot);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }
}
