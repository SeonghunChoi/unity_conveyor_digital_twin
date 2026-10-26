using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Bindings;

namespace ConveyorTwin.Twin.OpcUa
{
    /// <summary>
    /// Unity(Mono)용 opc.tcp 전송 바인딩.
    ///
    /// 스택의 TcpMessageSocket.ConnectAsync는 주소 체계를 지정하지 않은 소켓(IPv6 DualMode)으로 DnsEndPoint에 접속하는데,
    /// Mono는 이 조합을 지원하지 않아 호스트 표기와 관계없이 "None of the discovered or specified addresses match the
    /// socket address family"로 실패한다 (1일 차 스파이크 측정).
    /// 여기서는 주소를 먼저 풀고 그 주소 체계로 소켓을 만들어 IPEndPoint로 접속한 뒤,
    /// 연결된 소켓을 받는 TcpMessageSocket 생성자(서버 쪽이 accept 소켓에 쓰는 것)에 넘겨 나머지는 스택에 맡긴다.
    ///
    /// 사용: 세션을 만들기 전에 MonoTcpTransport.Install() 한 번 (RobotOpcUaLink가 호출).
    /// </summary>
    public static class MonoTcpTransport
    {
        public static void Install()
        {
            // ITransportChannelBindings에는 SetBinding이 없지만, 실제 구현(TransportBindingsBase)은 ITransportBindings<T>를 구현한다
            if (!(TransportBindings.Channels is ITransportBindings<ITransportChannelFactory> bindings))
                throw new NotSupportedException($"opc.tcp 바인딩을 교체할 수 없습니다: {TransportBindings.Channels.GetType().FullName}");
            bindings.SetBinding(new ChannelFactory());
        }

        sealed class ChannelFactory : ITransportChannelFactory
        {
            public string UriScheme => Utils.UriSchemeOpcTcp;
            public ITransportChannel Create(ITelemetryContext telemetry)
                => new UaSCUaBinaryTransportChannel(new SocketFactory(telemetry), telemetry);
        }

        sealed class SocketFactory : IMessageSocketFactory
        {
            readonly ITelemetryContext _telemetry;
            public SocketFactory(ITelemetryContext telemetry) => _telemetry = telemetry;
            public string Implementation => "UA-TCP (Mono)";
            public IMessageSocket Create(IMessageSink sink, BufferManager bufferManager, int receiveBufferSize)
                => new MessageSocket(sink, bufferManager, receiveBufferSize, _telemetry);
        }

        /// <summary>접속만 직접 하고, 접속 후의 모든 동작은 스택의 TcpMessageSocket에 위임한다</summary>
        sealed class MessageSocket : IMessageSocket
        {
            readonly BufferManager _bufferManager;
            readonly int _receiveBufferSize;
            readonly ITelemetryContext _telemetry;
            readonly object _lock = new object();
            IMessageSink _sink;
            TcpMessageSocket _inner;
            bool _closed;

            public MessageSocket(IMessageSink sink, BufferManager bufferManager, int receiveBufferSize, ITelemetryContext telemetry)
            {
                _sink = sink;
                _bufferManager = bufferManager;
                _receiveBufferSize = receiveBufferSize;
                _telemetry = telemetry;
            }

            public async Task ConnectAsync(Uri endpointUrl, CancellationToken ct = default)
            {
                if (_inner != null) throw new InvalidOperationException("The socket is already connected.");
                int port = endpointUrl.Port is > 0 and <= ushort.MaxValue ? endpointUrl.Port : Utils.UaTcpDefaultPort;

                // IP 리터럴이면 그대로, 호스트 이름이면 IPv4를 먼저 시도한다 (트윈은 같은 PC의 127.0.0.1에 접속)
                var host = endpointUrl.DnsSafeHost;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false))
                        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();

                Exception last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        LingerState = new LingerOption(true, 5),
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        lock (_lock)
                        {
                            if (_closed) throw new ObjectDisposedException(nameof(MessageSocket));
                            _inner = new TcpMessageSocket(_sink, socket, _bufferManager, _receiveBufferSize, _telemetry);
                        }
                        return;
                    }
                    catch (Exception e)
                    {
                        last = e;
                        socket.Dispose();
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostNotFound);
            }

            public int Handle => _inner?.Handle ?? -1;
            public EndPoint LocalEndpoint => _inner?.LocalEndpoint;
            public EndPoint RemoteEndpoint => _inner?.RemoteEndpoint;
            public TransportChannelFeatures MessageSocketFeatures =>
                TransportChannelFeatures.ReverseConnect | TransportChannelFeatures.Reconnect;

            public void Close()
            {
                lock (_lock) { _closed = true; }
                _inner?.Close();
            }

            public void ReadNextMessage() => _inner?.ReadNextMessage();

            public void ChangeSink(IMessageSink sink)
            {
                lock (_lock) { _sink = sink; }
                _inner?.ChangeSink(sink);
            }

            public bool Send(IMessageSocketAsyncEventArgs args)
                => (_inner ?? throw new InvalidOperationException("The socket is not connected.")).Send(args);

            public IMessageSocketAsyncEventArgs MessageSocketEventArgs()
                => (_inner ?? throw new InvalidOperationException("The socket is not connected.")).MessageSocketEventArgs();

            public void Dispose() => _inner?.Dispose();
        }
    }
}
