using System.Diagnostics;
using ConveyorTwin.Shared;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

// 로봇 OPC UA 서버 점검.
//   dotnet run --project src/Tools/OpcUaProbe -- <트윈 pki 경로> [보안 정책: Basic256Sha256(기본) | None]
// 더미 서버가 실행 중이어야 한다. pki는 서버의 --provision-twin-cert로 발급받은 트윈 저장소.
// 1) 접속 2) RobotCell01 아래 주소 공간 탐색·값 출력 3) JointPositions·ExecutionState 3초 구독
// 종료 코드 0 = 성공.

//   dotnet run --project src/Tools/OpcUaProbe -- <트윈 pki 경로> watch <초>
// watch: 로봇 신호(OPC UA)와 컨베이어 출구 센서(MQTT)를 함께 기록해 사이클·인계 순서·관절 속도를 분석한다 (4일 차).

if (args.Length < 1)
{
    Console.WriteLine("사용법: OpcUaProbe <트윈 pki 경로> [Basic256Sha256|None] | <트윈 pki 경로> watch <초>");
    return 2;
}
var pkiRoot = Path.GetFullPath(args[0]);
int watchSeconds = args.Length > 2 && args[1] == "watch" ? int.Parse(args[2]) : 0;
var policy = args.Length > 1 && args[1] == "None" ? SecurityPolicies.None : OpcUaEndpoint.SecurityPolicy;
var mode = policy == SecurityPolicies.None ? MessageSecurityMode.None : MessageSecurityMode.SignAndEncrypt;

var telemetry = DefaultTelemetry.Create(b => b.SetMinimumLevel(LogLevel.Warning));
var app = new ApplicationInstance(telemetry) { ApplicationName = OpcUaEndpoint.TwinApplicationName, ApplicationType = ApplicationType.Client };
var config = await app.Build(OpcUaEndpoint.TwinApplicationUri, OpcUaEndpoint.ProductUri)
    .AsClient()
    .AddSecurityConfiguration(OpcUaEndpoint.TwinSubject, pkiRoot)
    .SetAutoAcceptUntrustedCertificates(false)      // 서버 인증서는 발급 때 넣은 신뢰 목록으로만 신뢰
    .CreateAsync();
await app.CheckApplicationInstanceCertificatesAsync(false);
var myCert = config.SecurityConfiguration.ApplicationCertificate.Certificate;
Console.WriteLine($"[Probe] 클라이언트 인증서: {myCert?.Subject} [{myCert?.Thumbprint}]");

// 엔드포인트 선택
var epConfig = EndpointConfiguration.Create(config);
EndpointDescriptionCollection endpoints;
using (var dc = await DiscoveryClient.CreateAsync(config, new Uri(OpcUaEndpoint.Url), epConfig))
    endpoints = await dc.GetEndpointsAsync(null);
Console.WriteLine($"[Probe] 엔드포인트 {endpoints.Count}개: " +
    string.Join(", ", endpoints.Select(e => $"{e.SecurityMode}/{e.SecurityPolicyUri.Split('#').Last()}")));
var ep = endpoints.First(e => e.SecurityPolicyUri == policy && e.SecurityMode == mode);

var sw = Stopwatch.StartNew();
ISession session;
try
{
    session = await new DefaultSessionFactory(telemetry).CreateAsync(config, (ITransportWaitingConnection)null,
        new ConfiguredEndpoint(null, ep, epConfig), false, false, "OpcUaProbe", 30000, new UserIdentity(), null, CancellationToken.None);
}
catch (ServiceResultException e)
{
    Console.WriteLine($"[Probe] 접속 거부: {e.StatusCode} {StatusCodes.GetBrowseName(e.StatusCode)} — {e.Message}");
    return 1;
}
Console.WriteLine($"[Probe] 접속 OK ({mode}/{policy.Split('#').Last()}, {sw.ElapsedMilliseconds}ms)");

try
{
    var ns = (ushort)session.NamespaceUris.GetIndex(RobotNodes.NamespaceUri);
    Console.WriteLine($"[Probe] 네임스페이스 {RobotNodes.NamespaceUri} = ns={ns}");
    if (watchSeconds > 0)
        return await Watch.RunAsync(session, ns, watchSeconds);

    // ---- 주소 공간 탐색 ----
    int variables = 0;
    async Task BrowseAsync(NodeId node, string indent)
    {
        var browser = new Browser(session)
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
        };
        foreach (var r in await browser.BrowseAsync(node))
        {
            var id = ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris);
            if (r.NodeClass == NodeClass.Variable)
            {
                variables++;
                var dv = await session.ReadValueAsync(id);
                var v = dv.Value is double[] arr ? "[" + string.Join(", ", arr.Select(x => x.ToString("F1"))) + "]" : dv.Value;
                Console.WriteLine($"{indent}{r.DisplayName} = {v}  ({dv.StatusCode}, {dv.SourceTimestamp:HH:mm:ss.fff})");
            }
            else
            {
                Console.WriteLine($"{indent}{r.DisplayName}/");
                await BrowseAsync(id, indent + "  ");
            }
        }
    }
    var root = new NodeId(RobotNodes.System, ns);
    Console.WriteLine("[Probe] 주소 공간");
    Console.WriteLine("  RobotCell01/");
    await BrowseAsync(root, "    ");
    Console.WriteLine($"[Probe] 변수 {variables}개");

    // Shared에 정의한 노드가 모두 있는지
    var expected = new[]
    {
        RobotNodes.JointPositions, RobotNodes.MotorsOn, RobotNodes.SpeedOverride, RobotNodes.TaskProgramName,
        RobotNodes.ExecutionState, RobotNodes.CycleCount, RobotNodes.OperationalMode, RobotNodes.EmergencyStop,
        RobotNodes.ProtectiveStop, RobotNodes.VacuumOn, RobotNodes.VacuumOk, RobotNodes.Manufacturer, RobotNodes.Model,
    }.Concat(Enumerable.Range(1, 6).Select(RobotNodes.AxisActualPosition)).ToArray();
    int missing = 0;
    foreach (var id in expected)
    {
        var dv = await session.ReadValueAsync(new NodeId(id, ns));
        if (StatusCode.IsBad(dv.StatusCode)) { missing++; Console.WriteLine($"[Probe] 없음: {id} ({dv.StatusCode})"); }
    }
    Console.WriteLine($"[Probe] Shared.RobotNodes 노드 {expected.Length}개 중 누락 {missing}개");

    // ---- 구독 ----
    int jointNotes = 0, execNotes = 0;
    double lastLagMs = 0;
    var sub = new Subscription(session.DefaultSubscription) { PublishingInterval = 100, KeepAliveCount = 10 };
    MonitoredItem Item(string id, int samplingMs) => new MonitoredItem(sub.DefaultItem)
    {
        StartNodeId = new NodeId(id, ns), AttributeId = Attributes.Value, SamplingInterval = samplingMs, QueueSize = 10,
    };
    var joints = Item(RobotNodes.JointPositions, 50);
    joints.Notification += (_, e) =>
    {
        if (e.NotificationValue is MonitoredItemNotification n)
        {
            Interlocked.Increment(ref jointNotes);
            lastLagMs = (DateTime.UtcNow - n.Value.SourceTimestamp).TotalMilliseconds;
        }
    };
    var exec = Item(RobotNodes.ExecutionState, 100);
    exec.Notification += (_, _) => Interlocked.Increment(ref execNotes);
    sub.AddItems(new[] { joints, exec });
    session.AddSubscription(sub);
    await sub.CreateAsync();
    await Task.Delay(3000);
    await sub.DeleteAsync(true);
    Console.WriteLine($"[Probe] 구독 3초: JointPositions 알림 {jointNotes}건 (마지막 값 지연 {lastLagMs:F0}ms), " +
                      $"ExecutionState 알림 {execNotes}건 (값이 안 바뀌면 최초 1건)");
    return missing == 0 && jointNotes > 0 ? 0 : 1;
}
finally
{
    await session.CloseAsync();
    session.Dispose();
}
