using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ConveyorTwin.Twin.OpcUa;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ConveyorTwin.Spikes
{
    /// <summary>
    /// 2단계 1일 차 OPC UA 스파이크 클라이언트.
    /// src/Spikes/OpcUaSpikeServer 에 보안 정책별로 접속해 읽기·구독을 해보고 결과를 로그에 남긴 뒤 종료한다.
    /// 확인 대상: Mono 플레이어에서 (1) 인증서 자체 생성 (2) 보안 채널 암호화 (3) 구독 알림 수신.
    /// 결과 줄은 "[OpcUaSpike]"로 시작하며, 모든 시나리오 성공 시 종료 코드 0.
    /// </summary>
    public sealed class OpcUaSpikeClient : MonoBehaviour
    {
        const string ServerUrl = "opc.tcp://127.0.0.1:4840";
        const string Ns = "urn:conveyortwin:spike";
        const int SubscribeSeconds = 3;

        // (보안 모드, 정책) — 서버가 여는 엔드포인트 전부
        static readonly (MessageSecurityMode Mode, string Policy)[] Cases =
        {
            (MessageSecurityMode.None, SecurityPolicies.None),
            (MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            (MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes128_Sha256_RsaOaep),
            (MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes256_Sha256_RsaPss),
        };

        volatile int _exitCode = -1;
        static bool CertFailed;

        void Start()
        {
            Log($"시작: Unity {Application.unityVersion}, {(Application.isEditor ? "에디터" : "플레이어")}, " +
                $"런타임 {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Task.Run(RunAsync);
        }

        void Update()
        {
            if (_exitCode < 0) return;
            if (Application.isEditor) { Debug.Log("[OpcUaSpike] 에디터에서는 종료하지 않음"); enabled = false; return; }
            Application.Quit(_exitCode);
        }

        async Task RunAsync()
        {
            int failures = 0;
            try
            {
                var telemetry = DefaultTelemetry.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new UnityLoggerProvider()));
                await ProbeSocketsAsync();
                MonoTcpTransport.Install();   // Mono 소켓 문제 우회 (MonoTcpTransport 참고)
                var config = await CreateConfigAsync(telemetry);
                if (CertFailed) failures++;

                var endpoints = await GetEndpointsAsync(config);
                Log($"엔드포인트 {endpoints.Count}개 발견");

                foreach (var c in Cases)
                {
                    var policyName = c.Policy.Split('#').Last();
                    var ep = endpoints.FirstOrDefault(e => e.SecurityMode == c.Mode && e.SecurityPolicyUri == c.Policy);
                    if (ep == null) { Log($"FAIL {policyName}: 서버에 엔드포인트 없음"); failures++; continue; }
                    try
                    {
                        var r = await RunCaseAsync(config, telemetry, ep);
                        Log($"PASS {c.Mode}/{policyName}: {r}");
                    }
                    catch (Exception e)
                    {
                        failures++;
                        Log($"FAIL {c.Mode}/{policyName}: {Describe(e)}");
                    }
                }
            }
            catch (Exception e)
            {
                failures++;
                Log($"FAIL 준비 단계: {Describe(e)}");
            }
            Log($"RESULT {(failures == 0 ? "ALL PASS" : $"{failures} FAIL")}");
            _exitCode = failures == 0 ? 0 : 1;
        }

        static async Task<ApplicationConfiguration> CreateConfigAsync(ITelemetryContext telemetry)
        {
            var pkiRoot = Path.Combine(Application.persistentDataPath, "pki");
            var app = new ApplicationInstance(telemetry)
            {
                ApplicationName = "ConveyorTwin OPC UA Spike Client",
                ApplicationType = ApplicationType.Client,
            };
#pragma warning disable CS0618 // 스파이크에서는 주체 이름만 주는 간단한 오버로드를 쓴다
            var config = await app.Build("urn:localhost:ConveyorTwin:SpikeClient", "uri:conveyortwin:spike")
                .AsClient()
                .AddSecurityConfiguration("CN=ConveyorTwin Spike Client, O=ConveyorTwin", pkiRoot)
                .SetAutoAcceptUntrustedCertificates(true)   // 스파이크 전용
                .CreateAsync();
#pragma warning restore CS0618

            // 인스턴스 인증서를 저장소에서 찾고, 없으면 자체 서명 인증서를 만든다.
            // Mono는 CertificateRequest 기반 생성을 지원하지 않으므로, 실패하면 스파이크 서버의
            // provision-client 모드로 미리 만들어 둔 인증서가 있어야 보안 정책을 쓸 수 있다.
            var t0 = DateTime.UtcNow;
            try
            {
                if (!await app.CheckApplicationInstanceCertificatesAsync(false))
                    throw new Exception("CheckApplicationInstanceCertificatesAsync = false");
                var cert = config.SecurityConfiguration.ApplicationCertificate.Certificate;
                Log($"인증서 OK ({(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms): {cert?.Subject}, 개인키 {cert?.HasPrivateKey}, 저장소 {pkiRoot}");
            }
            catch (Exception e)
            {
                CertFailed = true;
                Log($"FAIL 인증서 준비: {Describe(e)} (저장소 {pkiRoot})");
            }
            return config;
        }

        static async Task<EndpointDescriptionCollection> GetEndpointsAsync(ApplicationConfiguration config)
        {
            var epConfig = EndpointConfiguration.Create(config);
            epConfig.OperationTimeout = 5000;
            using (var client = await DiscoveryClient.CreateAsync(config, new Uri(ServerUrl), epConfig))
                return await client.GetEndpointsAsync(null);
        }

        static async Task<string> RunCaseAsync(ApplicationConfiguration config, ITelemetryContext telemetry, EndpointDescription ep)
        {
            // 서버가 알려준 URL의 호스트를 접속한 주소로 맞춘다 (localhost/127.0.0.1 혼동 방지)
            ep.EndpointUrl = new UriBuilder(ep.EndpointUrl) { Host = new Uri(ServerUrl).Host }.ToString();
            var endpoint = new ConfiguredEndpoint(null, ep, EndpointConfiguration.Create(config));

            var t0 = DateTime.UtcNow;
            var session = await new DefaultSessionFactory(telemetry).CreateAsync(
                config, (ITransportWaitingConnection)null, endpoint, false, false, "OpcUaSpike", 30000, new UserIdentity(), null, CancellationToken.None);
            var connectMs = (DateTime.UtcNow - t0).TotalMilliseconds;
            try
            {
                ushort ns = (ushort)session.NamespaceUris.GetIndex(Ns);
                var counter = await session.ReadValueAsync(new NodeId("RobotSpike.Counter", ns));
                if (StatusCode.IsBad(counter.StatusCode)) throw new Exception($"Counter 읽기 실패: {counter.StatusCode}");

                // Joints 구독: 50ms 갱신을 100ms 발행 주기로 받는다
                int notifications = 0;
                double[] last = null;
                DateTime lastSource = default;
                var sub = new Subscription(session.DefaultSubscription) { PublishingInterval = 100, KeepAliveCount = 10 };
                var item = new MonitoredItem(sub.DefaultItem)
                {
                    StartNodeId = new NodeId("RobotSpike.Joints", ns),
                    AttributeId = Attributes.Value,
                    SamplingInterval = 50,
                    QueueSize = 10,
                };
                item.Notification += (mi, e) =>
                {
                    if (e.NotificationValue is MonitoredItemNotification n)
                    {
                        Interlocked.Increment(ref notifications);
                        last = n.Value.Value as double[];
                        lastSource = n.Value.SourceTimestamp;
                    }
                };
                sub.AddItem(item);
                session.AddSubscription(sub);
                await sub.CreateAsync();
                await Task.Delay(SubscribeSeconds * 1000);
                await sub.DeleteAsync(true);

                if (notifications == 0) throw new Exception("구독 알림 0건");
                var lagMs = (DateTime.UtcNow - lastSource).TotalMilliseconds;
                return $"접속 {connectMs:F0}ms, Counter={counter.Value}, 알림 {notifications}건/{SubscribeSeconds}s, " +
                       $"Joints[0]={last?[0]:F2}, 마지막 SourceTimestamp 경과 {lagMs:F0}ms";
            }
            finally
            {
                await session.CloseAsync();
                session.Dispose();
            }
        }

        /// <summary>
        /// 스택의 TcpMessageSocket.ConnectAsync와 같은 방식(주소 체계 미지정 소켓 + DnsEndPoint)으로
        /// Mono가 어느 호스트 표기에 접속할 수 있는지 확인한다.
        /// </summary>
        static async Task ProbeSocketsAsync()
        {
            var u = new Uri(ServerUrl);
            Log($"Uri.IdnHost='{u.IdnHost}', DnsSafeHost='{u.DnsSafeHost}', HostNameType={u.HostNameType}");
            using (var s = new Socket(SocketType.Stream, ProtocolType.Tcp))
                Log($"주소 체계 미지정 소켓: AddressFamily={s.AddressFamily}, DualMode={(s.AddressFamily == AddressFamily.InterNetworkV6 ? s.DualMode.ToString() : "n/a")}");
            foreach (var host in new[] { "127.0.0.1", "localhost", "::1", "[::1]" })
            {
                try
                {
                    using (var s = new Socket(SocketType.Stream, ProtocolType.Tcp))
                    {
                        var connect = s.ConnectAsync(new DnsEndPoint(host, u.Port));
                        if (await Task.WhenAny(connect, Task.Delay(2000)) != connect) throw new TimeoutException();
                        await connect;
                        Log($"소켓 접속 OK: DnsEndPoint('{host}') → {s.RemoteEndPoint}");
                    }
                }
                catch (Exception e)
                {
                    Log($"소켓 접속 실패: DnsEndPoint('{host}') → {e.GetType().Name}: {e.Message}");
                }
            }
        }

        static string Describe(Exception e)
        {
            var inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;
            var where = inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            return inner == e
                ? $"{e.GetType().Name}: {e.Message} @ {where}"
                : $"{e.GetType().Name}: {e.Message} ← {inner.GetType().Name}: {inner.Message} @ {where}";
        }

        static void Log(string msg) => Debug.Log("[OpcUaSpike] " + msg);

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
                    var msg = $"[OpcUa:{logLevel}] {_category}: {formatter(state, exception)}{(exception != null ? " / " + exception.Message : "")}";
                    if (logLevel >= LogLevel.Error) Debug.LogWarning(msg); else Debug.Log(msg);
                }
            }
        }
    }
}
