using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ConveyorTwin.Shared;
using ConveyorTwin.Twin;
using ConveyorTwin.Twin.Robot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 플레이 모드 통합 점검 (2단계 6일 차). 트윈 씬을 실제로 재생하며 컨베이어(MQTT) → 그리퍼 → 팔레트(OPC UA) 인계를 측정한다.
    /// 더미 서버가 실행 중이어야 한다 (없으면 트윈의 ServerLauncher가 띄운다).
    ///
    ///  - 집을 때: 그리퍼에 붙은 박스 윗면 중심과 TCP의 거리 (출구 스토퍼 위치 추정 + 관절 재생 시각이 맞는지)
    ///  - 놓을 때: 팔레트에 놓인 박스 중심과 가장 가까운 칸 중심의 거리 (RobotCell 적재 패턴)
    ///  - 경고: 인계·추적 관련 [Robot]/[Twin] 경고, 잼 발생
    ///  - 캡처: 집기·옮기기·놓기 장면과 마지막 개요 → dist/playcheck/*.png
    ///
    /// 명령줄 (-quit 없이): Unity.exe -batchmode -projectPath Twin -executeMethod ConveyorTwin.EditorTools.TwinPlayCheck.RunBatch -playSeconds 60 -logFile play.log
    /// 플레이 모드 진입 때 도메인이 다시 로드되므로 진행 상태는 SessionState에 둔다.
    /// </summary>
    [InitializeOnLoad]
    public static class TwinPlayCheck
    {
        const string ScenePath = "Assets/Scenes/Twin.unity";
        const string KeyEnd = "TwinPlayCheck.EndUtcTicks";
        const string KeyStart = "TwinPlayCheck.StartUtcTicks";

        static readonly List<string> Logs = new List<string>();
        static readonly List<double> GripErrors = new List<double>();
        static bool _wasHolding;
        static int _lastPalletCount;
        static int _grips, _releases, _maxJams;
        static uint _firstCycle = uint.MaxValue, _lastCycle;
        static double _nextSample;
        static int _shots;
        static bool _hooked;

        static TwinPlayCheck()
        {
            EditorApplication.update += Tick;
        }

        [MenuItem("Tools/Twin/Play Check (60s)")]
        static void Menu() => Begin(60);

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            int seconds = 60;
            int i = Array.IndexOf(args, "-playSeconds");
            if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
            EditorSceneManager.OpenScene(ScenePath);
            Begin(seconds);
        }

        static void Begin(int seconds)
        {
            SessionState.SetString(KeyStart, DateTime.UtcNow.Ticks.ToString());
            SessionState.SetString(KeyEnd, DateTime.UtcNow.AddSeconds(seconds + 15).Ticks.ToString());   // 플레이 진입 시간 여유
            SessionState.SetInt("TwinPlayCheck.Seconds", seconds);
            EditorApplication.EnterPlaymode();
        }

        static void Tick()
        {
            var endStr = SessionState.GetString(KeyEnd, "");
            if (endStr == "" || !EditorApplication.isPlaying) return;

            if (!_hooked)
            {
                _hooked = true;
                Application.logMessageReceived += (msg, stack, type) =>
                {
                    if (type == LogType.Warning && (msg.StartsWith("[Robot]") || msg.StartsWith("[Twin]")))
                        Logs.Add($"{EditorApplication.timeSinceStartup - _startedAt,6:F1}s  {msg}");
                };
            }

            var robot = UnityEngine.Object.FindAnyObjectByType<RobotTwin>();
            var conveyor = UnityEngine.Object.FindAnyObjectByType<ConveyorTwinController>();
            if (robot == null || conveyor == null) return;

            double t = EditorApplication.timeSinceStartup;
            if (t >= _nextSample)
            {
                _nextSample = t + 0.05;
                Sample(robot, conveyor);
            }

            if (DateTime.UtcNow.Ticks >= long.Parse(endStr))
                Finish(robot, conveyor);
        }

        static readonly HashSet<string> ActiveAlarms = new HashSet<string>();
        static readonly HashSet<string> Captured = new HashSet<string>();
        static double _startedAt = -1;
        static double _connectedAt = -1;
        static bool _normalShot;
        static bool _announced;

        /// <summary>알람 키가 켜지고 꺼지는 순간을 기록 (장애 시나리오 검증용). 로봇 알람이 처음 뜨면 화면도 캡처</summary>
        static void TrackAlarms(ConveyorTwinController conveyor)
        {
            var now = new HashSet<string>(conveyor.Alarms.History.Where(a => a.Active).Select(a => a.Key));
            double t = EditorApplication.timeSinceStartup - _startedAt;
            foreach (var k in now.Except(ActiveAlarms))
            {
                var text = conveyor.Alarms.History.First(a => a.Active && a.Key == k).Text;
                Logs.Add($"{t,6:F1}s  + {k}: {text}");
                // 로봇이 한 번 접속한 뒤의 로봇 알람만 (시작 직후 접속 전 robot_link는 제외)
                if (k.StartsWith("robot_") && _announced && Captured.Add(k)) CaptureHmi("alarm_" + k);
            }
            foreach (var k in ActiveAlarms.Except(now))
                Logs.Add($"{t,6:F1}s  - {k}");
            ActiveAlarms.Clear();
            ActiveAlarms.UnionWith(now);
        }

        static void Sample(RobotTwin robot, ConveyorTwinController conveyor)
        {
            if (_startedAt < 0) _startedAt = EditorApplication.timeSinceStartup;
            if (!_announced && robot.LinkState == ConveyorTwin.Twin.OpcUa.RobotLinkState.Connected)
            {
                _announced = true;
                // 외부 장애 조율 스크립트가 이 줄을 보고 시점을 맞춘다
                Debug.Log("[PlayState] robot connected");
                Logs.Add($"{EditorApplication.timeSinceStartup - _startedAt,6:F1}s  로봇 접속");
                _connectedAt = EditorApplication.timeSinceStartup;
            }
            TrackAlarms(conveyor);
            if (_announced && !_normalShot && EditorApplication.timeSinceStartup - _connectedAt > 4.0)
            {
                _normalShot = true;
                CaptureHmi("normal");   // 장애 없는 정상 운전 화면
            }
            PumpHmiCapture();

            if (robot.CycleCount > 0)
            {
                _firstCycle = Math.Min(_firstCycle, robot.CycleCount);
                _lastCycle = Math.Max(_lastCycle, robot.CycleCount);
            }
            _maxJams = Math.Max(_maxJams, conveyor.JammedCount);

            // 집기: 박스 윗면 중심과 TCP의 거리
            if (robot.Holding && !_wasHolding)
            {
                _grips++;
                // RobotTwin이 박스를 TCP에 맞춰 붙이기 전의 오차 (벨트 위치 추정 + 관절 재생 시각의 정확도)
                var box = robot.HeldBox.transform;
                if (!robot.LastGripDuringBadData) GripErrors.Add(robot.LastGripOffsetM);
                Logs.Add($"{EditorApplication.timeSinceStartup - _startedAt,6:F1}s  [Grip {_grips}] 맞추기 전 오차 {robot.LastGripOffsetM * 1000:F1}mm" +
                         $"{(robot.LastGripDuringBadData ? " (관절각 Bad 중 — 통계 제외)" : "")}, 박스 길이 {box.lossyScale.z:F3}m, 사이클 {robot.CycleCount}");
                if (_grips <= 2) Capture($"grip_{_grips}");
            }
            if (robot.Holding && _wasHolding && _grips == 1 && _shots < 3 && robot.JointsDeg[0] < -40)
                Capture("carry");   // 팔레트 쪽으로 돌아가는 중
            if (!robot.Holding && _wasHolding)
            {
                _releases++;
                if (_releases <= 2) Capture($"release_{_releases}");
            }
            _wasHolding = robot.Holding;
            _lastPalletCount = robot.BoxesOnPallet;
        }

        static void Finish(RobotTwin robot, ConveyorTwinController conveyor)
        {
            SessionState.EraseString(KeyEnd);
            // 진행 중이던 HMI 캡처가 있으면 패널 설정(에셋)을 원래대로
            var doc = UnityEngine.Object.FindAnyObjectByType<UnityEngine.UIElements.UIDocument>();
            if (_uiRt != null && doc != null)
            {
                doc.panelSettings.targetTexture = null;
                doc.panelSettings.clearColor = _uiClearColor;
                doc.panelSettings.colorClearValue = _uiClearValue;
            }
            Capture("overview_end");

            // 놓인 박스 ↔ 가장 가까운 칸 중심
            var slotCenters = Enumerable.Range(0, RobotCell.SlotCount).Select(i =>
            {
                var top = RobotCell.SlotTop(i);
                return RobotCellBuilder.CellToWorld(new CellPoint(top.X, top.Y, top.Z - RobotCell.BoxHeightM / 2));
            }).ToList();
            var placeErrors = new List<double>();
            foreach (Transform box in robot.Stack)
                placeErrors.Add(slotCenters.Min(c => (c - box.position).magnitude));

            int seconds = SessionState.GetInt("TwinPlayCheck.Seconds", 0);
            var sb = new StringBuilder($"[Play] 트윈 플레이 점검 {seconds}s\n");
            sb.AppendLine($"  로봇 OPC UA: {robot.LinkState}{(robot.LinkError != null ? $" ({robot.LinkError})" : "")}, 관절 지연 {robot.LastJointLatencyMs}ms, " +
                          $"검증 통과 {robot.Gate.Accepted} / 거부 {robot.Gate.RejectedBad + robot.Gate.RejectedOld + robot.Gate.RejectedFuture + robot.Gate.RejectedRange}");
            sb.AppendLine($"  컨베이어 MQTT: {(conveyor.BrokerConnected ? "연결" : "끊김")}, 화면 지연 {conveyor.ScreenDelayMs}ms, " +
                          $"입구 {conveyor.Tracker.Entered} / 출구 {conveyor.Tracker.Exited}, 최대 잼 {_maxJams}");
            sb.AppendLine($"  사이클 {(_firstCycle == uint.MaxValue ? "-" : $"{_firstCycle} → {_lastCycle}")}, 집기 {_grips}, 놓기 {_releases}, 팔레트 위 {robot.BoxesOnPallet}");
            if (GripErrors.Count > 0)
                sb.AppendLine($"  집기 오차 (박스 윗면 ↔ TCP): 평균 {GripErrors.Average() * 1000:F1}mm, 최대 {GripErrors.Max() * 1000:F1}mm");
            if (placeErrors.Count > 0)
                sb.AppendLine($"  놓기 오차 (박스 ↔ 칸 중심): 평균 {placeErrors.Average() * 1000:F1}mm, 최대 {placeErrors.Max() * 1000:F1}mm");
            sb.AppendLine($"  타임라인·경고 {Logs.Count}건");
            foreach (var l in Logs.Take(80)) sb.AppendLine("    " + l);
            sb.AppendLine($"  캡처: dist/playcheck/ ({_shots}장)");

            bool ok = robot.LinkState == ConveyorTwin.Twin.OpcUa.RobotLinkState.Connected && _grips > 0 && _releases > 0 &&
                      _maxJams == 0 && (GripErrors.Count == 0 || GripErrors.Max() < 0.02) &&
                      (placeErrors.Count == 0 || placeErrors.Max() < 0.02);
            sb.AppendLine(ok ? "  결과: 통과" : "  결과: 실패");
            if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());

            EditorApplication.ExitPlaymode();
            if (Application.isBatchMode)
                EditorApplication.delayCall += () => EditorApplication.Exit(ok ? 0 : 1);
        }

        // ---------- HMI 포함 캡처 ----------
        // UI Toolkit 패널은 카메라 렌더에 들어가지 않는다. 패널을 잠시 렌더 텍스처에 그리게 하고(PanelSettings.targetTexture),
        // 다음 프레임 이후에 3D 화면 위에 알파 합성한다. 패널 설정은 에셋이라 끝나면 반드시 원래 값으로 되돌린다.
        static readonly Queue<string> HmiRequests = new Queue<string>();
        static RenderTexture _uiRt;
        static double _uiRequestedAt;
        static string _uiName;
        static bool _uiClearColor;
        static Color _uiClearValue;

        static void CaptureHmi(string name) => HmiRequests.Enqueue(name);

        static void PumpHmiCapture()
        {
            var doc = UnityEngine.Object.FindAnyObjectByType<UnityEngine.UIElements.UIDocument>();
            if (doc == null || doc.panelSettings == null) { HmiRequests.Clear(); return; }
            var ps = doc.panelSettings;
            if (_uiRt == null)
            {
                if (HmiRequests.Count == 0) return;
                _uiName = HmiRequests.Dequeue();
                _uiRt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
                _uiClearColor = ps.clearColor;
                _uiClearValue = ps.colorClearValue;
                ps.clearColor = true;
                ps.colorClearValue = Color.clear;
                ps.targetTexture = _uiRt;
                _uiRequestedAt = EditorApplication.timeSinceStartup;
                return;
            }
            if (EditorApplication.timeSinceStartup - _uiRequestedAt < 0.4) return;   // 패널이 몇 프레임 그릴 시간

            const int w = 1600, h = 900;
            var ui = ReadRt(_uiRt, TextureFormat.RGBA32);
            ps.targetTexture = null;
            ps.clearColor = _uiClearColor;
            ps.colorClearValue = _uiClearValue;
            UnityEngine.Object.DestroyImmediate(_uiRt);
            _uiRt = null;

            var cam = Camera.main;
            var camRt = new RenderTexture(w, h, 24);
            cam.targetTexture = camRt;
            cam.Render();
            cam.targetTexture = null;
            var scene = ReadRt(camRt, TextureFormat.RGB24);
            UnityEngine.Object.DestroyImmediate(camRt);

            var a = scene.GetPixels32();
            var b = ui.GetPixels32();
            for (int i = 0; i < a.Length; i++)
            {
                float t = b[i].a / 255f;
                a[i] = new Color32((byte)(a[i].r * (1 - t) + b[i].r * t), (byte)(a[i].g * (1 - t) + b[i].g * t), (byte)(a[i].b * (1 - t) + b[i].b * t), 255);
            }
            scene.SetPixels32(a);
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dist", "playcheck"));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "hmi_" + _uiName + ".png"), scene.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(scene);
            UnityEngine.Object.DestroyImmediate(ui);
            _shots++;
        }

        static Texture2D ReadRt(RenderTexture rt, TextureFormat format)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, format, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        static void Capture(string name)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dist", "playcheck"));
            Directory.CreateDirectory(dir);
            const int w = 1600, h = 900;
            var rt = new RenderTexture(w, h, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            cam.targetTexture = prev;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
            _shots++;
        }
    }
}
