using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

// OPC UA 스파이크 서버.
// opc.tcp://127.0.0.1:4840 에 보안 없음(None)과 Basic256Sha256 SignAndEncrypt 엔드포인트를 연다.
// 노드: Objects/RobotSpike/Joints (double[6], 50ms마다 사인파), Objects/RobotSpike/Counter (Int64)
// 사용법: dotnet run --project src/Spikes/OpcUaSpikeServer  (Ctrl+C로 종료)

const string EndpointUrl = "opc.tcp://127.0.0.1:4840";

var telemetry = DefaultTelemetry.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");

// provision-client <pkiRoot>: Unity(Mono)는 자체 서명 인증서를 만들지 못하므로(1일 차 스파이크 결과)
// .NET 8에서 클라이언트와 같은 설정으로 인스턴스 인증서를 만들어 클라이언트 저장소에 넣어 준다.
if (args.Length == 2 && args[0] == "provision-client")
{
    var client = new ApplicationInstance(telemetry)
    {
        ApplicationName = "ConveyorTwin OPC UA Spike Client",
        ApplicationType = ApplicationType.Client,
    };
    var clientConfig = await client.Build("urn:localhost:ConveyorTwin:SpikeClient", "uri:conveyortwin:spike")
        .AsClient()
        .AddSecurityConfiguration("CN=ConveyorTwin Spike Client, O=ConveyorTwin", args[1])
        .SetAutoAcceptUntrustedCertificates(true)
        .CreateAsync();
    var ok = await client.CheckApplicationInstanceCertificatesAsync(false);
    Console.WriteLine($"[Spike] 클라이언트 인증서 {(ok ? "준비됨" : "실패")}: {clientConfig.SecurityConfiguration.ApplicationCertificate.Certificate?.Subject} → {args[1]}");
    return ok ? 0 : 1;
}

var app = new ApplicationInstance(telemetry)
{
    ApplicationName = "ConveyorTwin OPC UA Spike Server",
    ApplicationType = ApplicationType.Server,
};

await app.Build("urn:localhost:ConveyorTwin:SpikeServer", "uri:conveyortwin:spike")
    .AsServer(new[] { EndpointUrl })
    .AddUnsecurePolicyNone()
    .AddSignAndEncryptPolicies()
    .AddUserTokenPolicy(UserTokenType.Anonymous)
    .AddSecurityConfiguration("CN=ConveyorTwin Spike Server, O=ConveyorTwin", pkiRoot)
    .SetAutoAcceptUntrustedCertificates(true)   // 스파이크 전용. 실제 서버는 신뢰 목록으로 관리한다
    .CreateAsync();

if (!await app.CheckApplicationInstanceCertificatesAsync(false))
    throw new Exception("서버 인스턴스 인증서를 만들지 못했습니다.");

var server = new StandardServer();
server.AddNodeManager(new SpikeNodeManagerFactory());
await app.StartAsync(server);

foreach (var ep in server.GetEndpoints())
    Console.WriteLine($"[Spike] {ep.EndpointUrl}  {ep.SecurityMode}  {ep.SecurityPolicyUri.Split('#').Last()}");
Console.WriteLine("[Spike] 실행 중. Ctrl+C로 종료");

var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
server.CurrentInstance.SessionManager.SessionActivated += (s, _) => Console.WriteLine($"[Spike] 세션 활성: {s.SessionDiagnostics.SessionName}");
server.CurrentInstance.SessionManager.SessionClosing += (s, _) => Console.WriteLine($"[Spike] 세션 종료: {s.SessionDiagnostics.SessionName}");
await quit.Task;
await server.StopAsync();
return 0;

sealed class SpikeNodeManagerFactory : INodeManagerFactory
{
    public const string Ns = "urn:conveyortwin:spike";
    public StringCollection NamespacesUris => new StringCollection { Ns };
    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        => new SpikeNodeManager(server, configuration);
}

sealed class SpikeNodeManager : CustomNodeManager2
{
    BaseDataVariableState _joints;
    BaseDataVariableState _counter;
    Timer _timer;
    long _count;

    public SpikeNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, SpikeNodeManagerFactory.Ns) { }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            var root = new FolderState(null)
            {
                SymbolicName = "RobotSpike",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId("RobotSpike", NamespaceIndex),
                BrowseName = new QualifiedName("RobotSpike", NamespaceIndex),
                DisplayName = "RobotSpike",
            };
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));

            _joints = Variable(root, "Joints", DataTypeIds.Double, ValueRanks.OneDimension, new double[6]);
            _joints.ArrayDimensions = new ReadOnlyList<uint>(new uint[] { 6 });
            _counter = Variable(root, "Counter", DataTypeIds.Int64, ValueRanks.Scalar, 0L);

            AddPredefinedNode(SystemContext, root);
        }
        _timer = new Timer(_ => Tick(), null, 0, 50);
    }

    BaseDataVariableState Variable(NodeState parent, string name, NodeId dataType, int valueRank, object value)
    {
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId("RobotSpike." + name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            DataType = dataType,
            ValueRank = valueRank,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(v);
        return v;
    }

    void Tick()
    {
        lock (Lock)
        {
            _count++;
            double t = _count * 0.05;
            var joints = new double[6];
            for (int i = 0; i < 6; i++)
                joints[i] = 30.0 * Math.Sin(t + i);   // 관절각(도)
            var now = DateTime.UtcNow;
            _joints.Value = joints;
            _joints.Timestamp = now;
            _counter.Value = _count;
            _counter.Timestamp = now;
            _joints.ClearChangeMasks(SystemContext, false);
            _counter.ClearChangeMasks(SystemContext, false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}
