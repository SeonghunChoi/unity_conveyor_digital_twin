namespace ConveyorTwin.Shared
{
    /// <summary>
    /// 로봇 셀 OPC UA 정보 모델 규약 (2단계, docs/robot-opcua.md).
    /// OPC 40010 Robotics 동반 규격의 구조·이름(MotionDeviceSystem, MotionDevices, Axes, Controllers, SafetyStates)을
    /// 따르되, 표준 NodeSet 없이 서버가 직접 만든 단순화 모델이다.
    ///
    /// 노드 ID는 모두 이 네임스페이스의 문자열 ID다. 네임스페이스 인덱스는 접속 후 NamespaceUri로 찾는다
    /// (인덱스는 서버마다 달라질 수 있으므로 하드코딩하지 않는다).
    /// Shared는 OPC UA 스택에 의존하지 않으므로 문자열·정수만 담는다.
    /// </summary>
    public static class RobotNodes
    {
        public const string NamespaceUri = "urn:conveyortwin:robotcell";

        public const string System = "RobotCell01";
        public const string MotionDevice = System + ".MotionDevices.IRB2600";
        public const string Controller = System + ".Controllers.IRC5";
        public const string Task = Controller + ".TaskControls.T_ROB1";
        public const string Safety = System + ".SafetyStates.SafetyState";
        public const string Io = Controller + ".IO";

        // ---- MotionDevice: 식별 정보 (Property) ----
        public const string Manufacturer = MotionDevice + ".Manufacturer";
        public const string Model = MotionDevice + ".Model";
        public const string SerialNumber = MotionDevice + ".SerialNumber";

        /// <summary>
        /// 6축 관절각 한 묶음 (Double[6], 도, ABB 부호). 트윈은 이 노드를 구독한다.
        /// 40010의 축별 ActualPosition도 함께 두지만, 축마다 따로 구독하면 같은 순간의 6축이 다른 알림으로 나뉘어
        /// 자세가 잠깐 찢어질 수 있다. 그래서 한 SourceTimestamp에 6축이 함께 담긴 배열을 따로 둔다 (벤더 확장).
        /// </summary>
        public const string JointPositions = MotionDevice + ".JointPositions";

        /// <summary>축별 실제 위치 (40010 Axis.ParameterSet.ActualPosition), axis = 1~6</summary>
        public static string AxisActualPosition(int axis) => $"{MotionDevice}.Axes.Axis{axis}.ParameterSet.ActualPosition";

        // ---- Controller ----
        /// <summary>모터 전원 (Boolean). 비상정지·보호정지가 걸리면 꺼진다</summary>
        public const string MotorsOn = Controller + ".MotorsOn";

        /// <summary>속도 오버라이드 (Double, %)</summary>
        public const string SpeedOverride = Controller + ".SpeedOverride";

        // ---- TaskControl (40010 TaskControl) ----
        public const string TaskProgramName = Task + ".ParameterSet.TaskProgramName";
        /// <summary>프로그램 실행 상태 (Int32, <see cref="ProgramExecutionState"/>)</summary>
        public const string ExecutionState = Task + ".ParameterSet.ExecutionState";
        /// <summary>완료한 픽·적재 사이클 수 (UInt32)</summary>
        public const string CycleCount = Task + ".ParameterSet.CycleCount";

        // ---- SafetyState (40010 SafetyState) ----
        /// <summary>운전 모드 (Int32, <see cref="OperationalMode"/>)</summary>
        public const string OperationalMode = Safety + ".ParameterSet.OperationalMode";
        public const string EmergencyStop = Safety + ".ParameterSet.EmergencyStop";
        public const string ProtectiveStop = Safety + ".ParameterSet.ProtectiveStop";

        // ---- IO: 진공 그리퍼 (원시 신호. 픽 실패는 트윈이 두 신호의 조합으로 추론한다 — 원칙 2) ----
        /// <summary>DO: 진공 켜기 명령 (Boolean)</summary>
        public const string VacuumOn = Io + ".DO_VacuumOn";
        /// <summary>DI: 진공 압력 스위치 = 물건이 붙어 있음 (Boolean)</summary>
        public const string VacuumOk = Io + ".DI_VacuumOk";

        /// <summary>DI: 팔레트 존재 센서 (Boolean). 가득 찬 팔레트를 빼고 빈 팔레트를 넣는 동안 false</summary>
        public const string PalletPresent = Io + ".DI_PalletPresent";
    }

    /// <summary>운전 모드 (OPC 40010 OperationalModeEnumeration과 같은 값)</summary>
    public enum OperationalMode
    {
        Other = 0,
        ManualReducedSpeed = 1,
        ManualHighSpeed = 2,
        Automatic = 3,
        AutomaticExternal = 4,
    }

    /// <summary>로봇 프로그램 실행 상태 (ABB RAPID 실행 상태를 단순화)</summary>
    public enum ProgramExecutionState
    {
        Stopped = 0,
        Running = 1,
    }

    /// <summary>
    /// OPC UA 접속 정보. 서버와 트윈의 애플리케이션 신원(ApplicationUri, 인증서 주체)은 서버의 인증서 발급 모드와
    /// 트윈 클라이언트가 같은 값을 써야 하므로 여기 둔다.
    /// </summary>
    public static class OpcUaEndpoint
    {
        /// <summary>IPv4 루프백. Mono 클라이언트는 MonoTcpTransport로 접속한다 (docs/opcua-spike.md)</summary>
        public const string Url = "opc.tcp://127.0.0.1:4840";

        /// <summary>트윈이 쓰는 보안 정책 (Mono에서 RsaPss 불가)</summary>
        public const string SecurityPolicy = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";

        public const string ServerApplicationUri = "urn:localhost:ConveyorTwin:DummyServer";
        public const string ServerApplicationName = "ConveyorTwin Dummy Server";
        public const string ServerSubject = "CN=ConveyorTwin Dummy Server, O=ConveyorTwin";

        public const string TwinApplicationUri = "urn:localhost:ConveyorTwin:Twin";
        public const string TwinApplicationName = "ConveyorTwin Twin";
        public const string TwinSubject = "CN=ConveyorTwin Twin, O=ConveyorTwin";

        public const string ProductUri = "uri:conveyortwin";

        /// <summary>서버 exe의 인증서 발급 모드 인수: ConveyorTwin.DummyServer.exe --provision-twin-cert &lt;트윈 pki 경로&gt;</summary>
        public const string ProvisionArg = "--provision-twin-cert";
    }
}
