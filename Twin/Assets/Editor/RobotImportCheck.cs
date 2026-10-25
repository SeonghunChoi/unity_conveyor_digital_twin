using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 로봇 glb(tools/cad/export_robot.py) 임포트 점검.
    ///
    /// 1. 관절 노드 위치: STEP(mm, Z-up, 오른손)에서 뽑은 관절 원점이 Unity에서 기대한 위치에 왔는지.
    ///    좌표 변환 가설: STEP (x, y, z) → glTF (x, z, −y) → glTFast X 반전 → Unity (−x, z, −y), mm→m.
    /// 2. 관절 회전 규약: STEP 좌표에서 오른손 법칙으로 계산한 정기구학(FK) 결과와
    ///    Unity 노드에 localRotation을 넣어 얻은 결과를 비교해, 관절마다 Unity 축·부호를 확정한다.
    ///    (좌표계가 거울 반전되므로 같은 각도를 그대로 넣으면 반대로 돌 수 있다 — 추측하지 않고 측정한다)
    ///
    /// 메뉴: Tools/CAD/Check Robot Import
    /// 명령줄: Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.RobotImportCheck.CheckBatch -logFile robot-check.log
    /// </summary>
    public static class RobotImportCheck
    {
        const string GlbPath = "Assets/ThirdParty/Cad/irb2600.glb";
        const double Tolerance = 0.0005;   // 0.5mm

        // export_robot.py의 CHAIN과 같은 값 (STEP mm). 축 방향은 STEP 좌표.
        static readonly string[] Names = { "J1", "J2", "J3", "J4", "J5", "J6" };
        static readonly Vector3d[] Origins =
        {
            new Vector3d(0, 0, 0), new Vector3d(150, 0, 445), new Vector3d(150, 0, 1145),
            new Vector3d(150, 0, 1260), new Vector3d(945, 0, 1260), new Vector3d(1030, 0, 1260),
        };
        static readonly Vector3d[] Axes =
        {
            new Vector3d(0, 0, 1), new Vector3d(0, 1, 0), new Vector3d(0, 1, 0),
            new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(1, 0, 0),
        };
        // J6 축에서 벗어난 공구 점 (플랜지 면에서 STEP +Z로 100mm) — J6 회전도 위치에 드러나게 한다
        static readonly Vector3d ToolPoint = new Vector3d(1030, 0, 1360);

        [MenuItem("Tools/CAD/Check Robot Import")]
        static void CheckMenu() => Check();

        public static void CheckBatch() => EditorApplication.Exit(Check() ? 0 : 1);

        static bool Check()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
            if (prefab == null)
            {
                Debug.LogError($"[CAD] {GlbPath} 없음. tools/cad/export_robot.py를 먼저 실행하세요.");
                return false;
            }

            var go = Object.Instantiate(prefab);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            bool ok = true;
            try
            {
                var sb = new StringBuilder("[CAD] irb2600.glb 임포트 점검\n");
                var joints = new Transform[6];
                for (int i = 0; i < 6; i++)
                {
                    joints[i] = Find(go.transform, Names[i]);
                    if (joints[i] == null) { sb.AppendLine($"  {Names[i]} 노드 없음"); ok = false; }
                }
                if (!ok) { Debug.LogError(sb.ToString()); return false; }

                // ---- 메시 요약 ----
                int tris = 0;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    int t = 0;
                    for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) t += (int)mf.sharedMesh.GetIndexCount(s) / 3;
                    tris += t;
                    var r = mf.GetComponent<Renderer>();
                    sb.AppendLine($"  {PathOf(mf.transform)}  subMesh={mf.sharedMesh.subMeshCount} tris={t} size={r.bounds.size:F3}");
                }
                sb.AppendLine($"  전체 tris={tris}");

                // ---- 1. 홈 자세 관절 위치 ----
                sb.AppendLine("  [홈 자세 관절 위치] Unity 실제 / 기대 (STEP→Unity 가설)");
                for (int i = 0; i < 6; i++)
                {
                    var expected = ToUnity(Origins[i]);
                    var err = (joints[i].position - expected).magnitude;
                    bool pass = err < Tolerance;
                    ok &= pass;
                    sb.AppendLine($"    {Names[i]} {joints[i].position:F4} / {expected:F4}  {(pass ? "OK" : "불일치 " + err.ToString("F4") + "m")}");
                }

                // ---- 2. 관절별 회전 부호 (한 관절씩 +30°) ----
                sb.AppendLine("  [관절 회전 규약] STEP 오른손 +30° 와 같은 결과를 내는 Unity localRotation");
                var signs = new int[6];
                for (int i = 0; i < 6; i++)
                {
                    var q = new double[6];
                    q[i] = 30;
                    var target = ToUnity(Fk(q));
                    var unityAxis = ToUnityDir(Axes[i]);
                    float errPlus = ToolError(go, joints, q, i, +1, unityAxis, target);
                    float errMinus = ToolError(go, joints, q, i, -1, unityAxis, target);
                    signs[i] = errPlus < errMinus ? +1 : -1;
                    bool pass = Mathf.Min(errPlus, errMinus) < Tolerance;
                    ok &= pass;
                    sb.AppendLine($"    {Names[i]} Unity 축 {unityAxis:F0}  부호 {(signs[i] > 0 ? "+" : "−")}  " +
                                  $"(오차 +:{errPlus:F4} −:{errMinus:F4} m) {(pass ? "OK" : "실패")}");
                }

                // ---- 3. 여러 관절 동시 자세로 교차 확인 ----
                sb.AppendLine("  [복합 자세 교차 확인] 공구 점 Unity / STEP FK");
                double[][] poses =
                {
                    new double[] { 30, 20, -15, 40, 30, 60 },
                    new double[] { -90, -40, 30, -120, 90, -180 },
                    new double[] { 170, 60, -60, 200, -100, 300 },
                };
                foreach (var q in poses)
                {
                    Apply(joints, q, signs);
                    var tool = joints[5].TransformPoint(ToUnityLocal(ToolPoint - Origins[5]));
                    var target = ToUnity(Fk(q));
                    var err = (tool - target).magnitude;
                    bool pass = err < Tolerance;
                    ok &= pass;
                    sb.AppendLine($"    q=({string.Join(", ", q)}) → {tool:F4} / {target:F4}  {(pass ? "OK" : "불일치 " + err.ToString("F4") + "m")}");
                }
                Apply(joints, new double[6], signs);

                sb.AppendLine(ok ? "  결과: 모두 통과" : "  결과: 실패 항목 있음");
                if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
                return ok;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ---------- Unity 쪽 ----------

        static float ToolError(GameObject go, Transform[] joints, double[] q, int i, int sign, Vector3 unityAxis, Vector3 target)
        {
            var signs = new int[6];
            for (int k = 0; k < 6; k++) signs[k] = 1;
            signs[i] = sign;
            Apply(joints, q, signs);
            var tool = joints[5].TransformPoint(ToUnityLocal(ToolPoint - Origins[5]));
            Apply(joints, new double[6], signs);
            return (tool - target).magnitude;
        }

        /// <summary>트윈이 할 일과 같다: 각 관절 노드의 localRotation = AngleAxis(부호 × 각도, Unity 축)</summary>
        static void Apply(Transform[] joints, double[] qDeg, int[] signs)
        {
            for (int i = 0; i < 6; i++)
                joints[i].localRotation = Quaternion.AngleAxis(signs[i] * (float)qDeg[i], ToUnityDir(Axes[i]));
        }

        static Vector3 ToUnity(Vector3d stepMm) => ToUnityDir(stepMm) * 0.001f;
        static Vector3 ToUnityLocal(Vector3d stepMmOffset) => ToUnityDir(stepMmOffset) * 0.001f;
        static Vector3 ToUnityDir(Vector3d v) => new Vector3((float)-v.X, (float)v.Z, (float)-v.Y);

        // ---------- STEP 쪽 정기구학 (오른손 법칙, 홈 자세 기준 지수곱) ----------

        static Vector3d Fk(double[] qDeg)
        {
            // T = R1 R2 ... R6, 각 Ri는 홈 자세의 축선(Origins[i], Axes[i]) 둘레 회전. 공구 점에 안쪽부터 적용.
            var p = ToolPoint;
            for (int i = 5; i >= 0; i--)
                p = RotateAbout(p, Origins[i], Axes[i], qDeg[i]);
            return p;
        }

        static Vector3d RotateAbout(Vector3d p, Vector3d o, Vector3d k, double deg)
        {
            // 로드리게스 회전 (오른손)
            double a = deg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            var v = p - o;
            var r = v * c + Vector3d.Cross(k, v) * s + k * (Vector3d.Dot(k, v) * (1 - c));
            return o + r;
        }

        readonly struct Vector3d
        {
            public readonly double X, Y, Z;
            public Vector3d(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.X * s, a.Y * s, a.Z * s);
            public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static Vector3d Cross(Vector3d a, Vector3d b) =>
                new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t.name == name) return t;
            return null;
        }

        static string PathOf(Transform t)
        {
            var p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
