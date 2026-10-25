using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ConveyorTwin.Spikes.EditorTools
{
    /// <summary>
    /// OPC UA 스파이크 빌드. 배포 빌드(TwinBuild)와 같은 Mono 백엔드·스트리핑 설정으로
    /// OpcUaSpikeClient만 있는 씬을 dist/OpcUaSpike/ 에 빌드한다 (gitignore 대상).
    ///
    /// 명령줄 (Twin 프로젝트를 에디터에서 닫은 상태):
    ///   Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.Spikes.EditorTools.OpcUaSpikeBuild.BuildBatch -logFile spike-build.log
    /// 실행 (스파이크 서버를 먼저 띄운다):
    ///   dist/OpcUaSpike/OpcUaSpike.exe -batchmode -nographics -logFile spike-run.log
    /// </summary>
    public static class OpcUaSpikeBuild
    {
        const string ScenePath = "Assets/Spikes/OpcUaSpike/OpcUaSpike.unity";

        [MenuItem("Tools/Spikes/Build OPC UA Spike")]
        static void BuildMenu() => Build();

        public static void BuildBatch() => EditorApplication.Exit(Build() ? 0 : 1);

        static bool Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("OpcUaSpike").AddComponent<OpcUaSpikeClient>();
            EditorSceneManager.SaveScene(scene, ScenePath);

            // 배포 빌드와 같은 조건 (TwinBuild.BuildRelease 참고)
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            Debug.Log($"[OpcUaSpike] 스트리핑 수준: {PlayerSettings.GetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.Standalone)}");

            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dist", "OpcUaSpike"));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, "OpcUaSpike.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[OpcUaSpike] 빌드 결과: {report.summary.result}, 오류 {report.summary.totalErrors}건 → {outDir}");
            return report.summary.result == BuildResult.Succeeded;
        }
    }
}
