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

        [SerializeField] ConveyorTwinController conveyor;
        [SerializeField] Transform robotRoot;
        [SerializeField] Transform pallet;

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
                    if (_seenConnectCount > 0) AddWarning("로봇 OPC UA 재접속");
                    _seenConnectCount = _link.ConnectCount;
                }
                if (_link.IsConnected) _gate.ObserveServerStart(_link.ServerStartUtc);

                while (_link.TryDequeue(out var s))
                {
                    if (!_gate.Accept(s, nowUtc, out var reason))
                    {
                        if (reason != null) AddWarning($"로봇 값 거부 {Short(s.Node)}: {reason}");
                        continue;
                    }
                    double t = ToMs(s.SourceUtc);
                    if (s.Node == RobotNodes.JointPositions)
                    {
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

            // IO·상태 신호: 재생 시각에 도달한 것부터 순서대로 (초기값처럼 오래된 시각의 값은 바로 적용)
            while (_pendingSignals.Count > 0 && ToMs(_pendingSignals[0].SourceUtc) <= playback)
            {
                ApplySignal(_pendingSignals[0], playback);
                _pendingSignals.RemoveAt(0);
            }

            ApplyJoints(playback);
            ExpireHandoff(playback);
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
                    if (ok && !VacuumOk) Grip();
                    else if (!ok && VacuumOk) Release();
                    VacuumOk = ok;
                    break;
                case RobotNodes.VacuumOn: VacuumOn = (bool)s.Value; break;
                case RobotNodes.PalletPresent:
                    bool present = (bool)s.Value;
                    if (present != PalletPresent) OnPalletChanged(present);
                    PalletPresent = present;
                    break;
                case RobotNodes.MotorsOn: MotorsOn = (bool)s.Value; break;
                case RobotNodes.EmergencyStop: EmergencyStop = (bool)s.Value; break;
                case RobotNodes.ProtectiveStop: ProtectiveStop = (bool)s.Value; break;
                case RobotNodes.OperationalMode: Mode = (OperationalMode)(int)s.Value; break;
                case RobotNodes.ExecutionState: Execution = (ProgramExecutionState)(int)s.Value; break;
                case RobotNodes.CycleCount: CycleCount = (uint)s.Value; break;
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
            // 압력 스위치 없이 출구를 떠남: 로봇이 집지 않았는데 사라진 박스 (사람이 치웠거나 신호 유실)
            AddWarning($"Item_{_handoff.Id:D4}: 출구를 떠났지만 로봇 흡착 신호가 없음");
            Destroy(_handoff.View);
            _handoff = null;
        }

        void Grip()
        {
            if (_held != null)
            {
                AddWarning("이미 박스를 든 상태에서 흡착 신호가 다시 켜짐");
                return;
            }
            // 출구 해제가 먼저 처리됐으면 대기 중인 박스, 아니면 아직 출구에 있는 박스를 가져온다
            var item = _handoff ?? conveyor.Tracker.ClaimAtExit();
            _handoff = null;
            GameObject view;
            if (item != null)
            {
                view = item.View;
            }
            else
            {
                // 트윈이 추적하지 못한 박스를 집음 (재동기화 직후 등): 그리퍼에 기본 크기 박스를 보인다
                AddWarning("추적 밖 물건을 흡착함");
                view = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(view.GetComponent<Collider>());
                view.GetComponent<Renderer>().sharedMaterial = conveyor.BoxMaterial;
                view.transform.localScale = new Vector3((float)RobotCell.BoxWidthM, (float)RobotCell.BoxHeightM, (float)RobotCell.BoxLengthM);
                // 박스 윗면 = 흡착면, 박스 긴 변 = 반송 방향(Unity +Z, 베이스 −X)
                view.transform.SetPositionAndRotation(_tcp.position + Vector3.down * (float)RobotCell.BoxHeightM * 0.5f,
                    Quaternion.LookRotation(Vector3.forward, Vector3.up));
            }
            view.GetComponent<Renderer>().sharedMaterial = conveyor.BoxMaterial;   // 잼 표시 등은 그리퍼에서 해제
            view.transform.SetParent(_tcp, true);
            _held = view;
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
        }
    }
}
