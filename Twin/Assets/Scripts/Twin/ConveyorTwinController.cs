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
        public double Value;
        public Quality Quality;
        public long Ts;            // 서버 발행 시각
        public long ReceivedMs;    // 트윈 수신 시각
        public bool HasValue;
    }

    /// <summary>
    /// 트윈 본체. MQTT로 받은 원시 신호를 해석해 3D 모델(드럼 회전, 물건 박스)에 반영한다.
    /// 트윈은 값을 만들지 않는다 (CLAUDE.md 설계 원칙 1). 물건 위치처럼 신호 사이를 채우는 값은
    /// 받은 속도로 추정하고, 다음 센서 신호에서 보정한다.
    /// </summary>
    public sealed class ConveyorTwinController : MonoBehaviour
    {
        const string BrokerHost = "127.0.0.1";
        const int BrokerPort = 1883;
        /// <summary>벨트를 감은 드럼 외경의 반지름 (docs/cad-pipeline.md 6절). 벨트 속도 → 드럼 각속도</summary>
        const float BeltOuterRadiusM = 0.0275f;

        [SerializeField] Transform conveyorRoot;
        [SerializeField] string deviceId = Topics.DefaultDeviceId;
        [SerializeField] Color boxColor = new Color(0.76f, 0.60f, 0.42f);

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
        };

        MqttLink _link;
        readonly MessageGate _gate = new MessageGate();
        ItemTracker _tracker;
        Material _boxMaterial;

        Transform _drumHead, _drumTail;
        Quaternion _drumHeadBase, _drumTailBase;
        Vector3 _entryPoint, _flowDir, _drumAxis;
        float _beltTopY;
        double _drumAngleDeg;

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
        public SignalValue Speed { get; } = new SignalValue();
        public SignalValue Temp { get; } = new SignalValue();
        public SignalValue Current { get; } = new SignalValue();
        public SignalValue Weight { get; } = new SignalValue();
        public ItemTracker Tracker => _tracker;
        public long LastLatencyMs { get; private set; }
        public long Received { get; private set; }
        public readonly List<string> Warnings = new List<string>();

        /// <summary>애니메이션에 쓰는 벨트 속도. 설비가 오프라인이거나 연결이 끊기면 알 수 없으므로 멈춘다</summary>
        public double EffectiveSpeed =>
            BrokerConnected && DeviceOnline == true && Speed.HasValue ? Speed.Value : 0.0;

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

            _boxMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _boxMaterial.SetColor("_BaseColor", boxColor);
            _tracker = new ItemTracker(CreateBoxView);
            _tracker.Warning += AddWarning;
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

            var span = _drumHead.position - _drumTail.position;
            span.y = 0f;
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
            if (_link != null)
            {
                while (_link.TryDequeue(out var m))
                {
                    try { Route(m); }
                    catch (Exception e) { AddWarning($"해석 실패 {m.Topic}: {e.Message}"); }
                }
            }

            double speed = EffectiveSpeed;
            double dt = Time.deltaTime;
            _tracker.Tick(dt, speed);
            foreach (var item in _tracker.Items)
                _tracker.Place(item, _entryPoint, _flowDir, _beltTopY);

            // 드럼 회전: 각속도 = 벨트 속도 / 반지름
            _drumAngleDeg = (_drumAngleDeg + speed / BeltOuterRadiusM * dt * Mathf.Rad2Deg) % 360.0;
            var spin = Quaternion.AngleAxis((float)_drumAngleDeg, _drumAxis);
            _drumHead.rotation = spin * _drumHeadBase;
            _drumTail.rotation = spin * _drumTailBase;
        }

        void Route(in ReceivedMessage m)
        {
            Received++;

            if (m.Topic == _tStatus)
            {
                var s = JsonConvert.DeserializeObject<StatusMessage>(m.Payload, JsonSettings);
                _gate.ObserveSession(s.Sid);
                if (DeviceOnline != false && !s.Online)
                    OfflineSinceMs = m.ReceivedMs;   // Will의 ts는 접속 시각이라 끊긴 시각으로는 수신 시각을 쓴다
                DeviceOnline = s.Online;
                return;
            }

            if (m.Topic == _tRun)
            {
                var r = Parse<RunStateMessage>(m);
                if (r == null) return;
                RunState = r.State;
                RunReason = r.Reason;
                return;
            }

            if (m.Topic == _tEntry || m.Topic == _tExit)
            {
                var e = Parse<SensorEdgeMessage>(m);
                if (e == null) return;
                if (m.Topic == _tEntry) _tracker.OnEntryEdge(e.Blocked, e.Ts, m.ReceivedMs, EffectiveSpeed);
                else _tracker.OnExitEdge(e.Blocked);
                return;
            }

            SignalValue target =
                m.Topic == _tSpeed ? Speed :
                m.Topic == _tTemp ? Temp :
                m.Topic == _tCurrent ? Current :
                m.Topic == _tWeight ? Weight : null;
            if (target == null)
                return;

            var t = Parse<TelemetryMessage>(m);
            if (t == null) return;
            target.Value = t.Value;
            target.Quality = t.Quality;
            target.Ts = t.Ts;
            target.ReceivedMs = m.ReceivedMs;
            target.HasValue = true;
            if (target == Weight)
                _tracker.OnWeight(t.Value);
        }

        /// <summary>역직렬화 + sid/seq 검사. 버릴 메시지면 null</summary>
        T Parse<T>(in ReceivedMessage m) where T : Envelope
        {
            var msg = JsonConvert.DeserializeObject<T>(m.Payload, JsonSettings);
            if (!_gate.Accept(m.Topic, msg))
                return null;
            if (!m.Retained)
                LastLatencyMs = m.ReceivedMs - msg.Ts;
            return msg;
        }

        void OnSessionChanged(string oldSid, string newSid)
        {
            // 서버가 재시작되면 이전 세션의 물건 추적은 의미가 없다
            _tracker.Clear();
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

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            throw new InvalidOperationException($"'{name}' 노드를 찾을 수 없습니다 (conveyor.glb 계층 확인)");
        }

        void OnDestroy()
        {
            if (_boxMaterial != null) Destroy(_boxMaterial);
        }
    }
}
