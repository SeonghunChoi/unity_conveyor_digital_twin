using ConveyorTwin.Twin;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 트윈 씬 생성. conveyor.glb는 라이선스상 저장소에 없으므로, 새로 받은 PC에서는
    /// tools/cad/export_conveyor.py로 glb를 만든 뒤 이 메뉴로 씬을 다시 구성한다.
    /// </summary>
    public static class TwinSceneSetup
    {
        const string GlbPath = "Assets/ThirdParty/Cad/conveyor.glb";
        const string ScenePath = "Assets/Scenes/Twin.unity";

        [MenuItem("Tools/Twin/Setup Scene")]
        static void Setup()
        {
            var glb = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
            if (glb == null)
            {
                EditorUtility.DisplayDialog("Twin", $"{GlbPath} 이 없습니다.\ntools/cad/export_conveyor.py를 먼저 실행하세요.", "확인");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var conveyor = (GameObject)PrefabUtility.InstantiatePrefab(glb);
            conveyor.name = "Conveyor";

            var twin = new GameObject("Twin");
            var controller = twin.AddComponent<ConveyorTwinController>();
            twin.AddComponent<TwinHud>();
            var so = new SerializedObject(controller);
            so.FindProperty("conveyorRoot").objectReferenceValue = conveyor.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            // 입구(-Z) 쪽 비스듬한 위에서 벨트 전체가 보이게
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(1.6f, 1.7f, -2.2f);
                cam.transform.LookAt(new Vector3(0f, 0.75f, 0.1f));
                cam.nearClipPlane = 0.05f;
            }

            // 서버 창을 조작하는 동안에도 트윈이 계속 갱신되어야 한다
            PlayerSettings.runInBackground = true;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log($"[Twin] {ScenePath} 생성 완료. 더미 서버를 실행한 뒤 Play 하세요.");
        }
    }
}
