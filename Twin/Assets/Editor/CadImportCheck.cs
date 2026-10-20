using System.Text;
using UnityEditor;
using UnityEngine;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// CAD glb 임포트 결과 점검. tools/cad/export_conveyor.py 결과물이 Unity에서
    /// 기대한 단위·축·계층·서브메시로 들어왔는지 콘솔에 출력한다.
    /// </summary>
    public static class CadImportCheck
    {
        const string GlbPath = "Assets/ThirdParty/Cad/conveyor.glb";

        [MenuItem("Tools/CAD/Check Conveyor Import")]
        static void Check()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
            if (prefab == null)
            {
                Debug.LogError($"[CAD] {GlbPath} 없음. tools/cad/export_conveyor.py를 먼저 실행하세요.");
                return;
            }

            // 월드 좌표를 얻기 위해 임시로 인스턴스화
            var go = Object.Instantiate(prefab);
            try
            {
                var sb = new StringBuilder("[CAD] conveyor.glb 임포트 점검\n");
                int tris = 0, subMeshes = 0;
                var total = new Bounds();
                bool first = true;

                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh = mf.sharedMesh;
                    var r = mf.GetComponent<Renderer>();
                    int t = 0;
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        t += (int)mesh.GetIndexCount(i) / 3;
                    tris += t;
                    subMeshes += mesh.subMeshCount;
                    if (first) { total = r.bounds; first = false; }
                    else total.Encapsulate(r.bounds);

                    sb.AppendLine($"  {Path(mf.transform)}  subMesh={mesh.subMeshCount} tris={t} " +
                                  $"uv={mesh.uv.Length > 0} materials={r.sharedMaterials.Length} " +
                                  $"center={r.bounds.center:F3} size={r.bounds.size:F3}");
                }

                var head = Find(go.transform, "Drum_Head");
                var tail = Find(go.transform, "Drum_Tail");
                sb.AppendLine($"  전체 bounds center={total.center:F3} size={total.size:F3}  tris={tris} subMeshes={subMeshes}");
                if (head != null && tail != null)
                {
                    // 반송 방향 = 아이들 드럼(입구) → 구동 드럼(출구)
                    var flow = head.position - tail.position;
                    sb.AppendLine($"  Drum_Tail(입구) pivot={tail.position:F3}  Drum_Head(출구) pivot={head.position:F3}");
                    sb.AppendLine($"  반송 방향={flow.normalized:F3}  드럼 중심 간 거리={flow.magnitude:F3} m (기대값 1.950)");
                }
                Debug.Log(sb.ToString());
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t.name == name) return t;
            return null;
        }

        static string Path(Transform t)
        {
            var p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
