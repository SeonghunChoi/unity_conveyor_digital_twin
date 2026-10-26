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
///
/// 설비 장애 (7일 차, 서버 UI 버튼 — 원칙 3: 장애는 현실 쪽에서):
///  - 비상정지: 그 자리에서 멈추고 모터 전원 차단, 프로그램 정지. 해제하면 1초 뒤 모터가 켜지고 멈춘 동작을 이어 간다
///    (컨트롤러 시계가 멈춰 있으므로 보간·대기 시간이 그대로 이어진다). 진공은 유지 — 비상정지로 박스를 떨어뜨리지 않는다
///  - 흡착 실패: 진공을 켜도 압력 스위치가 잡히지 않는다. 1초 뒤 진공을 끄고 프로그램을 멈춘 채 픽 위치에서 기다리며, 해제하면 다시 흡착을 시도한다
///  - 엔코더 이상: 관절각 노드의 StatusCode가 Bad (값은 마지막 값). 로봇은 계속 움직인다 — 측정만 믿을 수 없는 상태
/// 통신 끊김은 컨트롤러가 아니라 네트워크(NetworkRelay) 장애다.
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
    const double GripTimeoutS = 1.0;     // 진공을 켜고 압력 스위치를 기다리는 한도
    const double MotorsOnDelayS = 1.0;   // 비상정지 해제 후 모터 전원이 들어올 때까지

    enum Phase { ToReady, WaitPart, ToPick, Grip, GripFailed, Lift, ToApproach, ToPlace, Release, Retract, PalletSwap }

    // ---- 장애 (UI 스레드에서 켜고 끈다) ----
    public volatile bool EmergencyStop;
    public volatile bool VacuumFailure;
    public volatile bool EncoderFault;

    bool _motorsOn = true;
    double _estopReleasedAtS = -1;      // 실시간 기준

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
        double lastReal = 0, ctrl = 0;   // 컨트롤러 시계: 모터가 켜져 있을 때만 흐른다 (비상정지 중 동작·대기 시간 정지)
        StartMove(Pose(Ready), 0);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            double real = clock.Elapsed.TotalSeconds;
            double dt = real - lastReal;
            lastReal = real;

            bool estop = EmergencyStop;
            if (estop)
            {
                _motorsOn = false;
                _estopReleasedAtS = -1;
            }
            else if (!_motorsOn)
            {
                if (_estopReleasedAtS < 0) _estopReleasedAtS = real;
                if (real - _estopReleasedAtS >= MotorsOnDelayS) _motorsOn = true;
            }

            if (_motorsOn)
            {
                ctrl += dt;
                Step(ctrl);
            }
            UpdateVacuum(real);
            bool running = _motorsOn && _phase != Phase.GripFailed;
            _server.Publish(new RobotSnapshot((double[])_q.Clone(), _motorsOn, 100.0, OperationalMode.Automatic,
                running ? ProgramExecutionState.Running : ProgramExecutionState.Stopped, _cycles, estop, false,
                _vacuumOn, _vacuumOk, _palletPresent, EncoderFault));
            Updated?.Invoke(new RobotStatus(_phase.ToString(), _cycles, _slot, _palletPresent, _lastCycleS, (double[])_q.Clone()));
        }
    }

    static CellPoint Ready => Above(RobotCell.PickTop, ReadyAboveM);

    bool _lastVacuumOn;
    double _vacuumChangedAtS;

    /// <summary>
    /// 진공(공압)은 모터 전원과 무관하게 실제 시간으로 형성·해제된다. 컨트롤러 시계로 재면 비상정지 중 압력 스위치가 늦게 켜져
    /// 트윈이 흡착 실패로 오판했다 (7일 차 시험에서 발견). 흡착 실패 장애면 진공을 켜도 압력이 잡히지 않는다.
    /// </summary>
    void UpdateVacuum(double real)
    {
        if (_vacuumOn != _lastVacuumOn)
        {
            _lastVacuumOn = _vacuumOn;
            _vacuumChangedAtS = real;
        }
        if (_vacuumOn && !VacuumFailure && real - _vacuumChangedAtS >= VacuumBuildS) _vacuumOk = true;
        if (!_vacuumOn && real - _vacuumChangedAtS >= VacuumReleaseS) _vacuumOk = false;
    }

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
                if (VacuumFailure)
                {
                    // 압력 스위치가 잡히지 않음 → 한도 시간 뒤 진공을 끄고 프로그램 정지 (픽 위치에서 대기)
                    if (inPhase >= GripTimeoutS)
                    {
                        _vacuumOn = false;
                        Enter(Phase.GripFailed, now);
                    }
                }
                else if (_vacuumOk)
                {
                    // 압력 스위치가 잡혔다 (UpdateVacuum) → 들어 올리면 출구 센서가 풀린다
                    _conveyor.RequestPick();
                    StartMove(Pose(Ready), now);
                    Enter(Phase.Lift, now);
                }
                break;

            case Phase.GripFailed:
                if (!VacuumFailure)
                {
                    _vacuumOn = true;      // 운전자가 해제(재시작) → 다시 흡착 시도
                    Enter(Phase.Grip, now);
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
                if (!_vacuumOk && inPhase >= VacuumReleaseS + SettleS)
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
