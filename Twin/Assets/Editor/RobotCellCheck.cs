using System;
using System.IO;
using System.Linq;
using System.Text;
using ConveyorTwin.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 로봇 셀 배치 점검 (2단계 2일 차). Tools/Twin/Setup Scene으로 만든 씬에서:
    ///  1. 픽 위치(RobotCell.PickTop)가 컨베이어 출구에 멈춘 박스 윗면과 맞는지 (컨베이어 glb 실측과 비교)
    ///  2. 픽 위치와 팔레트 48칸 전부를 공구 수직 하향 자세로 닿을 수 있는지 — 관절 가동 범위 안의 해가 있는지
    ///  3. 그 관절각을 실제 Unity 로봇에 넣었을 때 TCP가 목표에 오는지 (관절 규약·그리퍼 길이까지 한 번에 검증)
    ///  4. 개요·픽·먼 칸 적재 자세 화면 캡처 → dist/cellcheck/*.png
    ///
    /// IK는 Shared.RobotKinematics — 서버 가상 로봇 컨트롤러가 목표 관절각을 만드는 것과 같은 계산이다.
    /// 트윈 런타임은 IK를 하지 않고 서버가 보낸 관절각만 따른다 (원칙 1).
    ///
    /// 명령줄: Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.RobotCellCheck.CheckBatch -logFile cell-check.log
    /// </summary>
    public static class RobotCellCheck
    {
        const string ScenePath = "Assets/Scenes/Twin.unity";
        const float Tolerance = 0.001f;   // TCP 위치 허용 오차 1mm

        const float YawToleranceDeg = 0.1f;   // 그리퍼 수평 방향 허용 오차
        // 관절 규약: localRotation = AngleAxis(θ, 축)  (docs/cad-robot.md 4절, RobotImportCheck로 검증)
        static readonly Vector3[] UnityAxes =
        {
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
        };

        [MenuItem("Tools/Twin/Check Robot Cell")]
        static void CheckMenu() => Check();

        public static void CheckBatch() => EditorApplication.Exit(Check() ? 0 : 1);

        static bool Check()
        {
            if (Application.isBatchMode)
                EditorSceneManager.OpenScene(ScenePath);

            var robot = GameObject.Find("RobotCell/Robot");
            var conveyor = GameObject.Find("Conveyor");
            if (robot == null || conveyor == null)
            {
                Debug.LogError("[Cell] 씬에 RobotCell/Robot 또는 Conveyor가 없습니다. Tools/Twin/Setup Scene을 먼저 실행하세요.");
                return false;
            }
            var joints = new Transform[6];
            for (int i = 0; i < 6; i++) joints[i] = RobotCellBuilder.FindDeep(robot.transform, "J" + (i + 1));
            var tcp = RobotCellBuilder.FindDeep(robot.transform, "TCP");
            if (joints.Any(j => j == null) || tcp == null)
            {
                Debug.LogError("[Cell] 로봇 관절 또는 TCP 노드를 찾지 못했습니다.");
                return false;
            }

            bool ok = true;
            var sb = new StringBuilder("[Cell] 로봇 셀 배치 점검\n");

            // ---- 1. 픽 위치 vs 컨베이어 실측 ----
            var head = RobotCellBuilder.FindDeep(conveyor.transform, "Drum_Head");
            var belt = RobotCellBuilder.FindDeep(conveyor.transform, "Belt");
            if (head != null && belt != null)
            {
                float beltTop = belt.GetComponentInChildren<Renderer>().bounds.max.y;
                // 출구 센서 = 구동 드럼 중심. 앞단이 닿아 멈춘 박스의 윗면 중심
                var expected = new Vector3(0f, beltTop + (float)RobotCell.BoxHeightM, head.position.z - (float)RobotCell.BoxLengthM / 2);
                var pick = RobotCellBuilder.CellToWorld(RobotCell.PickTop);
                float err = (pick - expected).magnitude;
                bool pass = err < 0.01f;
                ok &= pass;
                sb.AppendLine($"  픽 위치 {pick:F3} / 컨베이어 실측 {expected:F3}  {(pass ? "OK" : $"불일치 {err:F3}m")}  (벨트 윗면 y={beltTop:F3})");
            }
            else
            {
                ok = false;
                sb.AppendLine("  컨베이어 Drum_Head/Belt 노드 없음");
            }

            // ---- 2·3. 픽 + 팔레트 48칸 도달성 ----
            var targets = new (string Name, CellPoint P)[RobotCell.SlotCount + 1];
            targets[0] = ("Pick", RobotCell.PickTop);
            for (int i = 0; i < RobotCell.SlotCount; i++) targets[i + 1] = ($"Slot{i:D2}", RobotCell.SlotTop(i));

            double worstReach = 0, worstLimitMargin = double.MaxValue;
            string worstReachName = null, worstLimitName = null;
            float worstTcpErr = 0, worstYawErr = 0;
            double[] pickQ = null, farQ = null;
            Vector3? pickYawAxis = null;
            var gripper = RobotCellBuilder.FindDeep(robot.transform, "Gripper");
            foreach (var (name, p) in targets)
            {
                // Shared.RobotKinematics: 서버 가상 로봇 컨트롤러와 같은 IK
                bool solved = RobotKinematics.SolveTopDown(p, RobotCell.GripperLengthM, out var q, out double reachRatio);
                double margin = RobotKinematics.LimitMarginDeg(q, out int tightJoint);
                if (!solved)
                {
                    ok = false;
                    sb.AppendLine(reachRatio > 1 || reachRatio < 0.05
                        ? $"  {name} {p}: 도달 불가 (사용률 {reachRatio:P0})"
                        : $"  {name} {p}: J{tightJoint + 1} 가동 범위 초과 (q={Fmt(q)})");
                    continue;
                }
                if (reachRatio > worstReach) { worstReach = reachRatio; worstReachName = name; }
                if (margin < worstLimitMargin) { worstLimitMargin = margin; worstLimitName = $"{name} J{tightJoint + 1}"; }

                Apply(joints, q);
                float tcpErr = (tcp.position - RobotCellBuilder.CellToWorld(p)).magnitude;
                worstTcpErr = Mathf.Max(worstTcpErr, tcpErr);
                if (tcpErr > Tolerance)
                {
                    ok = false;
                    sb.AppendLine($"  {name}: TCP {tcp.position:F4} / 목표 {RobotCellBuilder.CellToWorld(p):F4} 오차 {tcpErr:F4}m");
                }
                // 그리퍼 수평 방향(요): J6 = J1 보정으로 픽 때와 같아야 박스가 같은 방향으로 놓인다
                var yawAxis = Vector3.ProjectOnPlane(gripper.forward, Vector3.up).normalized;
                if (pickYawAxis == null) pickYawAxis = yawAxis;
                float yawErr = Vector3.Angle(pickYawAxis.Value, yawAxis);
                worstYawErr = Mathf.Max(worstYawErr, yawErr);
                if (yawErr > YawToleranceDeg)
                {
                    ok = false;
                    sb.AppendLine($"  {name}: 그리퍼 방향이 픽 때와 {yawErr:F1}° 다름");
                }
                if (name == "Pick") pickQ = q;
                if (name == "Slot32") farQ = q;   // 3단 첫 칸 = 가장 높고 먼 줄의 끝
            }
            sb.AppendLine($"  목표 {targets.Length}개 (픽 1 + 팔레트 {RobotCell.SlotCount}칸), 공구 수직 하향");
            sb.AppendLine($"  도달 여유: 최대 사용률 {worstReach:P0} ({worstReachName}) — J2→손목 거리 / (상완+전완)");
            sb.AppendLine($"  관절 여유: 최소 {worstLimitMargin:F1}° ({worstLimitName})");
            sb.AppendLine($"  TCP 오차: 최대 {worstTcpErr * 1000:F2} mm (Unity 로봇에 관절각을 넣어 측정)");
            sb.AppendLine($"  그리퍼 방향 오차: 최대 {worstYawErr:F2}° (J6 = J1 보정)");
            if (pickQ != null) sb.AppendLine($"  픽 자세 q={Fmt(pickQ)}");
            if (farQ != null) sb.AppendLine($"  Slot32 자세 q={Fmt(farQ)}");

            // ---- 4. 화면 캡처 ----
            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dist", "cellcheck"));
            Directory.CreateDirectory(outDir);
            Apply(joints, new double[6]);
            Capture(Camera.main, Path.Combine(outDir, "overview_home.png"));
            var side = TempCamera(new Vector3(-3.2f, 2.2f, -0.3f), new Vector3(-0.5f, 0.8f, 1.3f));
            if (pickQ != null) { Apply(joints, pickQ); Capture(side, Path.Combine(outDir, "pick.png")); }
            if (farQ != null) { Apply(joints, farQ); Capture(side, Path.Combine(outDir, "place_slot32.png")); }
            UnityEngine.Object.DestroyImmediate(side.gameObject);
            Apply(joints, new double[6]);
            sb.AppendLine($"  캡처: {outDir}");

            sb.AppendLine(ok ? "  결과: 모두 통과" : "  결과: 실패 항목 있음");
            if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
            return ok;
        }

        static void Apply(Transform[] joints, double[] q)
        {
            for (int i = 0; i < 6; i++)
                joints[i].localRotation = Quaternion.AngleAxis((float)q[i], UnityAxes[i]);
        }

        static string Fmt(double[] q) => "(" + string.Join(", ", q.Select(v => v.ToString("F1"))) + ")";

        static Camera TempCamera(Vector3 pos, Vector3 lookAt)
        {
            var cam = new GameObject("CellCheckCamera").AddComponent<Camera>();
            cam.transform.position = pos;
            cam.transform.LookAt(lookAt);
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.05f;
            return cam;
        }

        static void Capture(Camera cam, string path)
        {
            if (cam == null) return;
            const int w = 1600, h = 900;
            var rt = new RenderTexture(w, h, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = prev;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
