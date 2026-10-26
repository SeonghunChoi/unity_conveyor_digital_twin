using System.IO;
using System.Security.Cryptography.X509Certificates;
using ConveyorTwin.Shared;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace ConveyorTwin.DummyServer.OpcUa;

/// <summary>
/// 가상 로봇 컨트롤러의 OPC UA 서버 (2단계). 컨베이어는 MQTT, 로봇은 OPC UA — 실제 현장처럼 두 프로토콜이 섞인 구성.
///
/// - 엔드포인트 opc.tcp://127.0.0.1:4840. 보안 정책: None(UaExpert 등 점검용) + SignAndEncrypt(Basic256Sha256 등).
///   트윈은 Basic256Sha256을 쓴다 (Mono 제약, docs/opcua-spike.md).
/// - 인증서 신뢰는 목록으로 관리한다 (자동 수락 끔). 트윈 인증서는 이 서버가 발급하면서 신뢰 목록에 넣는다
///   (<see cref="CertificateProvisioning"/>, 현장의 GDS 역할을 축소한 형태).
/// - 사용자 인증은 익명. 트윈은 읽기만 하므로 쓰기 가능한 노드가 없다.
/// </summary>
public sealed class RobotOpcUaServer : IAsyncDisposable
{
    public const int Port = 4840;

    /// <summary>서버 PKI 저장소. 단일 exe 배포 폴더가 아니라 사용자 데이터 폴더에 둔다</summary>
    public static string PkiRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConveyorTwin", "DummyServer", "pki");

    internal static readonly ITelemetryContext Telemetry =
        DefaultTelemetry.Create(b => b.SetMinimumLevel(LogLevel.Warning));

    readonly RobotNodeManagerFactory _factory = new();
    ApplicationInstance? _app;
    StandardServer? _server;
    ApplicationConfiguration? _config;

    /// <summary>활성 세션 이름 목록이 바뀔 때 (임의 스레드에서 호출)</summary>
    public event Action<IReadOnlyCollection<string>>? SessionsChanged;

    public async Task StartAsync()
    {
        (_app, _config) = await CreateServerApplicationAsync();
        _config.CertificateValidator.CertificateValidation += OnCertificateValidation;

        _server = new StandardServer();
        _server.AddNodeManager(_factory);
        await _app.StartAsync(_server);

        var sm = _server.CurrentInstance.SessionManager;
        sm.SessionActivated += (_, _) => RaiseSessions();
        sm.SessionClosing += (_, _) => RaiseSessions();
    }

    /// <summary>서버 애플리케이션 설정과 인스턴스 인증서 (없으면 만든다). 발급 모드에서도 같은 것을 쓴다</summary>
    internal static async Task<(ApplicationInstance, ApplicationConfiguration)> CreateServerApplicationAsync()
    {
        var app = new ApplicationInstance(Telemetry)
        {
            ApplicationName = OpcUaEndpoint.ServerApplicationName,
            ApplicationType = ApplicationType.Server,
        };
#pragma warning disable CS0618 // 주체 이름 + PKI 루트만 주는 오버로드 (저장소 경로를 own/trusted/issuer/rejected로 만든다)
        var config = await app.Build(OpcUaEndpoint.ServerApplicationUri, OpcUaEndpoint.ProductUri)
            .AsServer(new[] { OpcUaEndpoint.Url })
            .AddUnsecurePolicyNone()
            .AddSignAndEncryptPolicies()
            .AddUserTokenPolicy(UserTokenType.Anonymous)
            .AddSecurityConfiguration(OpcUaEndpoint.ServerSubject, PkiRoot)
            .SetAutoAcceptUntrustedCertificates(false)
            .CreateAsync();
#pragma warning restore CS0618
        if (!await app.CheckApplicationInstanceCertificatesAsync(false))
            throw new InvalidOperationException("OPC UA 서버 인스턴스 인증서를 준비하지 못했습니다.");
        return (app, config);
    }

    /// <summary>
    /// 신뢰 목록은 서버 시작 때 읽혀 캐시되므로, 서버가 실행 중일 때 발급한 트윈 인증서는 바로 반영되지 않는다.
    /// 그래서 "신뢰되지 않음"이면 디스크의 신뢰 목록 폴더를 다시 확인해 그 안에 있으면 받아들인다.
    /// </summary>
    void OnCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs e)
    {
        if (e.Error.StatusCode != StatusCodes.BadCertificateUntrusted) return;
        e.Accept = CertificateProvisioning.IsInTrustedFolder(Path.Combine(PkiRoot, "trusted"), e.Certificate);
    }

    /// <summary>로봇 상태를 주소 공간에 반영 (시뮬레이션 스레드에서 호출)</summary>
    public void Publish(RobotSnapshot snapshot) => _factory.Instance?.Publish(snapshot, DateTime.UtcNow);

    void RaiseSessions()
    {
        var sm = _server?.CurrentInstance?.SessionManager;
        if (sm == null) return;
        var names = sm.GetSessions().Select(s => s.SessionDiagnostics.SessionName).OrderBy(n => n).ToArray();
        SessionsChanged?.Invoke(names);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server != null)
        {
            await _server.StopAsync();
            _server.Dispose();
        }
    }
}

/// <summary>
/// 트윈 클라이언트 인증서 발급 (`ConveyorTwin.DummyServer.exe --provision-twin-cert &lt;트윈 pki&gt;`).
/// Unity(Mono)는 자체 서명 인증서를 만들지 못하므로(docs/opcua-spike.md) .NET인 서버가 대신 만든다.
///  1. 트윈과 같은 신원(Shared.OpcUaEndpoint)으로 인증서를 만들어 트윈 pki/own에 넣는다 (이미 있으면 그대로)
///  2. 서버 인증서를 트윈 pki/trusted에, 트윈 인증서를 서버 pki/trusted에 넣어 서로 신뢰하게 한다
/// </summary>
public static class CertificateProvisioning
{
    public static async Task<string> ProvisionTwinAsync(string twinPkiRoot)
    {
        var (_, serverConfig) = await RobotOpcUaServer.CreateServerApplicationAsync();
        var serverCert = serverConfig.SecurityConfiguration.ApplicationCertificate.Certificate
            ?? throw new InvalidOperationException("서버 인증서가 없습니다.");

        var twin = new ApplicationInstance(RobotOpcUaServer.Telemetry)
        {
            ApplicationName = OpcUaEndpoint.TwinApplicationName,
            ApplicationType = ApplicationType.Client,
        };
#pragma warning disable CS0618
        var twinConfig = await twin.Build(OpcUaEndpoint.TwinApplicationUri, OpcUaEndpoint.ProductUri)
            .AsClient()
            .AddSecurityConfiguration(OpcUaEndpoint.TwinSubject, twinPkiRoot)
            .SetAutoAcceptUntrustedCertificates(false)
            .CreateAsync();
#pragma warning restore CS0618
        if (!await twin.CheckApplicationInstanceCertificatesAsync(false))
            throw new InvalidOperationException("트윈 인증서를 만들지 못했습니다.");
        var twinCert = twinConfig.SecurityConfiguration.ApplicationCertificate.Certificate
            ?? throw new InvalidOperationException("트윈 인증서가 없습니다.");

        WriteTrusted(Path.Combine(twinPkiRoot, "trusted"), serverCert);
        WriteTrusted(Path.Combine(RobotOpcUaServer.PkiRoot, "trusted"), twinCert);
        return $"트윈 인증서 {twinCert.Subject} [{twinCert.Thumbprint}] → {twinPkiRoot}";
    }

    /// <summary>DirectoryCertificateStore 형식: &lt;store&gt;/certs/*.der</summary>
    static void WriteTrusted(string storeRoot, X509Certificate2 cert)
    {
        var dir = Path.Combine(storeRoot, "certs");
        Directory.CreateDirectory(dir);
        var name = cert.GetNameInfo(X509NameType.SimpleName, false);
        File.WriteAllBytes(Path.Combine(dir, $"{name} [{cert.Thumbprint}].der"), cert.RawData);
    }

    public static bool IsInTrustedFolder(string storeRoot, X509Certificate2 cert)
    {
        var dir = Path.Combine(storeRoot, "certs");
        return Directory.Exists(dir) &&
               Directory.EnumerateFiles(dir, "*.der").Any(f => f.Contains(cert.Thumbprint, StringComparison.OrdinalIgnoreCase));
    }
}
