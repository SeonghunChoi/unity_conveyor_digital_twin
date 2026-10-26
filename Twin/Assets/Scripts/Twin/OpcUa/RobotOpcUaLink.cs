using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConveyorTwin.Shared;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ConveyorTwin.Twin.OpcUa
{
    /// <summary>수신한 로봇 노드 값 한 건 (백그라운드 스레드 → 메인 스레드 전달용)</summary>
    public readonly struct RobotSample
    {
        /// <summary>Shared.RobotNodes의 문자열 ID</summary>
        public readonly string Node;
        public readonly object Value;
        /// <summary>서버가 값을 만든 시각 (OPC UA SourceTimestamp, UTC)</summary>
        public readonly DateTime SourceUtc;
        /// <summary>OPC UA StatusCode (Good = 0)</summary>
        public readonly uint Status;
        public readonly long ReceivedMs;

        public RobotSample(string node, object value, DateTime sourceUtc, uint status, long receivedMs)
        {
            Node = node;
            Value = value;
            SourceUtc = sourceUtc;
            Status = status;
            ReceivedMs = receivedMs;
        }
    }

    public enum RobotLinkState
    {
        Starting,
        Provisioning,   // 서버 exe로 인증서 발급 중
        Connecting,
        Connected,
        Reconnecting,   // 세션이 끊겨 다시 접속 시도 중
        Failed,         // 인증서를 준비하지 못함 (서버 exe 없음 등) — 일정 간격으로 다시 시도
    }

    /// <summary>
    /// 로봇 OPC UA 연결 (2단계 5일 차, docs/robot-opcua.md). MqttLink와 같은 역할 분담:
    /// 스택 콜백은 스레드 풀에서 오므로 값은 큐에만 넣고, 검증(RobotGate)과 씬 반영은 메인 스레드에서 한다.
    ///
    /// - 보안: SignAndEncrypt + Basic256Sha256, 자동 수락 끔. 서로의 인증서는 신뢰 목록으로만 믿는다.
    /// - 인증서: Unity(Mono)는 직접 만들지 못하므로 없으면 서버 exe(--provision-twin-cert)로 발급받는다.
    ///   서버가 인증서를 새로 만들어 신뢰가 깨진 경우(BadCertificateUntrusted 등)에도 한 번 다시 발급받는다.
    /// - 전송: Mono 소켓 문제 때문에 opc.tcp 바인딩을 MonoTcpTransport로 바꾼다 (docs/opcua-spike.md).
    /// - 끊김 감지: 값은 바뀔 때만 오므로 값 주기가 아니라 세션 KeepAlive(1초)로 판단한다. 끊기면 2초 간격으로 새 세션을 만든다.
    /// - 서버 재시작 감지: 접속 때 Server.ServerStatus.StartTime을 읽어 둔다 (MQTT의 sid 역할).
    /// </summary>
    public sealed class RobotOpcUaLink : IDisposable
    {
        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
        static readonly TimeSpan FailedRetryInterval = TimeSpan.FromSeconds(10);
        const int KeepAliveMs = 1000;
        const int SessionTimeoutMs = 10000;
        const int ProvisionTimeoutMs = 20000;

        /// <summary>구독할 노드: (ID, 샘플링 ms, 큐 크기). 관절각은 서버 갱신 주기(20ms)로, 나머지는 바뀔 때마다</summary>
        static readonly (string Id, int SamplingMs, uint Queue)[] Subscribed =
        {
            (RobotNodes.JointPositions, 20, 20),
            (RobotNodes.MotorsOn, 0, 10),
            (RobotNodes.SpeedOverride, 0, 10),
            (RobotNodes.ExecutionState, 0, 10),
            (RobotNodes.CycleCount, 0, 10),
            (RobotNodes.OperationalMode, 0, 10),
            (RobotNodes.EmergencyStop, 0, 10),
            (RobotNodes.ProtectiveStop, 0, 10),
            (RobotNodes.VacuumOn, 0, 10),
            (RobotNodes.VacuumOk, 0, 10),
            (RobotNodes.PalletPresent, 0, 10),
        };

        static int _transportInstalled;

        readonly string _pkiRoot;
        readonly Func<string> _findServerExe;
        readonly ConcurrentQueue<RobotSample> _queue = new ConcurrentQueue<RobotSample>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly ITelemetryContext _telemetry;

        ApplicationConfiguration _config;
        ISession _session;
        volatile bool _sessionLost;
        bool _reprovisioned;

        volatile RobotLinkState _state = RobotLinkState.Starting;
        public RobotLinkState State => _state;
        public bool IsConnected => _state == RobotLinkState.Connected;
        public string LastError { get; private set; }
        public int ConnectCount { get; private set; }
        /// <summary>서버 시작 시각 (UTC). 바뀌면 서버가 재시작된 것</summary>
        public DateTime ServerStartUtc { get; private set; }
        public DateTime LastKeepAliveUtc { get; private set; }
        public string ProvisionLog { get; private set; }

        /// <param name="pkiRoot">트윈 PKI 저장소 (Application.persistentDataPath/pki)</param>
        /// <param name="findServerExe">인증서 발급에 쓸 서버 exe 경로를 찾는다 (ServerLauncher.FindServerExe)</param>
        public RobotOpcUaLink(string pkiRoot, Func<string> findServerExe)
        {
            _pkiRoot = pkiRoot;
            _findServerExe = findServerExe;
            _telemetry = DefaultTelemetry.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new UnityLoggerProvider()));
        }

        public void Start() => Task.Run(() => RunAsync(_cts.Token));

        public bool TryDequeue(out RobotSample sample) => _queue.TryDequeue(out sample);

        async Task RunAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _transportInstalled, 1) == 0)
                MonoTcpTransport.Install();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_config == null)
                        _config = await PrepareConfigAsync(ct);
                    _state = ConnectCount == 0 ? RobotLinkState.Connecting : RobotLinkState.Reconnecting;
                    await ConnectAsync(ct);
                    ConnectCount++;
                    LastError = null;
                    _reprovisioned = false;
                    _state = RobotLinkState.Connected;

                    while (!_sessionLost && !ct.IsCancellationRequested)
                        await Task.Delay(100, ct);
                    _state = RobotLinkState.Reconnecting;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ServiceResultException e) when (IsTrustError(e.StatusCode) && !_reprovisioned)
                {
                    // 서버 인증서가 바뀌었거나(서버 PKI 삭제) 서버가 트윈 인증서를 모름 → 한 번 다시 발급받는다
                    LastError = $"인증서 신뢰 실패 ({StatusCodes.GetBrowseName(e.StatusCode)}) → 재발급";
                    _reprovisioned = true;
                    _config = null;
                    await ProvisionAsync(ct);
                }
                catch (Exception e)
                {
                    LastError = e.GetBaseException().Message;
                    if (_state != RobotLinkState.Failed)
                        _state = ConnectCount == 0 ? RobotLinkState.Connecting : RobotLinkState.Reconnecting;
                }
                finally
                {
                    await CloseSessionAsync();
                }

                try { await Task.Delay(_state == RobotLinkState.Failed ? FailedRetryInterval : RetryInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        static bool IsTrustError(StatusCode code) =>
            code == StatusCodes.BadSecurityChecksFailed || code == StatusCodes.BadCertificateUntrusted ||
            code == StatusCodes.BadCertificateInvalid || code == StatusCodes.BadCertificateUriInvalid;

        // ---------- 인증서·설정 ----------

        async Task<ApplicationConfiguration> PrepareConfigAsync(CancellationToken ct)
        {
            if (!HasOwnCertificate())
                await ProvisionAsync(ct);
            if (!HasOwnCertificate())
            {
                _state = RobotLinkState.Failed;
                throw new InvalidOperationException(LastError ?? "트윈 인증서가 없습니다.");
            }

            var app = new ApplicationInstance(_telemetry)
            {
                ApplicationName = OpcUaEndpoint.TwinApplicationName,
                ApplicationType = ApplicationType.Client,
            };
#pragma warning disable CS0618 // 주체 이름 + PKI 루트 오버로드 (서버의 발급 모드와 같은 저장소 구조)
            var config = await app.Build(OpcUaEndpoint.TwinApplicationUri, OpcUaEndpoint.ProductUri)
                .AsClient()
                .AddSecurityConfiguration(OpcUaEndpoint.TwinSubject, _pkiRoot)
                .SetAutoAcceptUntrustedCertificates(false)
                .CreateAsync(ct);
#pragma warning restore CS0618
            // 이미 있는 인증서를 읽기만 한다 (없으면 만들려다 Mono에서 PlatformNotSupportedException)
            if (!await app.CheckApplicationInstanceCertificatesAsync(false))
                throw new InvalidOperationException("트윈 인증서를 읽지 못했습니다.");
            return config;
        }

        bool HasOwnCertificate()
        {
            var own = Path.Combine(_pkiRoot, "own");
            var subject = OpcUaEndpoint.TwinApplicationName;   // 파일 이름 = 주체의 CN
            return Directory.Exists(Path.Combine(own, "private")) &&
                   Directory.EnumerateFiles(Path.Combine(own, "private"), subject + " [*].pfx").Any();
        }

        /// <summary>서버 exe를 발급 모드로 실행 (창 없이 실행되고 종료 코드로 결과를 알린다)</summary>
        async Task ProvisionAsync(CancellationToken ct)
        {
            _state = RobotLinkState.Provisioning;
            var exe = _findServerExe();
            if (exe == null)
            {
                _state = RobotLinkState.Failed;
                LastError = "서버 exe를 찾지 못해 OPC UA 인증서를 발급받을 수 없습니다.";
                return;
            }
            var psi = new ProcessStartInfo(exe, $"{OpcUaEndpoint.ProvisionArg} \"{_pkiRoot}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            using (var p = Process.Start(psi))
            {
                var waited = await Task.Run(() => p.WaitForExit(ProvisionTimeoutMs), ct);
                var logPath = Path.Combine(_pkiRoot, "provision.log");
                ProvisionLog = File.Exists(logPath) ? File.ReadLines(logPath).LastOrDefault() : null;
                if (!waited || p.ExitCode != 0)
                {
                    _state = RobotLinkState.Failed;
                    LastError = $"인증서 발급 실패 (종료 코드 {(waited ? p.ExitCode.ToString() : "시간 초과")}): {ProvisionLog}";
                }
            }
        }

        // ---------- 세션·구독 ----------

        async Task ConnectAsync(CancellationToken ct)
        {
            var epConfig = EndpointConfiguration.Create(_config);
            epConfig.OperationTimeout = 5000;
            EndpointDescriptionCollection endpoints;
            using (var dc = await DiscoveryClient.CreateAsync(_config, new Uri(OpcUaEndpoint.Url), epConfig, ct: ct))
                endpoints = await dc.GetEndpointsAsync(null, ct);

            var ep = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == OpcUaEndpoint.SecurityPolicy &&
                                                   e.SecurityMode == MessageSecurityMode.SignAndEncrypt)
                     ?? throw new InvalidOperationException("서버에 Basic256Sha256 SignAndEncrypt 엔드포인트가 없습니다.");
            // 서버가 알려준 URL의 호스트를 접속한 주소로 맞춘다
            ep.EndpointUrl = new UriBuilder(ep.EndpointUrl) { Host = new Uri(OpcUaEndpoint.Url).Host }.ToString();

            _sessionLost = false;
            var session = await new DefaultSessionFactory(_telemetry).CreateAsync(
                _config, (ITransportWaitingConnection)null, new ConfiguredEndpoint(null, ep, epConfig),
                false, false, "ConveyorTwin", SessionTimeoutMs, new UserIdentity(), null, ct);
            _session = session;
            session.KeepAliveInterval = KeepAliveMs;
            session.KeepAlive += OnKeepAlive;
            LastKeepAliveUtc = DateTime.UtcNow;

            var start = await session.ReadValueAsync(VariableIds.Server_ServerStatus_StartTime, ct);
            ServerStartUtc = start.Value is DateTime t ? t : DateTime.MinValue;

            ushort ns = (ushort)session.NamespaceUris.GetIndex(RobotNodes.NamespaceUri);
            if (ns == ushort.MaxValue)
                throw new InvalidOperationException($"서버에 네임스페이스 {RobotNodes.NamespaceUri}가 없습니다.");

            var sub = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = 50,
                KeepAliveCount = 10,        // 값이 없어도 0.5초마다 KeepAlive 알림
                LifetimeCount = 60,
                PublishingEnabled = true,
            };
            foreach (var (id, sampling, queue) in Subscribed)
            {
                var item = new MonitoredItem(sub.DefaultItem)
                {
                    StartNodeId = new NodeId(id, ns),
                    AttributeId = Attributes.Value,
                    SamplingInterval = sampling,
                    QueueSize = queue,
                    DiscardOldest = true,
                };
                var nodeId = id;
                item.Notification += (mi, e) =>
                {
                    if (e.NotificationValue is MonitoredItemNotification n)
                        _queue.Enqueue(new RobotSample(nodeId, n.Value.Value, n.Value.SourceTimestamp, n.Value.StatusCode.Code,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                };
                sub.AddItem(item);
            }
            session.AddSubscription(sub);
            await sub.CreateAsync(ct);
        }

        void OnKeepAlive(ISession session, KeepAliveEventArgs e)
        {
            // 서버가 막 시작했을 때는 Running 전 상태가 잠깐 보일 수 있으므로, 통신 오류나 명시적인 정지·고장 상태만 끊김으로 본다
            bool down = e.CurrentState == ServerState.Shutdown || e.CurrentState == ServerState.Failed ||
                        e.CurrentState == ServerState.CommunicationFault;
            if (ServiceResult.IsBad(e.Status) || down)
            {
                LastError = $"KeepAlive 실패: {e.Status?.StatusCode} (서버 상태 {e.CurrentState})";
                _sessionLost = true;
                e.CancelKeepAlive = true;
            }
            else
            {
                LastKeepAliveUtc = DateTime.UtcNow;
            }
        }

        async Task CloseSessionAsync()
        {
            var s = _session;
            _session = null;
            if (s == null) return;
            s.KeepAlive -= OnKeepAlive;
            try
            {
                // 서버가 이미 없으면 Close가 시간 초과까지 기다리므로 짧게 끊는다
                if (!_sessionLost)
                    await Task.WhenAny(s.CloseAsync(), Task.Delay(1000));
            }
            catch (Exception) { /* 끊긴 세션 정리 중 오류는 무시 */ }
            finally
            {
                try { s.Dispose(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            // 플레이 모드 종료(도메인 리로드) 전에 백그라운드 루프와 세션을 반드시 정리한다
            _cts.Cancel();
            try { CloseSessionAsync().Wait(1500); }
            catch (Exception) { }
            _cts.Dispose();
        }

        /// <summary>OPC UA 스택 로그(경고 이상)를 Unity 로그로 넘긴다</summary>
        sealed class UnityLoggerProvider : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new UnityLogger(categoryName);
            public void Dispose() { }

            sealed class UnityLogger : ILogger
            {
                readonly string _category;
                public UnityLogger(string category) => _category = category;
                public IDisposable BeginScope<TState>(TState state) => null;
                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    var msg = formatter(state, exception);
                    // 구독을 지운 직후 이미 보낸 Publish 응답(BadNoSubscription)은 무해하므로 거른다 (docs/opcua-spike.md)
                    if (msg.Contains("BadNoSubscription")) return;
                    UnityEngine.Debug.LogWarning($"[OpcUa:{logLevel}] {_category}: {msg}");
                }
            }
        }
    }
}
