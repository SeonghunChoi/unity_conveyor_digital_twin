using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>수신한 측정값 하나의 현재 상태</summary>
    public sealed class SignalValue
    {
        public readonly SignalSpec Spec;
        public double Value;           // 마지막으로 받아들인 값 (이상값은 반영하지 않음)
        public Quality Quality;
        public long Ts;                // 서버 발행 시각
        public long ReceivedMs;        // 받아들인 값의 트윈 수신 시각
        /// <summary>마지막 도착 시각 (거부한 이상값 포함). stale은 "아무것도 안 옴"이라 이 값으로 판정한다</summary>
        public long ArrivedMs;
        public bool HasValue;

        public long Rejected;          // 거부한 이상값 수
        public string LastRejectReason;
        public long LastRejectMs;

        public SignalValue(SignalSpec spec) => Spec = spec;

        /// <summary>주기 신호가 주기의 3배 동안 오지 않음 (SignalSpec.StaleAfterMs)</summary>
        /// <remarks>
        /// 이상값은 도착은 하므로 stale이 아니다. 같은 원인으로 알람이 두 개 뜨지 않게
        /// "통신 문제(stale)"와 "센서 문제(이상값)"를 구분한다.
        /// </remarks>
        public bool IsStale(long nowMs) => Spec.StaleAfterMs > 0 && (ArrivedMs == 0 || nowMs - ArrivedMs > Spec.StaleAfterMs);
    }

    /// <summary>
    /// 트윈 본체. MQTT로 받은 원시 신호를 검증·해석해 3D 모델(드럼 회전, 물건 박스)에 반영한다.
    /// 트윈은 값을 만들지 않는다 (CLAUDE.md 설계 원칙 1). 물건 위치처럼 신호 사이를 채우는 값은
    /// 받은 속도로 추정하고, 다음 센서 신호에서 보정한다.
    ///
    /// 재생 시계 (게임 네트코드의 스냅샷 보간과 같은 개념):
    /// 화면은 트윈의 시계가 아니라 서버 시각(ts)을 따라가는 재생 시계로 움직인다.
    /// - 목표 재생 시각 = 현재 시각 - 기준 지연 - PlaybackDelayMs. 기준 지연은 최근 1.5초 지연의 최솟값이라
    ///   메시지마다 다른 지터(순서 뒤섞기)에 흔들리지 않고, 지연 자체가 바뀌면 따라간다
    /// - 재생 시각은 목표로 순간이동하지 않고 배속을 조절해 따라간다 (뒤처지면 최대 3배속, 뒤로는 가지 않음)
    /// - 데이터가 끊기면 최신 ts + PlaybackHoldMs에서 멈춰 기다리고, 늦은 데이터가 오면 그 구간을 이어서 재생한다.
    ///   그래서 전송 지연 중에도 벨트가 움직인 거리를 잃지 않는다 (화면은 지연만큼 늦게 보인다)
    /// - 센서 이벤트는 재생 시각이 그 ts에 도달할 때 적용한다. PlaybackDelayMs 안에 늦게 도착한 이벤트는
    ///   자동으로 제 순서를 찾는다 (= docs/protocol.md 4절의 재정렬 버퍼)
    ///
    /// 수신 검증 (docs/protocol.md 4절, 원칙 4 "믿되 맹신하지 않는다"):
    /// - 세션: sid가 바뀌면 seq 기록과 물건 추적을 초기화
    /// - 중복·과거: 같은 세션에서 seq가 마지막 처리 값 이하이면 폐기
    /// - 이상값: SignalSpec 측정 범위 밖이거나 quality bad면 표시값에 반영하지 않음
    /// - 끊김: 주기의 3배 동안 안 오면 stale, status offline이면 설비 오프라인
    /// </summary>
    public sealed class ConveyorTwinController : MonoBehaviour
    {
        const string BrokerHost = "127.0.0.1";
        const int BrokerPort = 1883;
        /// <summary>벨트를 감은 드럼 외경의 반지름 (docs/cad-pipeline.md 6절). 벨트 속도 → 드럼 각속도</summary>
        const float BeltOuterRadiusM = 0.0275f;
        /// <summary>재생 지연 = 재정렬 버퍼 (docs/protocol.md 4절). 서버의 순서 뒤섞기(최대 250ms)보다 커야 한다</summary>
        const long PlaybackDelayMs = 300;
        /// <summary>최신 데이터보다 이만큼까지만 앞서 재생한다. 속도 주기(500ms)의 2배: 지터가 있어도 정상 수신 간격은 끊김 없이 넘긴다</summary>
        const long PlaybackHoldMs = 1000;
        /// <summary>기준 지연(최근 지연의 최솟값)을 구하는 구간</summary>
        const long LatencyWindowMs = 1500;
        /// <summary>목표와의 차이가 이만큼이면 재생 속도를 2배(또는 0배)로 조절</summary>
        const double PlaybackSlewMs = 500;
        /// <summary>따라잡을 때 최대 재생 배속</summary>
        const double PlaybackMaxRate = 3.0;
        /// <summary>재생 시계가 이보다 크게 뒤처지면 이어서 재생하지 않고 따라잡는다 (데이터가 없는 긴 공백)</summary>
        const long PlaybackMaxCatchUpMs = 10000;
        /// <summary>화면 지연이 이를 넘으면 경고</summary>
        const long ScreenDelayWarnMs = 1000;
        /// <summary>이상값 경고를 유지하는 시간</summary>
        public const long RejectAlarmHoldMs = 3000;
        /// <summary>
        /// 트윈 쪽 알람 기준값. 측정 범위(SignalSpec)와 달리 "정상 운전으로 보기 어려운 값"이다.
        /// 정상 운전 전류는 약 1.8~2.5A (docs/protocol.md 7절), 걸림 시 +1.2A
        /// </summary>
        public const double CurrentHighA = 3.0;
        public const double TempHighC = 70.0;

        [SerializeField] Transform conveyorRoot;
        [SerializeField] string deviceId = Topics.DefaultDeviceId;
        [SerializeField] Color boxColor = new Color(0.76f, 0.60f, 0.42f);
        [SerializeField] Color jamColor = new Color(0.90f, 0.15f, 0.12f);
        /// <summary>ConveyorTwin/StatusLit (Setup Scene이 지정. 빌드에 셰이더가 포함되도록 직접 참조)</summary>
        [SerializeField] Shader statusShader;

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
        };

        /// <summary>재생 시각을 기다리는 이벤트</summary>
        struct PendingEvent
        {
            public ReceivedMessage Raw;
            public Envelope Message;
            public int Order;      // 같은 ts일 때 처리 순서: 입구(0) → 무게(1) → 출구(2)
        }

        MqttLink _link;
        int _seenConnectCount;
        readonly MessageGate _gate = new MessageGate();
        readonly List<PendingEvent> _pending = new List<PendingEvent>();
        /// <summary>벨트 속도 샘플 (ts, 값). 재생 시각 당시의 속도를 찾는 데 쓴다</summary>
        readonly List<KeyValuePair<long, double>> _speedHistory = new List<KeyValuePair<long, double>>();
        ItemTracker _tracker;
        Material _boxMaterial, _jamMaterial;

        Transform _drumHead, _drumTail;
        Quaternion _drumHeadBase, _drumTailBase;
        Vector3 _entryPoint, _flowDir, _drumAxis;
        float _beltTopY;
        double _drumAngleDeg;

        double _playbackMs;            // 재생 시각 (서버 시각, ms)
        int _sessionEpoch;             // 세션이 바뀔 때마다 증가 (재생 루프 중 초기화 감지용)
        long _lastFrameMs;
        /// <summary>최근 메시지의 (수신 시각, 지연). 최솟값이 기준 지연</summary>
        readonly Queue<KeyValuePair<long, long>> _latencyWindow = new Queue<KeyValuePair<long, long>>();

        // 토픽 문자열은 Shared의 Topics로만 만든다
        string _tStatus, _tRun, _tSpeed, _tTemp, _tCurrent, _tWeight, _tEntry, _tExit;

        // ---- HUD가 읽는 상태 ----
        public bool BrokerConnected => _link != null && _link.IsConnected;
        public string BrokerError => _link?.LastError;
        public bool? DeviceOnline { get; private set; }
        public long OfflineSinceMs { get; private set; }
        public string Sid => _gate.Sid;
        public long Dropped => _gate.Dropped;
        public RunState? RunState { get; private set; }
        public string RunReason { get; private set; }
        public SignalValue Speed { get; } = new SignalValue(Signals.BeltSpeed);
        public SignalValue Temp { get; } = new SignalValue(Signals.MotorTemp);
        public SignalValue Current { get; } = new SignalValue(Signals.MotorCurrent);
        public SignalValue Weight { get; } = new SignalValue(Signals.LoadWeight);
        public ItemTracker Tracker => _tracker;
        /// <summary>재생 시각 (서버 시각, Unix ms). 로봇 미러(RobotTwin)도 이 시계로 관절각을 재생한다. 0이면 아직 시작 전</summary>
        public double PlaybackMs => _playbackMs;
        /// <summary>박스 머티리얼 (로봇이 든 박스·팔레트 위 박스도 같은 모양)</summary>
        public Material BoxMaterial => _boxMaterial;
        /// <summary>네트워크 지연: 마지막 메시지의 수신 시각 - ts</summary>
        public long LastLatencyMs { get; private set; }
        public long Received { get; private set; }
        public long Reordered { get; private set; }
        /// <summary>데이터 시계: 받아들인 실시간(비 retained) 메시지의 최신 ts (서버 시각)</summary>
        public long DataClockMs { get; private set; }
        /// <summary>화면 지연: 지금 화면이 몇 ms 전의 서버 상태인지 (현재 시각 - 재생 시각)</summary>
        public long ScreenDelayMs => _playbackMs > 0 ? NowMs() - (long)_playbackMs : 0;
        /// <summary>
        /// 이번 프레임의 재생 배속. 1이면 실시간, 1보다 크면 밀린 화면을 따라잡는 중(화면 속도가 실제 설비 속도보다 빠름),
        /// 1보다 작으면 데이터를 기다리며 늦추는 중
        /// </summary>
        public double PlaybackRate { get; private set; } = 1.0;
        /// <summary>재생이 최신 데이터 끝에서 다음 데이터를 기다리는 중</summary>
        public bool PlaybackHolding { get; private set; }
        /// <summary>재생 시각 당시의 벨트 속도 (화면에 쓰는 속도)</summary>
        public double PlaybackSpeed { get; private set; }
        /// <summary>재생 시계 기준 벨트 표면 누적 이동 거리 (루프 길이로 감음)</summary>
        public double BeltTravelM { get; private set; }
        /// <summary>벨트 루프 전체 길이 = 2 × 드럼 간 거리 + 2πr</summary>
        public static double BeltLoopLengthM => 2.0 * Equipment.BeltLengthM + 2.0 * Math.PI * BeltOuterRadiusM;
        public TwinAlarms Alarms { get; } = new TwinAlarms();
        /// <summary>다른 설비(로봇 셀)의 알람 조건을 같은 알람 목록에 더한다 (RobotTwin이 설정)</summary>
        public Action<List<AlarmCondition>> AppendConditions;
        readonly List<AlarmCondition> _conditions = new List<AlarmCondition>();
        public Transform DrumHead => _drumHead;
        public Transform DrumTail => _drumTail;
        public Transform ConveyorRoot => conveyorRoot;
        public const float BeltRadiusM = BeltOuterRadiusM;
        public readonly List<string> Warnings = new List<string>();

        void Awake()
        {
            _tStatus = Topics.Status(deviceId);
            _tRun = Topics.RunState(deviceId);
            _tSpeed = Topics.BeltSpeed(deviceId);
            _tTemp = Topics.MotorTemp(deviceId);
            _tCurrent = Topics.MotorCurrent(deviceId);
            _tWeight = Topics.LoadWeight(deviceId);
            _tEntry = Topics.EntrySensor(deviceId);
            _tExit = Topics.ExitSensor(deviceId);

            ResolveGeometry();

            // 상태 셰이더가 지정되어 있으면 잼 박스를 맥동 하이라이트로, 없으면 URP Lit 단색으로
            var shader = statusShader != null ? statusShader : Shader.Find("Universal Render Pipeline/Lit");
            _boxMaterial = new Material(shader);
            _boxMaterial.SetColor("_BaseColor", boxColor);
            _jamMaterial = new Material(shader);
            _jamMaterial.SetColor("_BaseColor", statusShader != null ? boxColor : jamColor);
            if (statusShader != null)
            {
                _jamMaterial.SetColor("_HighlightColor", jamColor * 2.5f);
                _jamMaterial.SetFloat("_HighlightStrength", 1f);
                _jamMaterial.SetFloat("_PulseHz", 2f);
            }

            _tracker = new ItemTracker(CreateBoxView);
            _tracker.Warning += AddWarning;
            _tracker.JamDetected += i =>
                AddWarning($"잼: Item_{i.Id:D4} 입구 후 {i.TravelM:F2}m 이동했는데 출구 미감지 (전류 {Current.Value:F2}A)");
            _tracker.JamCleared += i => AddWarning($"잼 해소: Item_{i.Id:D4} 출구 감지");
            _gate.SessionChanged += OnSessionChanged;
        }

        /// <summary>센서 위치와 반송 방향을 CAD 드럼 위치에서 얻는다 (입구 = Drum_Tail, 출구 = Drum_Head)</summary>
        void ResolveGeometry()
        {
            if (conveyorRoot == null)
                throw new InvalidOperationException("conveyorRoot가 지정되지 않았습니다 (Tools/Twin/Setup Scene)");
            _drumHead = FindDeep(conveyorRoot, "Drum_Head");
            _drumTail = FindDeep(conveyorRoot, "Drum_Tail");
            _drumHeadBase = _drumHead.rotation;
            _drumTailBase = _drumTail.rotation;

            // 반송 방향 = 수평면에서 드럼 축에 수직인 방향 (드럼 노드의 로컬 X가 축).
            // 두 드럼 중심을 그냥 이으면 안 된다: 구동 드럼 노드는 구동축까지 포함한 바운딩 박스 중심이라
            // 축 방향(Unity +X)으로 11.75mm 치우쳐 있어, 박스 경로가 벨트 중심선에서 비스듬해진다 (2단계 6일 차에 발견).
            // 축 방향 치우침은 드럼 회전에는 영향이 없으므로 여기서만 빼면 된다.
            var axis = _drumTail.rotation * Vector3.right;
            axis.y = 0f;
            axis.Normalize();
            var span = _drumHead.position - _drumTail.position;
            span.y = 0f;
            span -= axis * Vector3.Dot(span, axis);
            _entryPoint = _drumTail.position;
            _flowDir = span.normalized;
            _beltTopY = _drumTail.position.y + BeltOuterRadiusM;
            // 벨트 윗면이 반송 방향으로 움직이도록 하는 드럼 회전축
            _drumAxis = Vector3.Cross(Vector3.up, _flowDir);

            if (Mathf.Abs(span.magnitude - (float)Equipment.BeltLengthM) > 0.01f)
                Debug.LogWarning($"[Twin] CAD 드럼 간 거리 {span.magnitude:F3}m가 Equipment.BeltLengthM {Equipment.BeltLengthM}m와 다릅니다");
        }

        void OnEnable()
        {
            _link = new MqttLink(BrokerHost, BrokerPort, Topics.AllOf(deviceId));
            _seenConnectCount = 0;
            _link.Start();
        }

        void OnDisable()
        {
            _link?.Dispose();
            _link = null;
            DeviceOnline = null;
        }

        void Update()
        {
            long now = NowMs();
            if (_link != null)
            {
                // 트윈 쪽 재접속: 끊긴 동안의 엣지를 놓쳤으므로 추적을 다시 맞춘다
                if (_link.ConnectCount != _seenConnectCount)
                {
                    if (_seenConnectCount > 0)
                        AddWarning("브로커 재접속: 끊긴 동안의 센서 신호 유실 → 추적 재동기화");
                    _seenConnectCount = _link.ConnectCount;
                    ResyncTracking();
                }

                while (_link.TryDequeue(out var m))
                {
                    try { Route(m); }
                    catch (Exception e) { AddWarning($"해석 실패 {m.Topic}: {e.Message}"); }
                }
            }

            Playback(now);
            PlaybackSpeed = SpeedAt((long)_playbackMs);
            foreach (var item in _tracker.Items)
            {
                _tracker.Place(item, _entryPoint, _flowDir, _beltTopY);
                item.View.GetComponent<Renderer>().sharedMaterial = item.Jammed ? _jamMaterial : _boxMaterial;
            }

            var spin = Quaternion.AngleAxis((float)_drumAngleDeg, _drumAxis);
            _drumHead.rotation = spin * _drumHeadBase;
            _drumTail.rotation = spin * _drumTailBase;

            EvaluateConditions(_conditions);
            Alarms.Evaluate(_conditions, now);
        }

        /// <summary>
        /// 재생 시계를 목표 시각까지 진행한다. 그 사이에 든 이벤트 시각마다 끊어서
        /// "그 시각까지 이동 → 이벤트 적용"을 반복한다. 지연이 풀리며 데이터가 몰려와 재생이 크게 건너뛰어도
        /// 물건 이동과 센서 이벤트가 서버에서 일어난 순서 그대로 맞물린다.
        /// </summary>
        void Playback(long now)
        {
            double frameMs = _lastFrameMs > 0 ? now - _lastFrameMs : 0;
            _lastFrameMs = now;
            if (DataClockMs == 0)
                return;

            // 목표 = 현재 시각 - 기준 지연 - 재생 지연. 기준 지연은 최근 지연의 최솟값 (지터에 흔들리지 않는 경로 지연)
            while (_latencyWindow.Count > 1 && _latencyWindow.Peek().Key < now - LatencyWindowMs)
                _latencyWindow.Dequeue();
            long baseLatency = long.MaxValue;
            foreach (var s in _latencyWindow) baseLatency = Math.Min(baseLatency, s.Value);
            double target = now - baseLatency - PlaybackDelayMs;
            // 받은 데이터보다 PlaybackHoldMs 넘게 앞서지 않는다 (데이터가 끊기면 여기서 기다림)
            double limit = DataClockMs + PlaybackHoldMs - PlaybackDelayMs;

            if (_playbackMs <= 0 || Math.Min(target, limit) - _playbackMs > PlaybackMaxCatchUpMs)
            {
                _playbackMs = Math.Min(target, limit);   // 처음 시작하거나 데이터 없는 긴 공백: 이동 없이 따라잡는다
                return;
            }

            // 순간이동하지 않고 재생 속도를 조절해 목표를 따라간다: 뒤처지면 최대 3배속, 앞서면 느리게 (뒤로는 가지 않음)
            double error = target - _playbackMs;
            double rate = Math.Max(0.0, Math.Min(PlaybackMaxRate, 1.0 + error / PlaybackSlewMs));
            double next = _playbackMs + frameMs * rate;
            // 뒤처졌을 때 빨리 감되 목표를 넘지 않는다 (에디터 일시 멈춤처럼 긴 프레임에서 재정렬 여유를 잃지 않게)
            if (error > 0) next = Math.Min(next, target);
            next = Math.Min(next, limit);
            // 실제 재생 배속 (화면 속도 / 실제 속도). 1보다 크면 밀린 화면을 따라잡는 중
            PlaybackRate = frameMs > 0 ? Math.Max(0.0, next - _playbackMs) / frameMs : 1.0;
            PlaybackHolding = next >= limit && target > limit;
            target = Math.Max(next, _playbackMs);

            int session = _sessionEpoch;
            while (_pending.Count > 0 && _pending[0].Message.Ts <= target)
            {
                var ev = _pending[0];
                _pending.RemoveAt(0);
                if (ev.Message.Ts > _playbackMs)
                    StepTo(ev.Message.Ts);
                ApplyEvent(ev);
                // 이 이벤트로 세션이 바뀌면 재생 시계가 초기화되었으므로 이번 프레임은 여기서 멈춘다
                if (session != _sessionEpoch) return;
            }
            if (target > _playbackMs)
                StepTo(target);
        }

        /// <summary>재생 시각을 t까지 옮기며 물건 이동·잼 판단·드럼 회전을 진행</summary>
        void StepTo(double t)
        {
            if (_playbackMs <= 0)
            {
                // 재생 시계가 초기화된 직후: 이동 없이 시각만 맞춘다
                _playbackMs = t;
                return;
            }
            double dt = (t - _playbackMs) / 1000.0;
            double speed = SpeedAt((long)_playbackMs);
            _tracker.Tick(dt, speed);
            // 드럼 회전: 각속도 = 벨트 속도 / 반지름
            _drumAngleDeg = (_drumAngleDeg + speed / BeltOuterRadiusM * dt * Mathf.Rad2Deg) % 360.0;
            // 벨트 표면 이동 거리 (셰이더 줄무늬용). 오래 돌아도 float 정밀도를 잃지 않게 루프 길이로 감는다
            BeltTravelM = (BeltTravelM + speed * dt) % BeltLoopLengthM;
            _playbackMs = t;
        }

        /// <summary>재생 시각 당시의 벨트 속도: ts가 그 시각 이하인 마지막 샘플</summary>
        double SpeedAt(long t)
        {
            if (_speedHistory.Count == 0) return 0.0;
            for (int i = _speedHistory.Count - 1; i >= 0; i--)
                if (_speedHistory[i].Key <= t) return _speedHistory[i].Value;
            return _speedHistory[0].Value;
        }

        void Route(in ReceivedMessage m)
        {
            Received++;

            if (m.Topic == _tStatus)
            {
                var s = JsonConvert.DeserializeObject<StatusMessage>(m.Payload, JsonSettings);
                _gate.ObserveSession(s.Sid);
                if (DeviceOnline == true && !s.Online)
                {
                    OfflineSinceMs = m.ReceivedMs;   // Will의 ts는 접속 시각이라 끊긴 시각으로는 수신 시각을 쓴다
                    AddWarning("설비 오프라인 (Last Will)");
                }
                else if (DeviceOnline == false && s.Online)
                {
                    AddWarning("설비 연결 복구: 끊긴 동안의 센서 신호 유실 → 추적 재동기화");
                    ResyncTracking();
                }
                else if (DeviceOnline == null && !s.Online)
                {
                    OfflineSinceMs = m.ReceivedMs;
                }
                DeviceOnline = s.Online;
                return;
            }

            if (m.Topic == _tEntry || m.Topic == _tExit || m.Topic == _tWeight)
            {
                // 이벤트는 재생 시각이 ts에 도달할 때 적용한다 (ts 순서로 정렬해 보관)
                Envelope msg = m.Topic == _tWeight
                    ? (Envelope)JsonConvert.DeserializeObject<TelemetryMessage>(m.Payload, JsonSettings)
                    : JsonConvert.DeserializeObject<SensorEdgeMessage>(m.Payload, JsonSettings);
                AdvanceDataClock(m, msg);
                int order = m.Topic == _tEntry ? 0 : m.Topic == _tWeight ? 1 : 2;
                var ev = new PendingEvent { Raw = m, Message = msg, Order = order };
                int idx = _pending.FindIndex(p => Compare(ev, p) < 0);
                if (idx >= 0) { _pending.Insert(idx, ev); Reordered++; }
                else _pending.Add(ev);
                return;
            }

            if (m.Topic == _tRun)
            {
                var r = JsonConvert.DeserializeObject<RunStateMessage>(m.Payload, JsonSettings);
                if (!Accept(m, r)) return;
                if (r.State == Shared.RunState.Fault && RunState != Shared.RunState.Fault)
                    AddWarning($"모터 정지: {r.Reason}");
                else if (r.State == Shared.RunState.Running && RunState == Shared.RunState.Fault)
                    AddWarning("모터 재가동");
                RunState = r.State;
                RunReason = r.Reason;
                return;
            }

            var target = m.Topic == _tSpeed ? Speed : m.Topic == _tTemp ? Temp : m.Topic == _tCurrent ? Current : null;
            if (target == null)
                return;
            var t = JsonConvert.DeserializeObject<TelemetryMessage>(m.Payload, JsonSettings);
            if (!Accept(m, t) || !ApplyTelemetry(target, t, m.ReceivedMs))
                return;
            if (target == Speed)
                AddSpeedSample(t.Ts, t.Value);
        }

        void AddSpeedSample(long ts, double value)
        {
            // ts 순서 유지 (뒤섞여 도착해도)
            int i = _speedHistory.Count;
            while (i > 0 && _speedHistory[i - 1].Key > ts) i--;
            _speedHistory.Insert(i, new KeyValuePair<long, double>(ts, value));
            // 재생 시각보다 충분히 오래된 샘플 정리 (재생 시각 이전 샘플은 하나만 있으면 된다)
            while (_speedHistory.Count > 2 && _speedHistory[1].Key < _playbackMs - 5000)
                _speedHistory.RemoveAt(0);
        }

        /// <summary>재생 시각에 도달한 이벤트 하나를 적용 (ts 순으로 꺼낸 뒤 seq 검사)</summary>
        void ApplyEvent(in PendingEvent ev)
        {
            if (!_gate.Accept(ev.Raw.Topic, ev.Message))
                return;
            try
            {
                if (ev.Message is SensorEdgeMessage e)
                {
                    // 엣지를 하나라도 잃으면 FIFO 짝이 어긋나므로, 틀린 화면을 보이지 말고 다시 맞춘다
                    if (_gate.LastGap > 0)
                    {
                        AddWarning($"엣지 유실 감지: {ev.Raw.Topic} seq {_gate.LastGap}건 누락 → 추적 재동기화");
                        _tracker.Resync();
                    }
                    if (ev.Raw.Topic == _tEntry)
                        _tracker.OnEntryEdge(e.Blocked, e.Ts, (long)_playbackMs, SpeedAt((long)_playbackMs));
                    else
                        _tracker.OnExitEdge(e.Blocked);
                }
                else if (ev.Message is TelemetryMessage w && ApplyTelemetry(Weight, w, ev.Raw.ReceivedMs))
                {
                    _tracker.OnWeight(w.Value);
                }
            }
            catch (Exception ex) { AddWarning($"이벤트 처리 실패 {ev.Raw.Topic}: {ex.Message}"); }
        }

        static int Compare(in PendingEvent a, in PendingEvent b)
        {
            int c = a.Message.Ts.CompareTo(b.Message.Ts);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        }

        /// <summary>sid/seq 검사 후 데이터 시계 갱신</summary>
        bool Accept(in ReceivedMessage m, Envelope msg)
        {
            if (!_gate.Accept(m.Topic, msg))
                return false;
            AdvanceDataClock(m, msg);
            return true;
        }

        /// <summary>
        /// 데이터 시계와 지연 갱신. retained 메시지는 과거 값이라 시계에 쓰지 않는다.
        /// 뒤섞여 늦게 온 메시지(ts가 더 작음)는 시계를 되돌리지 않는다.
        /// </summary>
        void AdvanceDataClock(in ReceivedMessage m, Envelope msg)
        {
            if (m.Retained)
                return;
            LastLatencyMs = m.ReceivedMs - msg.Ts;
            _latencyWindow.Enqueue(new KeyValuePair<long, long>(m.ReceivedMs, LastLatencyMs));
            if (msg.Ts > DataClockMs)
                DataClockMs = msg.Ts;
        }

        /// <summary>이상값 판정 후 반영. 반영했으면 true</summary>
        bool ApplyTelemetry(SignalValue target, TelemetryMessage t, long receivedMs)
        {
            string reason =
                t.Quality == Quality.Bad ? "quality bad" :
                !target.Spec.IsInRange(t.Value) ? $"측정 범위 밖 {t.Value} (유효 {target.Spec.ValidMin}~{target.Spec.ValidMax})" :
                null;
            target.ArrivedMs = receivedMs;
            if (reason != null)
            {
                // 표시값은 마지막 정상값을 유지한다
                if (NowMs() - target.LastRejectMs > RejectAlarmHoldMs)
                    AddWarning($"이상값 거부 {target.Spec.Name}: {reason}");
                target.Rejected++;
                target.LastRejectReason = reason;
                target.LastRejectMs = receivedMs;
                return false;
            }
            target.Value = t.Value;
            target.Quality = t.Quality;
            target.Ts = t.Ts;
            target.ReceivedMs = receivedMs;
            target.HasValue = true;
            return true;
        }

        
        /// <summary>
        /// 지금 성립한 알람 조건. 트윈이 신호 조합으로 판단한다 (서버는 판단을 보내지 않음, 설계 원칙 2).
        /// 키는 조건의 정체성이라 문구(경과 시간 등)가 바뀌어도 같은 알람으로 이어진다.
        /// </summary>
        void EvaluateConditions(List<AlarmCondition> into)
        {
            long now = NowMs();
            into.Clear();
            if (!BrokerConnected)
                into.Add(new AlarmCondition("broker", AlarmSeverity.Critical, "브로커 연결 끊김: 트윈이 현실을 볼 수 없음"));
            if (DeviceOnline == false)
                into.Add(new AlarmCondition("offline", AlarmSeverity.Critical, "설비 오프라인 (Last Will): 화면은 마지막 수신 상태"));
            if (RunState == Shared.RunState.Fault)
                into.Add(new AlarmCondition("motor_fault", AlarmSeverity.Critical, $"모터 정지: {RunReason}"));
            int jams = JammedCount;
            if (jams > 0)
                into.Add(new AlarmCondition("jam", AlarmSeverity.Critical, $"잼: 출구 미감지 물건 {jams}개 (입구 후 {Equipment.BeltLengthM * Equipment.JamTimeFactor:F2}m 이동)"));
            if (Current.HasValue && Current.Value > CurrentHighA)
                into.Add(new AlarmCondition("current_high", AlarmSeverity.Warning, $"모터 과전류 {Current.Value:F2}A (기준 {CurrentHighA}A): 부하 증가·걸림 의심"));
            if (Temp.HasValue && Temp.Value > TempHighC)
                into.Add(new AlarmCondition("temp_high", AlarmSeverity.Warning, $"모터 과열 {Temp.Value:F1}°C (기준 {TempHighC}°C)"));
            foreach (var s in new[] { Speed, Temp, Current })
            {
                if (DeviceOnline == true && s.IsStale(now))
                    into.Add(new AlarmCondition("stale_" + s.Spec.Name, AlarmSeverity.Warning,
                        $"stale: {s.Spec.Name} {(s.ArrivedMs > 0 ? (now - s.ArrivedMs) / 1000.0 : 0):F1}s 무수신"));
                if (now - s.LastRejectMs < RejectAlarmHoldMs)
                    into.Add(new AlarmCondition("reject_" + s.Spec.Name, AlarmSeverity.Warning,
                        $"이상값 거부: {s.Spec.Name} {s.LastRejectReason}"));
            }
            long delay = ScreenDelayMs;
            if (DeviceOnline == true && delay > ScreenDelayWarnMs)
                into.Add(new AlarmCondition("screen_delay", AlarmSeverity.Warning, $"화면 지연 {delay / 1000.0:F1}s (서버 시각 기준 재생)"));
            if (_tracker.Resyncing)
                into.Add(new AlarmCondition("resync", AlarmSeverity.Info, "물건 추적 재동기화 중"));
            AppendConditions?.Invoke(into);
        }

        public int JammedCount
        {
            get
            {
                int n = 0;
                foreach (var i in _tracker.Items) if (i.Jammed) n++;
                return n;
            }
        }

        /// <summary>
        /// 연결 복구·세션 변경: 유실이 이미 알려졌으므로 물건 추적을 다시 맞추고 엣지 seq 기록도 잊는다.
        /// 대기 중인 이벤트는 현재 세션 것이면 유효한 데이터이므로 남기고, 이전 세션 것만 버린다.
        /// </summary>
        void ResyncTracking()
        {
            var sid = _gate.Sid;
            _pending.RemoveAll(p => p.Message.Sid != sid);
            _tracker.Resync();
            _gate.Forget(_tEntry);
            _gate.Forget(_tExit);
        }

        void OnSessionChanged(string oldSid, string newSid)
        {
            // 서버가 재시작되면 이전 세션의 물건 추적과 시계는 의미가 없다
            _sessionEpoch++;
            ResyncTracking();
            _speedHistory.Clear();
            _latencyWindow.Clear();
            DataClockMs = 0;
            _playbackMs = 0;
            if (oldSid != null)
                AddWarning($"세션 변경 {oldSid} → {newSid}: 추적 초기화");
        }

        GameObject CreateBoxView(TrackedItem item)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Item_{item.Id:D4}";
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = _boxMaterial;
            go.transform.SetParent(transform, false);
            return go;
        }

        void AddWarning(string text)
        {
            Warnings.Insert(0, $"{DateTime.Now:HH:mm:ss} {text}");
            if (Warnings.Count > 8) Warnings.RemoveAt(Warnings.Count - 1);
            Debug.LogWarning("[Twin] " + text);
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            throw new InvalidOperationException($"'{name}' 노드를 찾을 수 없습니다 (conveyor.glb 계층 확인)");
        }

        void OnDestroy()
        {
            if (_boxMaterial != null) Destroy(_boxMaterial);
            if (_jamMaterial != null) Destroy(_jamMaterial);
        }
    }
}
