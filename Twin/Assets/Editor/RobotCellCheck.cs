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
    /// 여기의 역기구학(IK)은 배치 점검용이다. 트윈 런타임은 IK를 하지 않고 서버가 보낸 관절각만 따른다 (원칙 1).
    /// 4일 차 서버 가상 컨트롤러가 같은 계산으로 목표 관절각을 만든다.
    ///
    /// 명령줄: Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.RobotCellCheck.CheckBatch -logFile cell-check.log
    /// </summary>
    public static class RobotCellCheck
    {
        const string ScenePath = "Assets/Scenes/Twin.unity";
        const float Tolerance = 0.001f;   // TCP 위치 허용 오차 1mm

        // IRB 2600-20/1.65 기구학 (m, docs/cad-robot.md) — 베이스 좌표 (r, z) 평면
        const double J2R = 0.150, J2Z = 0.445, L2 = 0.700;
        const double ForearmX = 0.795, ForearmZ = 0.115;   // J3 → J5 (홈 자세)
        const double WristToFlange = 0.085;
        static readonly double L3 = Math.Sqrt(ForearmX * ForearmX + ForearmZ * ForearmZ);
        static readonly double Beta0 = Math.Atan2(ForearmZ, ForearmX) * Rad2Deg;

        // 관절 가동 범위 (도, ABB IRB 2600 제품 자료)
        static readonly double[] Min = { -180, -95, -180, -400, -120, -400 };
        static readonly double[] Max = { 180, 155, 75, 400, 120, 400 };

        // 관절 규약: localRotation = AngleAxis(θ, 축)  (docs/cad-robot.md 4절, RobotImportCheck로 검증)
        static readonly Vector3[] UnityAxes =
        {
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
        };

        const double Rad2Deg = 180.0 / Math.PI;

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
            float worstTcpErr = 0;
            double[] pickQ = null, farQ = null;
            foreach (var (name, p) in targets)
            {
                if (!SolveTopDown(p, out var q, out double reachRatio, out string why))
                {
                    ok = false;
                    sb.AppendLine($"  {name} {p}: 도달 불가 — {why}");
                    continue;
                }
                double margin = LimitMargin(q, out int tightJoint);
                if (margin < 0)
                {
                    ok = false;
                    sb.AppendLine($"  {name} {p}: J{tightJoint + 1} 가동 범위 초과 (q={Fmt(q)})");
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
                if (name == "Pick") pickQ = q;
                if (name == "Slot32") farQ = q;   // 3단 첫 칸 = 가장 높고 먼 줄의 끝
            }
            sb.AppendLine($"  목표 {targets.Length}개 (픽 1 + 팔레트 {RobotCell.SlotCount}칸), 공구 수직 하향");
            sb.AppendLine($"  도달 여유: 최대 사용률 {worstReach:P0} ({worstReachName}) — J2→손목 거리 / (상완+전완)");
            sb.AppendLine($"  관절 여유: 최소 {worstLimitMargin:F1}° ({worstLimitName})");
            sb.AppendLine($"  TCP 오차: 최대 {worstTcpErr * 1000:F2} mm (Unity 로봇에 관절각을 넣어 측정)");
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

        /// <summary>
        /// 공구가 수직으로 내려보는 자세의 해석적 IK (팔꿈치 위, J4 = 0).
        /// 각도는 ABB 부호(베이스 좌표 오른손): J2 + 팔 앞으로, J3 + 팔 아래로, J5 + 손목 아래로.
        /// </summary>
        static bool SolveTopDown(CellPoint tcp, out double[] q, out double reachRatio, out string why)
        {
            q = new double[6];
            reachRatio = 0;
            why = null;

            // 손목 중심(J5) = TCP 위로 그리퍼 + 플랜지 거리
            double wx = tcp.X, wy = tcp.Y, wz = tcp.Z + RobotCell.GripperLengthM + WristToFlange;
            double j1 = Math.Atan2(wy, wx) * Rad2Deg;
            double r = Math.Sqrt(wx * wx + wy * wy) - J2R;
            double z = wz - J2Z;
            double d = Math.Sqrt(r * r + z * z);
            reachRatio = d / (L2 + L3);
            if (d > L2 + L3 || d < Math.Abs(L2 - L3)) { why = $"J2→손목 {d:F3}m, 범위 {Math.Abs(L2 - L3):F3}~{L2 + L3:F3}m"; return false; }

            double phi = Math.Atan2(z, r);
            double alpha = Math.Acos((L2 * L2 + d * d - L3 * L3) / (2 * L2 * d));
            double e2 = phi + alpha;                                  // 상완 앙각 (수평 기준)
            double er = L2 * Math.Cos(e2), ez = L2 * Math.Sin(e2);   // 팔꿈치(J3) 위치
            double e3 = Math.Atan2(z - ez, r - er);                   // J3 → 손목 방향 앙각

            double j2 = 90.0 - e2 * Rad2Deg;
            double j3 = Beta0 - j2 - e3 * Rad2Deg;
            double j5 = 90.0 - j2 - j3;                               // 공구 방향 = −(J2+J3+J5) = −90°
            q[0] = j1; q[1] = j2; q[2] = j3; q[3] = 0; q[4] = j5; q[5] = 0;
            return true;
        }

        static double LimitMargin(double[] q, out int joint)
        {
            double m = double.MaxValue;
            joint = 0;
            for (int i = 0; i < 6; i++)
            {
                double mi = Math.Min(q[i] - Min[i], Max[i] - q[i]);
                if (mi < m) { m = mi; joint = i; }
            }
            return m;
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
