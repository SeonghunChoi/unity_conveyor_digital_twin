using ConveyorTwin.Shared;
using Opc.Ua;
using Opc.Ua.Server;

namespace ConveyorTwin.DummyServer.OpcUa;

/// <summary>
/// 로봇 셀 주소 공간. 노드 구조와 ID는 Shared.RobotNodes (docs/robot-opcua.md).
///
///   Objects/RobotCell01                         (40010 MotionDeviceSystem)
///     MotionDevices/IRB2600                     (MotionDevice: Manufacturer, Model, SerialNumber, JointPositions)
///       Axes/Axis1..6/ParameterSet/ActualPosition
///     Controllers/IRC5                          (Controller: MotorsOn, SpeedOverride)
///       TaskControls/T_ROB1/ParameterSet        (TaskProgramName, ExecutionState, CycleCount)
///       IO                                      (DO_VacuumOn, DI_VacuumOk, DI_PalletPresent)
///     SafetyStates/SafetyState/ParameterSet     (OperationalMode, EmergencyStop, ProtectiveStop)
/// </summary>
sealed class RobotNodeManager : CustomNodeManager2
{
    readonly Dictionary<string, BaseVariableState> _vars = new();

    public RobotNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, RobotNodes.NamespaceUri) { }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();

            var system = Obj(null, RobotNodes.System, "RobotCell01");
            system.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, system.NodeId));

            // ---- MotionDevice ----
            var devices = Folder(system, RobotNodes.System + ".MotionDevices", "MotionDevices");
            var device = Obj(devices, RobotNodes.MotionDevice, "IRB2600");
            Prop(device, RobotNodes.Manufacturer, "Manufacturer", "ABB");
            Prop(device, RobotNodes.Model, "Model", "IRB 2600-20/1.65");
            Prop(device, RobotNodes.SerialNumber, "SerialNumber", "SIM-2600-0001");
            var joints = Var(device, RobotNodes.JointPositions, "JointPositions", DataTypeIds.Double, ValueRanks.OneDimension,
                new double[6], "6축 관절각 [deg], 한 시각의 묶음 (벤더 확장)");
            joints.ArrayDimensions = new ReadOnlyList<uint>(new uint[] { 6 });

            var axes = Folder(device, RobotNodes.MotionDevice + ".Axes", "Axes");
            for (int i = 1; i <= 6; i++)
            {
                var axis = Obj(axes, $"{RobotNodes.MotionDevice}.Axes.Axis{i}", $"Axis{i}");
                var ps = Obj(axis, $"{RobotNodes.MotionDevice}.Axes.Axis{i}.ParameterSet", "ParameterSet");
                Var(ps, RobotNodes.AxisActualPosition(i), "ActualPosition", DataTypeIds.Double, ValueRanks.Scalar, 0.0, "[deg]");
            }

            // ---- Controller ----
            var controllers = Folder(system, RobotNodes.System + ".Controllers", "Controllers");
            var ctrl = Obj(controllers, RobotNodes.Controller, "IRC5");
            Var(ctrl, RobotNodes.MotorsOn, "MotorsOn", DataTypeIds.Boolean, ValueRanks.Scalar, false, "모터 전원");
            Var(ctrl, RobotNodes.SpeedOverride, "SpeedOverride", DataTypeIds.Double, ValueRanks.Scalar, 100.0, "[%]");

            var tasks = Folder(ctrl, RobotNodes.Controller + ".TaskControls", "TaskControls");
            var task = Obj(tasks, RobotNodes.Task, "T_ROB1");
            var tps = Obj(task, RobotNodes.Task + ".ParameterSet", "ParameterSet");
            Var(tps, RobotNodes.TaskProgramName, "TaskProgramName", DataTypeIds.String, ValueRanks.Scalar, "PalletizeBoxes", "적재 프로그램");
            Var(tps, RobotNodes.ExecutionState, "ExecutionState", DataTypeIds.Int32, ValueRanks.Scalar, 0, "0=Stopped, 1=Running");
            Var(tps, RobotNodes.CycleCount, "CycleCount", DataTypeIds.UInt32, ValueRanks.Scalar, 0u, "완료한 픽·적재 사이클 수");

            var io = Obj(ctrl, RobotNodes.Io, "IO");
            Var(io, RobotNodes.VacuumOn, "DO_VacuumOn", DataTypeIds.Boolean, ValueRanks.Scalar, false, "진공 켜기 출력");
            Var(io, RobotNodes.VacuumOk, "DI_VacuumOk", DataTypeIds.Boolean, ValueRanks.Scalar, false, "진공 압력 스위치 (물건 흡착됨)");
            Var(io, RobotNodes.PalletPresent, "DI_PalletPresent", DataTypeIds.Boolean, ValueRanks.Scalar, true, "팔레트 존재 센서 (교체 중 false)");

            // ---- SafetyState ----
            var safeties = Folder(system, RobotNodes.System + ".SafetyStates", "SafetyStates");
            var safety = Obj(safeties, RobotNodes.Safety, "SafetyState");
            var sps = Obj(safety, RobotNodes.Safety + ".ParameterSet", "ParameterSet");
            Var(sps, RobotNodes.OperationalMode, "OperationalMode", DataTypeIds.Int32, ValueRanks.Scalar, 0,
                "40010 OperationalModeEnumeration: 0=Other, 1=ManualReducedSpeed, 2=ManualHighSpeed, 3=Automatic, 4=AutomaticExternal");
            Var(sps, RobotNodes.EmergencyStop, "EmergencyStop", DataTypeIds.Boolean, ValueRanks.Scalar, false, "비상정지");
            Var(sps, RobotNodes.ProtectiveStop, "ProtectiveStop", DataTypeIds.Boolean, ValueRanks.Scalar, false, "보호정지");

            AddPredefinedNode(SystemContext, system);
        }
    }

    /// <summary>스냅샷을 노드에 반영. 모든 값에 같은 SourceTimestamp를 찍는다 (임의 스레드에서 호출)</summary>
    public void Publish(RobotSnapshot s, DateTime sourceTimeUtc)
    {
        lock (Lock)
        {
            if (_vars.Count == 0) return;   // 주소 공간 생성 전
            Set(RobotNodes.JointPositions, (double[])s.JointsDeg.Clone(), sourceTimeUtc);
            for (int i = 0; i < 6; i++)
                Set(RobotNodes.AxisActualPosition(i + 1), s.JointsDeg[i], sourceTimeUtc);
            Set(RobotNodes.MotorsOn, s.MotorsOn, sourceTimeUtc);
            Set(RobotNodes.SpeedOverride, s.SpeedOverridePct, sourceTimeUtc);
            Set(RobotNodes.ExecutionState, (int)s.Execution, sourceTimeUtc);
            Set(RobotNodes.CycleCount, s.CycleCount, sourceTimeUtc);
            Set(RobotNodes.VacuumOn, s.VacuumOn, sourceTimeUtc);
            Set(RobotNodes.VacuumOk, s.VacuumOk, sourceTimeUtc);
            Set(RobotNodes.PalletPresent, s.PalletPresent, sourceTimeUtc);
            Set(RobotNodes.OperationalMode, (int)s.Mode, sourceTimeUtc);
            Set(RobotNodes.EmergencyStop, s.EmergencyStop, sourceTimeUtc);
            Set(RobotNodes.ProtectiveStop, s.ProtectiveStop, sourceTimeUtc);
        }
    }

    void Set(string id, object value, DateTime ts)
    {
        var v = _vars[id];
        // 값이 그대로면 갱신하지 않는다. 어차피 서버의 데이터 변경 필터(기본 StatusValue)가 같은 값은 알림을 보내지 않으므로,
        // SourceTimestamp가 "마지막으로 값이 바뀐 시각"으로 남게 한다. 끊김 감지는 값 갱신이 아니라 구독 KeepAlive로 한다.
        bool same = value is double[] a && v.Value is double[] b ? a.AsSpan().SequenceEqual(b) : Equals(v.Value, value);
        if (same) return;
        v.Value = value;
        v.Timestamp = ts;
        v.StatusCode = StatusCodes.Good;
        v.ClearChangeMasks(SystemContext, false);
    }

    // ---------- 노드 생성 도우미 ----------

    FolderState Folder(NodeState? parent, string id, string name)
    {
        var f = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
        };
        parent?.AddChild(f);
        return f;
    }

    BaseObjectState Obj(NodeState? parent, string id, string name)
    {
        var o = new BaseObjectState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.HasComponent,
            TypeDefinitionId = ObjectTypeIds.BaseObjectType,
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
        };
        parent?.AddChild(o);
        return o;
    }

    void Prop(NodeState parent, string id, string name, string value)
    {
        var p = new PropertyState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.HasProperty,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            DataType = DataTypeIds.String,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(p);
    }

    BaseDataVariableState Var(NodeState parent, string id, string name, NodeId dataType, int valueRank, object value, string description)
    {
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.HasComponent,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            Description = description,
            DataType = dataType,
            ValueRank = valueRank,
            AccessLevel = AccessLevels.CurrentRead,       // 트윈은 읽기만 한다 (원칙 1)
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(v);
        _vars[id] = v;
        return v;
    }
}

sealed class RobotNodeManagerFactory : INodeManagerFactory
{
    public RobotNodeManager? Instance { get; private set; }
    public StringCollection NamespacesUris => new() { RobotNodes.NamespaceUri };

    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        => Instance = new RobotNodeManager(server, configuration);
}
