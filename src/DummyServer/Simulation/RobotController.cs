using System.Diagnostics;
using ConveyorTwin.DummyServer.OpcUa;
using ConveyorTwin.Shared;

namespace ConveyorTwin.DummyServer.Simulation;

/// <summary>UI 표시용 로봇 상태</summary>
public sealed record RobotStatus(string Phase, uint CycleCount, int NextSlot, bool PalletPresent, double LastCycleS, double[] JointsDeg);

/// <summary>
/// 가상 로봇 컨트롤러 ("현실" 역할, 2단계 4일 차). 적재 프로그램 PalletizeBoxes를 실행하고 원시 신호를 OPC UA로 낸다.
///
/// 사이클: 대기 자세(픽 위) → 출구에 물건이 멈추면 내려가 진공 흡착 → 들어 올림 → 팔레트 칸 위 접근 → 내려놓음(진공 해제)
///        → 빠져나옴 → 대기 자세. 48칸이 차면 대기 자세에서 팔레트 교체(DI_PalletPresent가 잠시 false)를 기다린다.
///
/// 동작은 관절 공간 보간이다: 구간마다 목표 관절각을 IK(Shared.RobotKinematics, 공구 수직 하향)로 구하고,
/// 가장 오래 걸리는 관절에 맞춘 시간 동안 5차 다항식(시작·끝에서 속도·가속도 0)으로 모든 관절을 함께 움직인다.
/// 실제 로봇의 MoveJ와 같은 방식이며, 직선 경로(MoveL)는 쓰지 않는다 — 수직 접근 구간이 짧아 휘는 정도가 작다.
///
/// 트윈에는 관절각·진공 출력·압력 스위치·사이클 수 같은 원시 신호만 간다. "지금 무슨 단계인가"는 트윈이 신호 조합으로 추론한다.
/// </summary>
public sealed class RobotController
{
    const int TickMs = 20;

    /// <summary>프로그램 속도: 관절 최대 속도 대비 비율 (ABB speeddata를 단순화)</summary>
    const double ProgramSpeed = 0.6;
    /// <summary>5차 다항식 보간의 최고 속도 = 평균 속도 × 1.875 → 최고 속도가 한계를 넘지 않게 시간을 늘린다</summary>
    const double QuinticPeakFactor = 1.875;
    const double MinMoveS = 0.25;

    const double ReadyAboveM = 0.25;     // 대기 자세: 픽 위치 위
    const double ApproachAboveM = 0.20;  // 팔레트 칸 위 접근 높이
    const double VacuumBuildS = 0.15;    // 진공 켠 뒤 압력 스위치가 잡힐 때까지
    const double VacuumReleaseS = 0.10;  // 진공 끈 뒤 압력 스위치가 풀릴 때까지
    const double SettleS = 0.10;         // 놓은 뒤 빠져나오기 전 대기
    const double PalletSwapS = 4.0;      // 가득 찬 팔레트를 빼고 빈 팔레트를 넣는 시간

    enum Phase { ToReady, WaitPart, ToPick, Grip, Lift, ToApproach, ToPlace, Release, Retract, PalletSwap }

    readonly RobotOpcUaServer _server;
    readonly ConveyorSimulator _conveyor;

    Phase _phase = Phase.ToReady;
    double _phaseStartS;
    readonly double[] _q = new double[6];          // 현재 관절각 (홈 자세에서 시작)
    double[] _from = new double[6], _to = new double[6];
    double _moveStartS, _moveDurationS;

    bool _vacuumOn, _vacuumOk, _palletPresent = true;
    uint _cycles;
    int _slot;
    double _cycleStartS = -1, _lastCycleS;

    public event Action<RobotStatus>? Updated;

    public RobotController(RobotOpcUaServer server, ConveyorSimulator conveyor)
    {
        _server = server;
        _conveyor = conveyor;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        StartMove(Pose(Ready), 0);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            double now = clock.Elapsed.TotalSeconds;
            Step(now);
            _server.Publish(new RobotSnapshot((double[])_q.Clone(), true, 100.0, OperationalMode.Automatic,
                ProgramExecutionState.Running, _cycles, false, false, _vacuumOn, _vacuumOk, _palletPresent));
            Updated?.Invoke(new RobotStatus(_phase.ToString(), _cycles, _slot, _palletPresent, _lastCycleS, (double[])_q.Clone()));
        }
    }

    static CellPoint Ready => Above(RobotCell.PickTop, ReadyAboveM);

    void Step(double now)
    {
        bool arrived = Interpolate(now);
        double inPhase = now - _phaseStartS;

        switch (_phase)
        {
            case Phase.ToReady:
                if (arrived) Enter(_slot >= RobotCell.SlotCount ? Phase.PalletSwap : Phase.WaitPart, now);
                break;

            case Phase.WaitPart:
                if (_conveyor.PartAtPick)
                {
                    _cycleStartS = now;
                    StartMove(Pose(RobotCell.PickTop), now);
                    Enter(Phase.ToPick, now);
                }
                break;

            case Phase.ToPick:
                if (arrived)
                {
                    _vacuumOn = true;
                    Enter(Phase.Grip, now);
                }
                break;

            case Phase.Grip:
                if (inPhase >= VacuumBuildS)
                {
                    _vacuumOk = true;           // 압력 스위치: 물건이 붙었다
                    _conveyor.RequestPick();    // 들어 올리면 출구 센서가 풀린다
                    StartMove(Pose(Ready), now);
                    Enter(Phase.Lift, now);
                }
                break;

            case Phase.Lift:
                if (arrived)
                {
                    StartMove(Pose(Above(RobotCell.SlotTop(_slot), ApproachAboveM)), now);
                    Enter(Phase.ToApproach, now);
                }
                break;

            case Phase.ToApproach:
                if (arrived)
                {
                    StartMove(Pose(RobotCell.SlotTop(_slot)), now);
                    Enter(Phase.ToPlace, now);
                }
                break;

            case Phase.ToPlace:
                if (arrived)
                {
                    _vacuumOn = false;
                    Enter(Phase.Release, now);
                }
                break;

            case Phase.Release:
                if (inPhase >= VacuumReleaseS) _vacuumOk = false;
                if (inPhase >= VacuumReleaseS + SettleS)
                {
                    StartMove(Pose(Above(RobotCell.SlotTop(_slot), ApproachAboveM)), now);
                    _cycles++;
                    _slot++;
                    Enter(Phase.Retract, now);
                }
                break;

            case Phase.Retract:
                if (arrived)
                {
                    StartMove(Pose(Ready), now);
                    if (_cycleStartS >= 0) _lastCycleS = now - _cycleStartS + _moveDurationS;   // 대기 자세 복귀까지
                    Enter(Phase.ToReady, now);
                }
                break;

            case Phase.PalletSwap:
                _palletPresent = inPhase < 0.2 || inPhase >= PalletSwapS;   // 잠깐 뒤 빠지고, 교체 시간이 지나면 빈 팔레트
                if (inPhase >= PalletSwapS)
                {
                    _slot = 0;
                    _palletPresent = true;
                    Enter(Phase.WaitPart, now);
                }
                break;
        }
    }

    void Enter(Phase phase, double now)
    {
        _phase = phase;
        _phaseStartS = now;
    }

    // ---------- 관절 공간 보간 ----------

    void StartMove(double[] target, double now)
    {
        _from = (double[])_q.Clone();
        _to = target;
        double t = 0;
        for (int i = 0; i < 6; i++)
            t = Math.Max(t, Math.Abs(_to[i] - _from[i]) / (RobotKinematics.MaxSpeedDegPerS[i] * ProgramSpeed));
        _moveDurationS = Math.Max(MinMoveS, t * QuinticPeakFactor);
        _moveStartS = now;
    }

    /// <returns>목표에 도달했으면 true</returns>
    bool Interpolate(double now)
    {
        double s = Math.Clamp((now - _moveStartS) / _moveDurationS, 0, 1);
        double k = s * s * s * (10 - 15 * s + 6 * s * s);   // 5차 다항식: 위치 0→1, 양 끝 속도·가속도 0
        for (int i = 0; i < 6; i++)
            _q[i] = _from[i] + (_to[i] - _from[i]) * k;
        return s >= 1;
    }

    static double[] Pose(CellPoint tcp)
    {
        if (!RobotKinematics.SolveTopDown(tcp, RobotCell.GripperLengthM, out var q, out _))
            throw new InvalidOperationException($"도달할 수 없는 목표 {tcp} (셀 배치 점검: Tools/Twin/Check Robot Cell)");
        return q;
    }

    static CellPoint Above(CellPoint p, double dz) => new(p.X, p.Y, p.Z + dz);
}
