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
                        Logs.Add($"{DateTime.Now:HH:mm:ss} {msg}");
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

        static void Sample(RobotTwin robot, ConveyorTwinController conveyor)
        {
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
                var box = robot.HeldBox.transform;
                var top = box.position + box.up * box.lossyScale.y * 0.5f;
                var d = top - robot.Tcp.position;
                GripErrors.Add(d.magnitude);
                Logs.Add($"{DateTime.Now:HH:mm:ss} [Grip {_grips}] 박스 윗면 - TCP = ({d.x * 1000:F1}, {d.y * 1000:F1}, {d.z * 1000:F1})mm, " +
                         $"박스 길이 {box.lossyScale.z:F3}m, 사이클 {robot.CycleCount}");
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
            sb.AppendLine($"  경고 {Logs.Count}건");
            foreach (var l in Logs.Take(20)) sb.AppendLine("    " + l);
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
