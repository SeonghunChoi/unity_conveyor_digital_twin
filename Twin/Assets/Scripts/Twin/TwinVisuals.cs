using System.Collections.Generic;
using ConveyorTwin.Shared;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 상태별 시각화. CAD 노드마다 ConveyorTwin/StatusLit 머티리얼을 하나씩 만들어 붙이고,
    /// 트윈이 판단한 상태에 따라 "어느 부품이 문제인지"를 하이라이트한다.
    ///
    /// | 상태 | 표현 |
    /// | --- | --- |
    /// | 브로커 끊김·설비 오프라인 | 전체 회색조 (보이는 값을 믿을 수 없음) + 제어 박스 주황 점멸 |
    /// | 모터 정지 (fault) | 감속 모터·제어 박스 빨강 빠른 맥동 |
    /// | 잼 | 벨트 빨강 맥동 (박스는 컨트롤러가 개별 하이라이트) |
    /// | 과전류·과열·모터 신호 이상값 | 감속 모터 주황 맥동 |
    /// | 속도 stale | 벨트 노랑 (흐름이 실제와 다를 수 있음) |
    ///
    /// 벨트 표면 줄무늬는 컨트롤러의 BeltTravelM(재생 시계 기준 이동 거리)로 흘린다.
    /// CAD 색 정보가 없으므로 부품별 기본색은 여기서 정한다 (실제 mk 설비의 일반적인 마감 기준 추정).
    /// </summary>
    [RequireComponent(typeof(ConveyorTwinController))]
    public sealed class TwinVisuals : MonoBehaviour
    {
        [SerializeField] Shader statusShader;

        static readonly Color Red = new Color(1.0f, 0.12f, 0.08f) * 2.2f;
        static readonly Color Orange = new Color(1.0f, 0.45f, 0.05f) * 2.0f;
        static readonly Color Yellow = new Color(1.0f, 0.85f, 0.1f) * 1.6f;
        /// <summary>하이라이트가 켜지고 꺼지는 시간 (깜빡임 없이 부드럽게)</summary>
        const float FadeS = 0.25f;

        static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int IdSmoothness = Shader.PropertyToID("_Smoothness");
        static readonly int IdMetallic = Shader.PropertyToID("_Metallic");
        static readonly int IdHighlightColor = Shader.PropertyToID("_HighlightColor");
        static readonly int IdHighlightStrength = Shader.PropertyToID("_HighlightStrength");
        static readonly int IdPulseHz = Shader.PropertyToID("_PulseHz");
        static readonly int IdDesaturate = Shader.PropertyToID("_Desaturate");
        static readonly int IdBeltOffset = Shader.PropertyToID("_BeltOffset");

        /// <summary>CAD 노드 하나의 표현 상태</summary>
        sealed class Part
        {
            public string Name;
            public readonly List<Material> Materials = new List<Material>();
            public float Strength;          // 현재 (페이드 중)
            public float TargetStrength;
            public Color Color;
            public float PulseHz;
            public float Desaturate;

            public void Set(Color color, float strength, float pulseHz)
            {
                // 더 강한 요청이 이긴다 (같은 프레임에 여러 상태가 겹칠 때)
                if (strength <= TargetStrength) return;
                Color = color;
                TargetStrength = strength;
                PulseHz = pulseHz;
            }
        }

        ConveyorTwinController _twin;
        readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        readonly List<Material> _owned = new List<Material>();
        Part _belt, _motor, _box;

        void Start()
        {
            _twin = GetComponent<ConveyorTwinController>();
            if (statusShader == null)
            {
                Debug.LogWarning("[Twin] TwinVisuals: statusShader가 없습니다 (Tools/Twin/Setup Scene 다시 실행)");
                enabled = false;
                return;
            }

            //            노드         기본색                              smooth metal
            AddPart("Belt",       new Color(0.10f, 0.11f, 0.12f), 0.25f, 0.0f);
            AddPart("Drum_Head",  new Color(0.62f, 0.64f, 0.67f), 0.65f, 0.85f);
            AddPart("Drum_Tail",  new Color(0.62f, 0.64f, 0.67f), 0.65f, 0.85f);
            AddPart("DriveMotor", new Color(0.22f, 0.32f, 0.46f), 0.50f, 0.30f);   // 도장된 감속 모터
            AddPart("ControlBox", new Color(0.78f, 0.79f, 0.77f), 0.35f, 0.00f);   // 제어함 표준 밝은 회색
            AddPart("Frame",      new Color(0.76f, 0.78f, 0.81f), 0.55f, 0.60f);   // 알루미늄 프로파일
            AddPart("Legs",       new Color(0.72f, 0.74f, 0.77f), 0.50f, 0.60f);

            _parts.TryGetValue("Belt", out _belt);
            _parts.TryGetValue("DriveMotor", out _motor);
            _parts.TryGetValue("ControlBox", out _box);
            SetupBeltFlow();
        }

        void AddPart(string nodeName, Color baseColor, float smoothness, float metallic)
        {
            var node = FindDeep(_twin.ConveyorRoot, nodeName);
            if (node == null)
            {
                Debug.LogWarning($"[Twin] TwinVisuals: '{nodeName}' 노드 없음");
                return;
            }
            var part = new Part { Name = nodeName };
            foreach (var r in node.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = new Material(statusShader) { name = $"{nodeName}_Status" };
                    m.SetColor(IdBaseColor, baseColor);
                    m.SetFloat(IdSmoothness, smoothness);
                    m.SetFloat(IdMetallic, metallic);
                    mats[i] = m;
                    part.Materials.Add(m);
                    _owned.Add(m);
                }
                r.sharedMaterials = mats;
            }
            _parts[nodeName] = part;
        }

        /// <summary>벨트 셰이더에 드럼 위치를 벨트 메시의 오브젝트 공간 기준으로 알려 준다</summary>
        void SetupBeltFlow()
        {
            if (_belt == null) return;
            var beltRenderer = FindDeep(_twin.ConveyorRoot, "Belt").GetComponentInChildren<Renderer>();
            var local = beltRenderer.transform;
            var tail = local.InverseTransformPoint(_twin.DrumTail.position);
            var head = local.InverseTransformPoint(_twin.DrumHead.position);
            foreach (var m in _belt.Materials)
            {
                m.EnableKeyword("_BELT_FLOW");
                m.SetFloat("_BeltFlow", 1f);
                m.SetColor("_StripeColor", new Color(0.30f, 0.31f, 0.32f));
                m.SetFloat("_StripeSpacing", 0.10f);
                m.SetFloat("_StripeWidth", 0.12f);
                m.SetFloat("_BeltCenterY", (tail.y + head.y) * 0.5f);
                m.SetFloat("_BeltTailZ", tail.z);
                m.SetFloat("_BeltHeadZ", head.z);
                m.SetFloat("_BeltRadius", ConveyorTwinController.BeltRadiusM);
            }
        }

        void Update()
        {
            foreach (var p in _parts.Values) { p.TargetStrength = 0f; p.Desaturate = 0f; }

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool blind = !_twin.BrokerConnected || _twin.DeviceOnline == false;
            if (blind)
            {
                foreach (var p in _parts.Values) p.Desaturate = 1f;
                _box?.Set(Orange, 0.9f, 1.0f);
            }
            if (_twin.RunState == RunState.Fault)
            {
                _motor?.Set(Red, 1.0f, 2.5f);
                _box?.Set(Red, 1.0f, 2.5f);
            }
            if (_twin.JammedCount > 0)
                _belt?.Set(Red, 0.7f, 1.5f);

            bool motorSuspect =
                (_twin.Current.HasValue && _twin.Current.Value > ConveyorTwinController.CurrentHighA) ||
                (_twin.Temp.HasValue && _twin.Temp.Value > ConveyorTwinController.TempHighC) ||
                now - _twin.Current.LastRejectMs < ConveyorTwinController.RejectAlarmHoldMs ||
                now - _twin.Temp.LastRejectMs < ConveyorTwinController.RejectAlarmHoldMs;
            if (motorSuspect)
                _motor?.Set(Orange, 0.8f, 1.0f);
            if (!blind && _twin.Speed.IsStale(now))
                _belt?.Set(Yellow, 0.4f, 0f);

            float k = Mathf.Clamp01(Time.deltaTime / FadeS);
            float beltOffset = (float)_twin.BeltTravelM;
            foreach (var p in _parts.Values)
            {
                p.Strength = Mathf.Lerp(p.Strength, p.TargetStrength, k);
                foreach (var m in p.Materials)
                {
                    m.SetColor(IdHighlightColor, p.Color);
                    m.SetFloat(IdHighlightStrength, p.Strength);
                    m.SetFloat(IdPulseHz, p.PulseHz);
                    m.SetFloat(IdDesaturate, Mathf.MoveTowards(m.GetFloat(IdDesaturate), p.Desaturate, Time.deltaTime * 3f));
                }
            }
            if (_belt != null)
                foreach (var m in _belt.Materials) m.SetFloat(IdBeltOffset, beltOffset);
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        void OnDestroy()
        {
            foreach (var m in _owned) if (m != null) Destroy(m);
        }
    }
}
