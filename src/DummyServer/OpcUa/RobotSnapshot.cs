using ConveyorTwin.Shared;

namespace ConveyorTwin.DummyServer.OpcUa;

/// <summary>
/// 가상 로봇 컨트롤러의 한 시점 상태. OPC UA 노드에는 이 묶음 단위로, 같은 SourceTimestamp로 반영된다.
/// 모두 "원시 신호"다 — 픽 실패·정지 원인 같은 판단은 트윈이 조합해서 한다 (원칙 2).
/// </summary>
public sealed record RobotSnapshot(
    double[] JointsDeg,
    bool MotorsOn,
    double SpeedOverridePct,
    OperationalMode Mode,
    ProgramExecutionState Execution,
    uint CycleCount,
    bool EmergencyStop,
    bool ProtectiveStop,
    bool VacuumOn,
    bool VacuumOk)
{
    /// <summary>홈 자세, 자동 모드, 모터 켜짐, 프로그램 정지</summary>
    public static RobotSnapshot Home() => new(new double[6], true, 100.0, OperationalMode.Automatic,
        ProgramExecutionState.Stopped, 0, false, false, false, false);
}
