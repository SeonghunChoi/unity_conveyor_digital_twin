using ConveyorTwin.Shared;
using UnityEditor;
using UnityEngine;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 로봇 셀(로봇 + 진공 그리퍼 + EUR 팔레트)을 씬에 구성한다. TwinSceneSetup이 호출한다.
    /// 배치 수치는 src/Shared/RobotCell (서버와 공용), 관절 규약은 docs/cad-robot.md 4절.
    /// 그리퍼와 팔레트는 제조사 CAD 없이 프리미티브로 만든다 (1단계 박스와 같은 방침).
    /// </summary>
    public static class RobotCellBuilder
    {
        public const string RobotGlbPath = "Assets/ThirdParty/Cad/irb2600.glb";

        /// <summary>로봇 베이스(J1 축과 바닥 교점)의 Unity 위치. 팔은 홈 자세에서 −Z(컨베이어 출구)를 향한다</summary>
        public static readonly Vector3 RobotPosition = new Vector3(0f, 0f, 1.70f);
        public static readonly Quaternion RobotRotation = Quaternion.Euler(0f, -90f, 0f);

        /// <summary>로봇 베이스 좌표(RobotCell, m, Z-up 오른손) → Unity 월드</summary>
        public static Vector3 CellToWorld(CellPoint p) =>
            RobotPosition + new Vector3((float)p.Y, (float)p.Z, (float)-p.X);

        public static GameObject Build()
        {
            var glb = AssetDatabase.LoadAssetAtPath<GameObject>(RobotGlbPath);
            if (glb == null)
            {
                Debug.LogWarning($"[Twin] {RobotGlbPath} 이 없어 로봇 셀 없이 구성합니다. tools/cad/export_robot.py를 먼저 실행하세요.");
                return null;
            }

            var cell = new GameObject("RobotCell");

            var robot = (GameObject)PrefabUtility.InstantiatePrefab(glb);
            robot.name = "Robot";
            robot.transform.SetParent(cell.transform, false);
            robot.transform.SetPositionAndRotation(RobotPosition, RobotRotation);
            Paint(robot, Lit("RobotBody", new Color(0.93f, 0.93f, 0.91f), 0.55f));

            var j6 = FindDeep(robot.transform, "J6");
            if (j6 != null) BuildGripper(j6);
            else Debug.LogWarning("[Twin] 로봇에 J6 노드가 없습니다. 그리퍼를 붙이지 못했습니다.");

            BuildPallet(cell.transform);
            return cell;
        }

        /// <summary>
        /// 진공 그리퍼. J6 노드(원점 = 플랜지 면 중심)에 붙인다.
        /// 로봇 로컬에서 플랜지 법선은 −X (STEP +X), 흡착면까지 RobotCell.GripperLengthM.
        /// </summary>
        static void BuildGripper(Transform j6)
        {
            var steel = Lit("GripperSteel", new Color(0.32f, 0.34f, 0.36f), 0.6f);
            var rubber = Lit("SuctionRubber", new Color(0.08f, 0.08f, 0.08f), 0.2f);

            var g = new GameObject("Gripper").transform;
            g.SetParent(j6, false);

            // 길이 방향(−X)으로 쌓는다: 어댑터 0.04 → 몸체 0.06 → 패드 판 0.02 → 흡착 컵 0.03 = 0.15
            CylinderX(g, "Adapter", -0.020f, 0.040f, 0.045f, steel);
            Box(g, "Body", new Vector3(-0.070f, 0, 0), new Vector3(0.060f, 0.080f, 0.080f), steel);
            Box(g, "PadPlate", new Vector3(-0.110f, 0, 0), new Vector3(0.020f, 0.180f, 0.260f), steel);
            foreach (var y in new[] { -0.06f, 0.06f })
                foreach (var z in new[] { -0.09f, 0.09f })
                    CylinderX(g, "Cup", -0.135f, 0.030f, 0.025f, rubber, y, z);

            var tcp = new GameObject("TCP").transform;   // 흡착면 중심 = 공구 중심점
            tcp.SetParent(g, false);
            tcp.localPosition = new Vector3(-(float)RobotCell.GripperLengthM, 0, 0);
        }

        /// <summary>EUR 팔레트 1200×800×144 (하판 22 + 블록 78 + 스트링거 22 + 상판 22)</summary>
        static void BuildPallet(Transform parent)
        {
            var wood = Lit("PalletWood", new Color(0.74f, 0.58f, 0.38f), 0.15f);
            var pallet = new GameObject("Pallet").transform;
            pallet.SetParent(parent, false);
            pallet.position = CellToWorld(RobotCell.PalletCenter);

            // 팔레트 긴 변(베이스 X) = Unity Z, 짧은 변(베이스 Y) = Unity X
            float len = (float)RobotCell.PalletLengthM, wid = (float)RobotCell.PalletWidthM;
            float[] across = { -0.35f, 0f, 0.35f };       // 짧은 변 방향 3열
            float[] along = { -0.5275f, 0f, 0.5275f };    // 긴 변 방향 3열

            foreach (var x in across)
                Box(pallet, "BottomBoard", new Vector3(x, 0.011f, 0), new Vector3(0.10f, 0.022f, len), wood);
            foreach (var x in across)
                foreach (var z in along)
                    Box(pallet, "Block", new Vector3(x, 0.061f, z), new Vector3(0.10f, 0.078f, 0.145f), wood);
            foreach (var z in along)
                Box(pallet, "Stringer", new Vector3(0, 0.111f, z), new Vector3(wid, 0.022f, 0.145f), wood);
            foreach (var x in new[] { -0.35f, -0.175f, 0f, 0.175f, 0.35f })
                Box(pallet, "TopBoard", new Vector3(x, 0.133f, 0), new Vector3(0.10f, 0.022f, len), wood);
        }

        // ---------- 프리미티브 도우미 ----------

        static void Box(Transform parent, string name, Vector3 center, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
        }

        /// <summary>로컬 X축 방향 원통 (중심 x, 길이, 반지름)</summary>
        static void CylinderX(Transform parent, string name, float x, float length, float radius, Material m, float y = 0, float z = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(x, y, z);
            go.transform.localRotation = Quaternion.Euler(0, 0, 90);         // 원통 기본 축 Y → X
            go.transform.localScale = new Vector3(radius * 2, length / 2, radius * 2);   // 기본 원통 높이 2
            go.GetComponent<Renderer>().sharedMaterial = m;
        }

        static Material Lit(string name, Color color, float smoothness)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        static void Paint(GameObject root, Material m)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                r.sharedMaterial = m;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }
    }
}
