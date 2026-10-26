using System;
using System.Collections.Generic;
using System.IO;
using ConveyorTwin.Shared;
using ConveyorTwin.Twin.OpcUa;
using UnityEngine;

namespace ConveyorTwin.Twin.Robot
{
    /// <summary>
    /// 로봇 미러 (2단계 6일 차). OPC UA로 받은 원시 신호를 그대로 따라 그린다 — 트윈은 IK도 경로 계산도 하지 않는다 (원칙 1).
    ///
    /// 재생: 관절각과 IO 신호를 SourceTimestamp 기준으로 버퍼에 쌓고, 컨베이어와 같은 재생 시계(ConveyorTwinController.PlaybackMs)로 재생한다.
    /// MQTT ts와 OPC UA SourceTimestamp는 같은 서버 시계라, 박스가 출구에서 그리퍼로 넘어가는 순간이 두 프로토콜 사이에서 어긋나지 않는다.
    /// 관절각은 값이 바뀔 때만 오므로, 마지막 샘플 이후에는 그 자세를 유지하는 것이 곧 현실과 같다.
    ///
    /// 물건 인계 (신호 조합으로 추론, 원칙 2):
    ///  - 압력 스위치(DI_VacuumOk) ON: 출구에 멈춘 박스를 그리퍼(TCP)에 붙인다. 출구 해제(MQTT)와 같은 틱에 오므로 어느 쪽이 먼저 처리돼도 같은 박스가 넘어온다
    ///  - 압력 스위치 OFF: 박스를 그 자리에 놓는다 (팔레트 칸을 계산하지 않음 — 그리퍼가 있던 곳이 곧 놓인 곳)
    ///  - 팔레트 센서(DI_PalletPresent) OFF: 팔레트를 뺀 것이므로 쌓인 박스와 팔레트를 숨기고, ON이면 빈 팔레트를 보인다
    ///
    /// 장애 대응 (7일 차, 서버의 로봇 셀 장애 버튼):
    ///  - 통신 끊김: 마지막 수신 자세로 멈추고 회색조로 "믿을 수 없음"을 표시. 재접속하면 서버가 현재값을 보내 주지만,
    ///    끊긴 동안 일어난 흡착·해제 순간은 알 수 없다 → 그리퍼 박스는 근거 없이 놓지 않고 지우며, 그동안 적재된 개수만 알린다
    ///  - 비상정지·보호정지·모터 OFF·프로그램 정지: 원시 신호 그대로 알람
    ///  - 흡착 실패: 서버는 "실패"를 보내지 않는다. 진공 출력 ON인데 압력 스위치가 0.5초 넘게 OFF면 트윈이 흡착 실패로 판단한다 (원칙 2)
    ///  - 관절각 StatusCode Bad: RobotGate가 버리므로 마지막 정상 자세를 유지하고 알람
    ///  - 출구 대기: 출구(MQTT)가 3초 넘게 막혀 있는데 로봇(OPC UA)이 집지 않음 — 두 프로토콜을 합쳐야 보이는 상태
    /// </summary>
    public sealed class RobotTwin : MonoBehaviour
    {
        /// <summary>관절 노드 회전축 (로봇 로컬, docs/cad-robot.md 4절). localRotation = AngleAxis(θ, 축)</summary>
        static readonly Vector3[] JointAxes =
        {
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
        };

        /// <summary>재생 시계가 없을 때(컨베이어 MQTT 미수신) 쓰는 지연</summary>
        const long FallbackDelayMs = 300;
        /// <summary>출구를 떠난 박스를 압력 스위치가 받아 갈 때까지 기다리는 시간 (재생 시각 기준)</summary>
        const long HandoffWindowMs = 1000;
        const int MaxJointSamples = 512;
        /// <summary>진공 출력 ON 후 압력 스위치가 이만큼 안 잡히면 흡착 실패로 본다 (서버 진공 형성 0.15s의 3배 이상)</summary>
        const long VacuumFaultMs = 500;
        /// <summary>출구 대기 경고: 밀린 박스를 처리하는 동안은 로봇 사이클(약 4.5s) 내내 정상적으로 기다리므로 그보다 길게</summary>
        const long ExitWaitWarnMs = 8000;
        /// <summary>트윈 시작 후 이 시간이 지나도 접속하지 못하면 알람</summary>
        const float StartupGraceS = 5f;

        [SerializeField] ConveyorTwinController conveyor;
        [SerializeField] Transform robotRoot;
        [SerializeField] Transform pallet;
        /// <summary>ConveyorTwin/StatusLit (Setup Scene이 지정). 로봇 상태 하이라이트·회색조</summary>
        [SerializeField] Shader statusShader;

        readonly Transform[] _joints = new Transform[6];
        Transform _tcp;
        Transform _stack;   // 팔레트 위에 놓인 박스들의 부모

        RobotOpcUaLink _link;
        int _seenConnectCount;
        readonly RobotGate _gate = new RobotGate();

        /// <summary>관절각 샘플 (서버 시각 ms, 각도) — 시각 순</summary>
        readonly List<KeyValuePair<double, double[]>> _jointSamples = new List<KeyValuePair<double, double[]>>();
        /// <summary>재생 시각을 기다리는 IO·상태 신호 — 시각 순</summary>
        readonly List<RobotSample> _pendingSignals = new List<RobotSample>();

        GameObject _held;                      // 그리퍼에 붙은 박스
        TrackedItem _handoff;                  // 출구를 떠나 압력 스위치를 기다리는 박스
        double _handoffAtMs;

        // ---- 장애 추론 상태 ----
        double _vacuumOnAtMs = -1;             // 진공 출력이 켜진 재생 시각
        double _playbackMs;
        double _exitOccupiedSinceMs = -1;
        /// <summary>재접속을 알아챈 시각 (Unix ms). SourceTimestamp가 이보다 이전인 값은 끊긴 동안 바뀐 값이다</summary>
        double _reconnectedMs = -1;
        uint _cycleBeforeOutage;
        bool _everConnected;
        bool _haveState;
        readonly List<Material> _bodyMaterials = new List<Material>();
        static readonly int IdHighlightColor = Shader.PropertyToID("_HighlightColor");
        static readonly int IdHighlightStrength = Shader.PropertyToID("_HighlightStrength");
        static readonly int IdPulseHz = Shader.PropertyToID("_PulseHz");
        static readonly int IdDesaturate = Shader.PropertyToID("_Desaturate");
        static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");

        // ---- HMI·알람이 읽는 상태 (모두 재생 시각 기준의 원시 신호) ----
        public RobotLinkState LinkState => _link?.State ?? RobotLinkState.Starting;
        public string LinkError => _link?.LastError;
        public RobotGate Gate => _gate;
        public double[] JointsDeg { get; } = new double[6];
        public bool MotorsOn { get; private set; }
        public bool EmergencyStop { get; private set; }
        public bool ProtectiveStop { get; private set; }
        public OperationalMode Mode { get; private set; }
        public ProgramExecutionState Execution { get; private set; }
        public uint CycleCount { get; private set; }
        public double SpeedOverride { get; private set; }
        public bool VacuumOn { get; private set; }
        public bool VacuumOk { get; private set; }
        public bool PalletPresent { get; private set; } = true;
        public int BoxesOnPallet => _stack != null ? _stack.childCount : 0;
        public bool Holding => _held != null;
        public GameObject HeldBox => _held;
        public Transform Stack => _stack;
        public Transform Tcp => _tcp;
        /// <summary>최근 관절각의 SourceTimestamp → 수신 지연 (ms)</summary>
        public long LastJointLatencyMs { get; private set; }
        /// <summary>흡착 실패 (트윈 추론): 진공 출력 ON인데 압력 스위치가 VacuumFaultMs 넘게 OFF. 다음 흡착 성공까지 유지</summary>
        public bool VacuumFault { get; private set; }
        public double VacuumWaitS => VacuumOn && !VacuumOk && _vacuumOnAtMs > 0 ? (_playbackMs - _vacuumOnAtMs) / 1000.0 : 0;
        /// <summary>출구에 박스가 기다린 시간 (재생 시각 기준, 초)</summary>
        public double ExitWaitS => _exitOccupiedSinceMs > 0 ? (_playbackMs - _exitOccupiedSinceMs) / 1000.0 : 0;
        /// <summary>관절각 StatusCode Bad를 받은 뒤 정상 값이 다시 오기 전까지 true (마지막 정상 자세를 보이는 중)</summary>
        public bool JointDataBad { get; private set; }
        /// <summary>집을 때 벨트 위 추정 박스 윗면과 TCP의 거리 (맞춰 붙이기 전, 진단용)</summary>
        public double LastGripOffsetM { get; private set; }
        /// <summary>마지막 집기가 관절각 Bad 동안이었다 (트윈 자세를 믿을 수 없던 때)</summary>
        public bool LastGripDuringBadData { get; private set; }
        public int GripCount { get; private set; }
        public readonly List<string> Warnings = new List<string>();

        void Awake()
        {
            for (int i = 0; i < 6; i++)
                _joints[i] = FindDeep(robotRoot, "J" + (i + 1));
            _tcp = FindDeep(robotRoot, "TCP");
            _stack = new GameObject("Stack").transform;
            _stack.SetParent(pallet, false);
            _gate.ServerRestarted += OnServerRestarted;
        }

        void Start()
        {
            // 컨베이어 컨트롤러의 Awake에서 트래커가 만들어진 뒤에 연결한다
            conveyor.Tracker.ExitHandoff = OnItemLeftExit;
            conveyor.AppendConditions = EvaluateConditions;

            // 로봇 본체를 상태 셰이더로 바꿔 하이라이트·회색조를 표시한다 (그리퍼 프리미티브는 그대로)
            if (statusShader != null)
            {
                var gripper = _tcp.parent;
                foreach (var r in robotRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.transform.IsChildOf(gripper)) continue;
                    var src = r.sharedMaterial;
                    var m = new Material(statusShader);
                    if (src != null && src.HasProperty(IdBaseColor)) m.SetColor(IdBaseColor, src.GetColor(IdBaseColor));
                    r.sharedMaterial = m;
                    _bodyMaterials.Add(m);
                }
            }
        }

        /// <summary>
        /// 서버(현실)가 재시작됐다: 새 세션은 빈 팔레트·빈 그리퍼에서 시작하므로 이전 세션의 박스는 의미가 없다.
        /// 시각 기록은 RobotGate가 지우고, 여기서는 화면의 박스와 대기 중인 샘플을 비운다.
        /// </summary>
        void OnServerRestarted(DateTime oldStart, DateTime newStart)
        {
            AddWarning($"로봇 컨트롤러 재시작 감지 ({oldStart:HH:mm:ss} → {newStart:HH:mm:ss}): 그리퍼·팔레트 박스 초기화");
            foreach (Transform box in _stack) Destroy(box.gameObject);
            if (_held != null) Destroy(_held);
            if (_handoff != null) Destroy(_handoff.View);
            _held = null;
            _handoff = null;
            _jointSamples.Clear();
            _pendingSignals.Clear();
            VacuumOk = VacuumOn = false;
        }

        void OnEnable()
        {
            _link = new RobotOpcUaLink(Path.Combine(Application.persistentDataPath, "pki"), ServerLauncher.FindServerExe);
            _seenConnectCount = 0;
            _link.Start();
        }

        void OnDisable()
        {
            _link?.Dispose();
            _link = null;
        }

        // 컨베이어 컨트롤러가 Update에서 재생 시계를 진행한 뒤 같은 프레임에 따라간다
        void LateUpdate()
        {
            var nowUtc = DateTime.UtcNow;
            if (_link != null)
            {
                if (_link.ConnectCount != _seenConnectCount)
                {
                    if (_seenConnectCount > 0)
                    {
                        // 끊긴 동안의 흡착·해제는 볼 수 없었다 → 재접속 직후 오는 현재값으로 그 사이를 맞춘다
                        AddWarning("로봇 OPC UA 재접속: 끊긴 동안의 인계 이벤트 유실 → 그리퍼 상태 재동기화");
                        _reconnectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _cycleBeforeOutage = CycleCount;
                    }
                    _seenConnectCount = _link.ConnectCount;
                    _everConnected = true;
                }
                if (_link.IsConnected) _gate.ObserveServerStart(_link.ServerStartUtc);

                while (_link.TryDequeue(out var s))
                {
                    if (!_gate.Accept(s, nowUtc, out var reason))
                    {
                        if (reason != null)
                        {
                            // 관절각 거부는 정상 값이 다시 올 때까지 "측정을 믿을 수 없음" 상태로 둔다.
                            // OPC UA는 상태가 바뀔 때만 알리므로 Bad는 한 번만 온다 — 시간으로 풀면 장애 도중에 알람이 꺼진다
                            bool first = s.Node != RobotNodes.JointPositions || !JointDataBad;
                            if (s.Node == RobotNodes.JointPositions) JointDataBad = true;
                            if (first) AddWarning($"로봇 값 거부 {Short(s.Node)}: {reason}");
                        }
                        continue;
                    }
                    double t = ToMs(s.SourceUtc);
                    if (s.Node == RobotNodes.JointPositions)
                    {
                        JointDataBad = false;
                        AddJointSample(t, (double[])s.Value);
                        LastJointLatencyMs = s.ReceivedMs - (long)t;
                    }
                    else
                    {
                        InsertSignal(s);
                    }
                }
            }

            double playback = conveyor.PlaybackMs > 0
                ? conveyor.PlaybackMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - FallbackDelayMs;
            _playbackMs = playback;

            // IO·상태 신호: 재생 시각에 도달한 것부터 순서대로 (초기값처럼 오래된 시각의 값은 바로 적용)
            while (_pendingSignals.Count > 0 && ToMs(_pendingSignals[0].SourceUtc) <= playback)
            {
                ApplySignal(_pendingSignals[0], playback);
                _pendingSignals.RemoveAt(0);
            }

            ApplyJoints(playback);
            ExpireHandoff(playback);

            // 흡착 실패 추론: 진공 출력 ON인데 압력 스위치가 안 잡힌 채 시간이 지남
            if (!VacuumFault && VacuumOn && !VacuumOk && _vacuumOnAtMs > 0 && playback - _vacuumOnAtMs > VacuumFaultMs)
            {
                VacuumFault = true;
                AddWarning($"흡착 실패: 진공 출력 ON {VacuumFaultMs}ms 넘게 압력 스위치 OFF");
            }
            // 출구 대기: 컨베이어(MQTT) 출구 센서가 막힌 채 로봇이 집지 않음
            if (conveyor.Tracker.ExitOccupied) { if (_exitOccupiedSinceMs < 0) _exitOccupiedSinceMs = playback; }
            else _exitOccupiedSinceMs = -1;

            UpdateVisuals();
        }

        // ---------- 관절각 재생 ----------

        void AddJointSample(double t, double[] q)
        {
            int i = _jointSamples.Count;
            while (i > 0 && _jointSamples[i - 1].Key > t) i--;
            _jointSamples.Insert(i, new KeyValuePair<double, double[]>(t, q));
            if (_jointSamples.Count > MaxJointSamples) _jointSamples.RemoveAt(0);
        }

        void ApplyJoints(double t)
        {
            if (_jointSamples.Count == 0) return;
            // 재생 시각 이전 샘플은 보간에 필요한 하나만 남긴다
            while (_jointSamples.Count > 2 && _jointSamples[1].Key <= t)
                _jointSamples.RemoveAt(0);

            var a = _jointSamples[0];
            double[] q;
            if (t <= a.Key || _jointSamples.Count == 1)
            {
                q = a.Value;   // 첫 샘플 이전이거나 마지막 샘플 이후: 그 자세 유지 (값이 바뀔 때만 오므로 현실과 같다)
            }
            else
            {
                var b = _jointSamples[1];
                double k = (t - a.Key) / (b.Key - a.Key);
                q = JointsDeg;
                for (int i = 0; i < 6; i++) q[i] = a.Value[i] + (b.Value[i] - a.Value[i]) * k;
            }
            for (int i = 0; i < 6; i++)
            {
                JointsDeg[i] = q[i];
                _joints[i].localRotation = Quaternion.AngleAxis((float)q[i], JointAxes[i]);
            }
        }

        // ---------- IO·상태 신호 ----------

        void InsertSignal(in RobotSample s)
        {
            int i = _pendingSignals.Count;
            while (i > 0 && _pendingSignals[i - 1].SourceUtc > s.SourceUtc) i--;
            _pendingSignals.Insert(i, s);
        }

        void ApplySignal(in RobotSample s, double playback)
        {
            // 관절각이 이 신호의 시각에 맞춰 있어야 박스를 그리퍼의 그 순간 위치에 붙이고 뗄 수 있다
            ApplyJoints(ToMs(s.SourceUtc));
            switch (s.Node)
            {
                case RobotNodes.VacuumOk:
                    bool ok = (bool)s.Value;
                    if (ok && !VacuumOk) Grip(ChangedDuringOutage(s));
                    else if (!ok && VacuumOk)
                    {
                        // 놓인 순간의 그리퍼 위치를 믿을 수 없으면 위치를 지어내지 않고 지운다:
                        //  - 끊긴 동안 놓음 (값의 SourceTimestamp가 재접속 이전) — 지금 그리퍼 위치에 두면 거짓 화면
                        //  - 관절각 Bad 동안 놓음 — 트윈 자세가 마지막 정상 자세에 멈춰 있다
                        if (ChangedDuringOutage(s)) DropUnknown("통신 끊김 중 놓인 박스");
                        else if (JointDataBad) DropUnknown("관절각 Bad 중 놓인 박스");
                        else Release();
                    }
                    if (ok) VacuumFault = false;
                    VacuumOk = ok;
                    break;
                case RobotNodes.VacuumOn:
                    bool on = (bool)s.Value;
                    if (on && !VacuumOn) _vacuumOnAtMs = ToMs(s.SourceUtc);
                    VacuumOn = on;
                    break;
                case RobotNodes.PalletPresent:
                    bool present = (bool)s.Value;
                    if (present != PalletPresent) OnPalletChanged(present);
                    PalletPresent = present;
                    break;
                case RobotNodes.MotorsOn:
                    MotorsOn = (bool)s.Value;
                    _haveState = true;   // 접속 직후 기본값(false)으로 "모터 OFF" 알람을 내지 않게
                    break;
                case RobotNodes.EmergencyStop: EmergencyStop = (bool)s.Value; break;
                case RobotNodes.ProtectiveStop: ProtectiveStop = (bool)s.Value; break;
                case RobotNodes.OperationalMode: Mode = (OperationalMode)(int)s.Value; break;
                case RobotNodes.ExecutionState: Execution = (ProgramExecutionState)(int)s.Value; break;
                case RobotNodes.CycleCount:
                    uint cycles = (uint)s.Value;
                    if (ChangedDuringOutage(s) && cycles > _cycleBeforeOutage)
                    {
                        // 끊긴 동안 완료한 사이클의 박스는 놓인 순간을 못 봤으므로 그리지 않는다 (위치를 지어내지 않음)
                        AddWarning($"끊긴 동안 적재 {cycles - _cycleBeforeOutage}개: 놓인 위치를 볼 수 없어 팔레트에 표시하지 않음");
                        _cycleBeforeOutage = cycles;
                    }
                    CycleCount = cycles;
                    break;
                case RobotNodes.SpeedOverride: SpeedOverride = (double)s.Value; break;
            }
        }

        // ---------- 물건 인계 ----------

        /// <summary>출구 해제로 벨트를 떠난 박스: 압력 스위치를 잠시 기다린다 (ItemTracker.ExitHandoff)</summary>
        bool OnItemLeftExit(TrackedItem item)
        {
            if (_handoff != null) Destroy(_handoff.View);   // 받아 가지 않은 이전 박스
            _handoff = item;
            _handoffAtMs = conveyor.PlaybackMs;
            return true;
        }

        void ExpireHandoff(double playback)
        {
            if (_handoff == null || playback - _handoffAtMs < HandoffWindowMs) return;
            // 압력 스위치 없이 출구를 떠남: 로봇 통신이 끊겨 흡착 신호를 못 받았거나, 로봇이 집지 않았는데 사라진 박스
            AddWarning(_link != null && !_link.IsConnected
                ? $"Item_{_handoff.Id:D4}: 로봇 통신 끊김 중 출구를 떠남 (로봇이 집었는지 확인 불가)"
                : $"Item_{_handoff.Id:D4}: 출구를 떠났지만 로봇 흡착 신호가 없음");
            Destroy(_handoff.View);
            _handoff = null;
        }

        /// <param name="duringOutage">끊긴 동안 켜진 흡착 — 어느 박스를 집었는지 트윈이 보지 못했다</param>
        void Grip(bool duringOutage = false)
        {
            if (_held != null)
            {
                AddWarning("이미 박스를 든 상태에서 흡착 신호가 다시 켜짐");
                return;
            }
            // 출구 해제가 먼저 처리됐으면 대기 중인 박스, 아니면 아직 출구에 있는 박스를 가져온다.
            // 단 끊긴 동안 집은 것이면 지금 출구에 있는 박스는 다음 박스다 — 벨트에서 가져오지 않는다 (7일 차 시험에서 발견)
            var item = duringOutage ? null : _handoff ?? conveyor.Tracker.ClaimAtExit();
            if (!duringOutage) _handoff = null;
            if (duringOutage) AddWarning("끊긴 동안 집은 박스: 어느 박스인지 볼 수 없어 출처 불명으로 표시");
            GameObject view;
            if (item != null)
            {
                view = item.View;
            }
            else
            {
                // 트윈이 추적하지 못한 박스를 집음 (재동기화 직후, 끊긴 동안 등): 그리퍼에 기본 크기 박스를 보인다
                if (!duringOutage) AddWarning("추적 밖 물건을 흡착함");
                view = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(view.GetComponent<Collider>());
                view.GetComponent<Renderer>().sharedMaterial = conveyor.BoxMaterial;
                view.transform.localScale = new Vector3((float)RobotCell.BoxWidthM, (float)RobotCell.BoxHeightM, (float)RobotCell.BoxLengthM);
                // 박스 윗면 = 흡착면, 박스 긴 변 = 반송 방향(Unity +Z, 베이스 −X)
                view.transform.SetPositionAndRotation(_tcp.position + Vector3.down * (float)RobotCell.BoxHeightM * 0.5f,
                    Quaternion.LookRotation(Vector3.forward, Vector3.up));
            }
            view.GetComponent<Renderer>().sharedMaterial = conveyor.BoxMaterial;   // 잼 표시 등은 그리퍼에서 해제

            // 압력 스위치 ON = 박스 윗면이 흡착 패드(TCP)에 붙어 있다는 사실. 박스를 TCP에 맞춰 붙인다.
            // 정상이면 벨트 위 추정 위치와 몇 mm 차이(박스 길이 추정 오차)지만, 관절각이 Bad라 트윈 자세가 멈춰 있던 동안
            // 집은 경우에는 1m 넘게 어긋나 그대로 붙이면 팔레트에도 엉뚱하게 놓인다 (7일 차 시험에서 발견).
            var t = view.transform;
            var top = t.position + t.up * t.lossyScale.y * 0.5f;
            LastGripOffsetM = (top - _tcp.position).magnitude;
            LastGripDuringBadData = JointDataBad || duringOutage;
            t.position = _tcp.position - t.up * t.lossyScale.y * 0.5f;
            t.SetParent(_tcp, true);
            _held = view;
            GripCount++;
        }

        /// <summary>재접속 뒤 받은 값 중 끊긴 동안 바뀐 것 (SourceTimestamp가 재접속 이전)</summary>
        bool ChangedDuringOutage(in RobotSample s) => _reconnectedMs > 0 && ToMs(s.SourceUtc) < _reconnectedMs;

        /// <summary>놓인 위치를 알 수 없는 박스: 지어내지 않고 지운다</summary>
        void DropUnknown(string why)
        {
            if (_held == null) return;
            AddWarning(why + ": 놓인 위치를 알 수 없어 표시하지 않음");
            Destroy(_held);
            _held = null;
        }

        void Release()
        {
            if (_held == null)
            {
                AddWarning("든 박스가 없는데 흡착 해제 신호");
                return;
            }
            _held.transform.SetParent(_stack, true);   // 그 자리에 둔다
            _held = null;
        }

        void OnPalletChanged(bool present)
        {
            if (!present)
            {
                // 가득 찬 팔레트를 뺐다: 위에 쌓인 박스와 함께 사라진다
                foreach (Transform box in _stack) Destroy(box.gameObject);
                AddWarning($"팔레트 교체: 박스 {_stack.childCount}개와 함께 반출");
            }
            foreach (var r in pallet.GetComponentsInChildren<Renderer>(true))
                if (!r.transform.IsChildOf(_stack)) r.enabled = present;
        }

        // ---------- 알람·표시 ----------

        /// <summary>로봇 셀 알람 조건 (ConveyorTwinController.AppendConditions). 모두 원시 신호의 조합이다</summary>
        void EvaluateConditions(List<AlarmCondition> into)
        {
            bool connected = _link != null && _link.IsConnected;
            if (!connected && (_everConnected || Time.unscaledTime > StartupGraceS))
                into.Add(new AlarmCondition("robot_link", AlarmSeverity.Critical,
                    $"로봇 OPC UA 연결 끊김 ({LinkState}): 로봇은 마지막 수신 자세"));
            if (ExitWaitS * 1000 > ExitWaitWarnMs)
                into.Add(new AlarmCondition("exit_wait", AlarmSeverity.Warning,
                    $"출구 대기 {ExitWaitS:F0}s: 박스가 스토퍼에 있는데 로봇이 집지 않음"));
            if (!connected || !_haveState) return;   // 이하 로봇 상태는 끊긴 동안·첫 값을 받기 전에는 믿을 수 없다

            if (EmergencyStop)
                into.Add(new AlarmCondition("robot_estop", AlarmSeverity.Critical, "로봇 비상정지 (모터 전원 차단)"));
            if (ProtectiveStop)
                into.Add(new AlarmCondition("robot_pstop", AlarmSeverity.Critical, "로봇 보호정지"));
            if (!MotorsOn && !EmergencyStop)
                into.Add(new AlarmCondition("robot_motors_off", AlarmSeverity.Warning, "로봇 모터 OFF"));
            if (MotorsOn && Mode == OperationalMode.Automatic && Execution == ProgramExecutionState.Stopped)
                into.Add(new AlarmCondition("robot_program_stopped", AlarmSeverity.Warning, "로봇 프로그램 정지 (자동 모드)"));
            if (VacuumFault)
                into.Add(new AlarmCondition("robot_vacuum", AlarmSeverity.Critical, VacuumOn
                    ? $"흡착 실패: 진공 출력 ON, 압력 스위치 OFF {VacuumWaitS:F1}s"
                    : "흡착 실패: 압력 스위치가 잡히지 않아 진공 해제됨"));
            if (JointDataBad)
                into.Add(new AlarmCondition("robot_joint_bad", AlarmSeverity.Warning,
                    "로봇 관절각 StatusCode Bad: 마지막 정상 자세 유지"));
        }

        void UpdateVisuals()
        {
            if (_bodyMaterials.Count == 0) return;
            bool connected = _link != null && _link.IsConnected;
            Color color = Color.clear;
            float strength = 0f, pulse = 0f, desat = 0f;
            if (!connected && _everConnected) desat = 1f;                                      // 끊김: 회색조
            else if (EmergencyStop || ProtectiveStop) { color = new Color(2.5f, 0.3f, 0.2f); strength = 1f; pulse = 2f; }
            else if (VacuumFault) { color = new Color(2.5f, 1.2f, 0.1f); strength = 1f; pulse = 2f; }
            else if (MotorsOn && Execution == ProgramExecutionState.Stopped) { color = new Color(2.0f, 1.6f, 0.2f); strength = 0.5f; }
            if (connected && JointDataBad) desat = Mathf.Max(desat, 0.6f);                      // 측정을 믿을 수 없음
            foreach (var m in _bodyMaterials)
            {
                m.SetColor(IdHighlightColor, color);
                m.SetFloat(IdHighlightStrength, strength);
                m.SetFloat(IdPulseHz, pulse);
                m.SetFloat(IdDesaturate, desat);
            }
        }

        // ---------- 도우미 ----------

        static double ToMs(DateTime utc) => (utc - DateTime.UnixEpoch).TotalMilliseconds;

        static string Short(string node) => node.Substring(node.LastIndexOf('.') + 1);

        void AddWarning(string text)
        {
            Warnings.Insert(0, $"{DateTime.Now:HH:mm:ss} {text}");
            if (Warnings.Count > 8) Warnings.RemoveAt(Warnings.Count - 1);
            Debug.LogWarning("[Robot] " + text);
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            throw new InvalidOperationException($"'{name}' 노드를 찾을 수 없습니다 (Tools/Twin/Setup Scene)");
        }

        void OnDestroy()
        {
            if (conveyor != null && conveyor.Tracker != null) conveyor.Tracker.ExitHandoff = null;
            if (conveyor != null) conveyor.AppendConditions = null;
            foreach (var m in _bodyMaterials) Destroy(m);
        }
    }
}
