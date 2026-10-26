using ConveyorTwin.Twin;
using ConveyorTwin.Twin.Hmi;
using ConveyorTwin.Twin.Robot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

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
        const string StatusShaderPath = "Assets/Shaders/StatusLit.shader";
        const string HmiUxmlPath = "Assets/UI/TwinHmi.uxml";
        const string ThemePath = "Assets/UI/TwinTheme.tss";
        const string PanelSettingsPath = "Assets/UI/TwinPanelSettings.asset";
        const string KoreanFontPath = "Assets/ThirdParty/Fonts/KoreanOSFont.asset";

        [MenuItem("Tools/Twin/Setup Scene")]
        static void SetupMenu() => Setup();

        /// <summary>명령줄: Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.TwinSceneSetup.SetupBatch</summary>
        public static void SetupBatch() => EditorApplication.Exit(Setup() ? 0 : 1);

        static bool Setup()
        {
            var glb = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
            if (glb == null)
            {
                if (Application.isBatchMode) Debug.LogError($"[Twin] {GlbPath} 이 없습니다. tools/cad/export_conveyor.py를 먼저 실행하세요.");
                else EditorUtility.DisplayDialog("Twin", $"{GlbPath} 이 없습니다.\ntools/cad/export_conveyor.py를 먼저 실행하세요.", "확인");
                return false;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var conveyor = (GameObject)PrefabUtility.InstantiatePrefab(glb);
            conveyor.name = "Conveyor";

            // 2단계 로봇 셀 (로봇 glb가 없으면 건너뛴다)
            var cell = RobotCellBuilder.Build();

            // 상태 셰이더는 직접 참조해야 플레이어 빌드에 포함된다 (Shader.Find만으로는 빠질 수 있음)
            var statusShader = AssetDatabase.LoadAssetAtPath<Shader>(StatusShaderPath);
            if (statusShader == null)
                Debug.LogWarning($"[Twin] {StatusShaderPath} 를 찾을 수 없습니다. 상태 하이라이트 없이 구성합니다.");

            var twin = new GameObject("Twin");
            var controller = twin.AddComponent<ConveyorTwinController>();
            var hud = twin.AddComponent<TwinHud>();
            var visuals = twin.AddComponent<TwinVisuals>();
            var launcher = twin.AddComponent<ServerLauncher>();
            var so = new SerializedObject(controller);
            so.FindProperty("conveyorRoot").objectReferenceValue = conveyor.transform;
            so.FindProperty("statusShader").objectReferenceValue = statusShader;
            so.ApplyModifiedPropertiesWithoutUndo();
            // 2단계 로봇 미러: OPC UA 신호로 로봇을 그리고 컨베이어 박스를 그리퍼·팔레트로 넘긴다
            if (cell != null)
            {
                var robotTwin = twin.AddComponent<RobotTwin>();
                var rso = new SerializedObject(robotTwin);
                rso.FindProperty("conveyor").objectReferenceValue = controller;
                rso.FindProperty("robotRoot").objectReferenceValue = cell.transform.Find("Robot");
                rso.FindProperty("pallet").objectReferenceValue = cell.transform.Find("Pallet");
                rso.ApplyModifiedPropertiesWithoutUndo();
            }
            var vso = new SerializedObject(visuals);
            vso.FindProperty("statusShader").objectReferenceValue = statusShader;
            vso.ApplyModifiedPropertiesWithoutUndo();

            // HMI (UI Toolkit)
            var hmiGo = new GameObject("HMI");
            var doc = hmiGo.AddComponent<UIDocument>();
            doc.panelSettings = GetOrCreatePanelSettings();
            doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(HmiUxmlPath);
            var hmi = hmiGo.AddComponent<TwinHmi>();
            var hso = new SerializedObject(hmi);
            hso.FindProperty("twin").objectReferenceValue = controller;
            hso.FindProperty("launcher").objectReferenceValue = launcher;
            hso.FindProperty("diagnosticHud").objectReferenceValue = hud;
            hso.FindProperty("koreanFont").objectReferenceValue = GetOrCreateKoreanFont();
            hso.ApplyModifiedPropertiesWithoutUndo();

            // 좌우 패널 사이로 벨트와 로봇 셀(팔레트는 −X 쪽)이 함께 보이게: 입구(-Z)·팔레트 쪽 비스듬한 위
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(-2.6f, 2.5f, -2.2f);
                cam.transform.LookAt(new Vector3(-0.35f, 0.6f, 0.75f));
                cam.nearClipPlane = 0.05f;
                cam.fieldOfView = 50f;
            }

            // 서버 창을 조작하는 동안에도 트윈이 계속 갱신되어야 한다
            PlayerSettings.runInBackground = true;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log($"[Twin] {ScenePath} 생성 완료. Play하면 서버가 없을 때 자동으로 실행합니다.");
            return true;
        }

        /// <summary>
        /// HMI 한글 폰트: OS에 설치된 맑은 고딕을 동적으로 쓰는 폰트 에셋을 에디터에서 만든다.
        /// 런타임에 만들면 플레이 모드 종료 때 글리프 아틀라스가 정리되어 UI Toolkit 텍스트 생성에서 예외가 났다.
        /// 에셋 아틀라스에 글리프 이미지가 쌓이므로 저장소에는 넣지 않는다 (.gitignore의 ThirdParty/Fonts).
        /// </summary>
        static FontAsset GetOrCreateKoreanFont()
        {
            var fa = AssetDatabase.LoadAssetAtPath<FontAsset>(KoreanFontPath);
            if (fa != null) return fa;

            foreach (var family in new[] { "Malgun Gothic", "맑은 고딕" })
            {
                fa = FontAsset.CreateFontAsset(family, "Regular", 90);
                if (fa != null) break;
            }
            if (fa == null)
            {
                Debug.LogWarning("[Twin] OS 한글 폰트(맑은 고딕)를 찾지 못했습니다. HMI 한글이 깨질 수 있습니다.");
                return null;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(KoreanFontPath));
            fa.name = "KoreanOSFont";
            AssetDatabase.CreateAsset(fa, KoreanFontPath);
            // 아틀라스 텍스처와 머티리얼을 하위 에셋으로 묶어야 함께 저장된다
            foreach (var tex in fa.atlasTextures)
            {
                if (tex == null) continue;
                tex.name = "KoreanOSFont Atlas";
                AssetDatabase.AddObjectToAsset(tex, fa);
            }
            if (fa.material != null)
            {
                fa.material.name = "KoreanOSFont Material";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Twin] 한글 폰트 에셋 생성: {KoreanFontPath} ({fa.atlasPopulationMode})");
            return fa;
        }

        /// <summary>HMI 패널 설정: 1920×1080 기준으로 화면 크기에 맞춰 확대·축소</summary>
        static PanelSettings GetOrCreatePanelSettings()
        {
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, PanelSettingsPath);
            }
            ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.match = 0.5f;
            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();
            return ps;
        }
    }
}
