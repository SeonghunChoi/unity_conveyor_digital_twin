using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 배포 빌드 (13일 차). 결과물은 저장소 루트의 dist/ConveyorTwin/ (gitignore 대상).
    ///
    ///   dist/ConveyorTwin/
    ///     ConveyorTwin.exe, ConveyorTwin_Data/, UnityPlayer.dll, MonoBleedingEdge/   ← 트윈 (Unity, Mono)
    ///     Server/ConveyorTwin.DummyServer.exe                                        ← 더미 서버 (self-contained 단일 exe)
    ///     실행방법.txt
    ///
    /// 트윈의 ServerLauncher가 &lt;빌드 폴더&gt;/Server/에서 서버를 찾아 자동 실행하므로, 받는 사람은 ConveyorTwin.exe만 실행하면 된다.
    /// 서버는 .NET 런타임을 포함하므로(self-contained) 클린 PC에 .NET 설치가 필요 없다.
    ///
    /// 에디터 메뉴: Tools/Twin/Build Release
    /// 명령줄 (에디터를 닫은 상태):
    ///   Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.TwinBuild.BuildReleaseBatch -logFile build.log
    /// </summary>
    public static class TwinBuild
    {
        const string ProductName = "ConveyorTwin";
        const string ScenePath = "Assets/Scenes/Twin.unity";
        const string GlbPath = "Assets/ThirdParty/Cad/conveyor.glb";
        const string RobotGlbPath = "Assets/ThirdParty/Cad/irb2600.glb";
        const string KoreanFontPath = "Assets/ThirdParty/Fonts/KoreanOSFont.asset";
        const string ServerExe = "ConveyorTwin.DummyServer.exe";

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string DistDir => Path.Combine(RepoRoot, "dist", ProductName);

        [MenuItem("Tools/Twin/Build Release")]
        static void BuildReleaseMenu()
        {
            var error = BuildRelease();
            if (error == null)
            {
                EditorUtility.RevealInFinder(Path.Combine(DistDir, ProductName + ".exe"));
                EditorUtility.DisplayDialog("Build Release", $"빌드 완료\n{DistDir}", "확인");
            }
            else
            {
                EditorUtility.DisplayDialog("Build Release", "빌드 실패\n" + error, "확인");
            }
        }

        /// <summary>명령줄 진입점. 실패하면 종료 코드 1</summary>
        public static void BuildReleaseBatch()
        {
            var error = BuildRelease();
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        /// <returns>실패 사유 (성공이면 null)</returns>
        static string BuildRelease()
        {
            // ---- 사전 점검: 저장소에 없는 생성물 (CAD glb, 한글 폰트) ----
            if (!File.Exists(GlbPath))
                return $"{GlbPath} 이 없습니다. tools/cad/export_conveyor.py를 먼저 실행하세요.";
            if (!File.Exists(RobotGlbPath))
                return $"{RobotGlbPath} 이 없습니다. tools/cad/export_robot.py를 먼저 실행하세요 (2단계 로봇 셀).";
            if (!File.Exists(KoreanFontPath))
                return $"{KoreanFontPath} 이 없습니다. Tools/Twin/Setup Scene을 먼저 실행하세요.";
            if (!File.Exists(ScenePath))
                return $"{ScenePath} 이 없습니다. Tools/Twin/Setup Scene을 먼저 실행하세요.";

            // ---- 플레이어 설정 ----
            PlayerSettings.productName = ProductName;
            PlayerSettings.companyName = "SeonghunChoi";
            PlayerSettings.runInBackground = true;          // 서버 창을 조작하는 동안에도 트윈 갱신
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            // ---- 트윈 (Unity 플레이어) ----
            if (Directory.Exists(DistDir))
                Directory.Delete(DistDir, true);
            Directory.CreateDirectory(DistDir);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(DistDir, ProductName + ".exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                return $"플레이어 빌드 실패: {summary.result} (오류 {summary.totalErrors}건, Console 확인)";
            Debug.Log($"[Build] 트윈 빌드 완료: {summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:F0}s");

            // Burst 디버그 정보 등 "_DoNotShip" 폴더는 배포물에서 뺀다 (Burst는 Collections 패키지 의존성으로 들어옴)
            foreach (var dir in Directory.GetDirectories(DistDir, "*_DoNotShip"))
                Directory.Delete(dir, true);

            // ---- 더미 서버 (self-contained 단일 exe, 압축) ----
            var serverDir = Path.Combine(DistDir, "Server");
            var publishError = Run("dotnet",
                "publish src/DummyServer -c Release -r win-x64 --self-contained " +
                "-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true " +
                "-p:EnableCompressionInSingleFile=true -p:DebugType=none " +
                $"-o \"{serverDir}\" -nologo -v q",
                RepoRoot);
            if (publishError != null)
                return "서버 publish 실패: " + publishError;
            var serverPath = Path.Combine(serverDir, ServerExe);
            if (!File.Exists(serverPath))
                return $"서버 exe가 없습니다: {serverPath}";
            Debug.Log($"[Build] 서버 publish 완료: {new FileInfo(serverPath).Length / (1024 * 1024)} MB");

            WriteReadme();
            Debug.Log($"[Build] 배포 폴더: {DistDir}");
            return null;
        }

        /// <summary>외부 명령 실행. 실패하면 출력 마지막 부분을 돌려준다</summary>
        static string Run(string exe, string args, string workDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) return null;
                    var all = (stdout + "\n" + stderr).Trim();
                    return $"종료 코드 {p.ExitCode}\n{(all.Length > 1500 ? all.Substring(all.Length - 1500) : all)}";
                }
            }
            catch (Exception e)
            {
                return $"{exe} 실행 실패 ({e.Message}). .NET SDK가 PATH에 있는지 확인하세요.";
            }
        }

        static void WriteReadme()
        {
            var text =
$@"ConveyorTwin - 컨베이어 + 로봇 셀 디지털 트윈 (포트폴리오 2단계)
빌드: {DateTime.Now:yyyy-MM-dd HH:mm}

[실행]
ConveyorTwin.exe 를 실행하세요.
더미 서버(가상 설비 + MQTT 브로커 + 로봇 OPC UA 서버)가 실행 중이 아니면 트윈이 Server\ConveyorTwin.DummyServer.exe 를 자동으로 띄웁니다.
처음 실행할 때 트윈이 서버로부터 OPC UA 인증서를 발급받습니다 (몇 초, 사용자 데이터 폴더에 저장).
.NET 등 별도 설치는 필요 없습니다 (Windows 10/11 64비트).

[시연]
1. 서버 창의 '장애 주입' 버튼을 누르면 트윈 화면이 반응합니다.
   컨베이어(MQTT): 연결 끊김 / 전송 지연 3초 / 순서 뒤섞기 / 이상값 송신 / 물건 걸림 / 모터 정지
   로봇 셀(OPC UA): 로봇 통신 끊김 / 비상정지 / 흡착 실패 / 관절 엔코더 이상
2. 다시 누르면 복구됩니다. 서버 창의 송신 로그(보낸 것)와 트윈 화면(받은 것)을 비교해 보세요.
3. 트윈 오른쪽 위 '진단' 버튼은 수신 상세 정보를 보여줍니다.

[참고]
- 통신은 이 PC 안(MQTT 127.0.0.1:1883, OPC UA 127.0.0.1:4840)에서만 이루어지며 외부 네트워크를 쓰지 않습니다.
- 트윈을 닫아도 서버는 계속 실행됩니다 (서버는 '현실' 역할). 서버 창을 닫으면 종료됩니다.
- 한글 표시는 Windows 기본 글꼴(맑은 고딕)을 사용합니다.
- 컨베이어 3D 모델은 mk Technology Group의 GUF-P 2000, 로봇 3D 모델은 ABB의 IRB 2600 CAD 데이터를 변환한 것입니다.
  제조사 이용 조건에 따라 재배포하지 마세요.
";
            // 메모장에서 한글이 깨지지 않도록 BOM 포함 UTF-8
            File.WriteAllText(Path.Combine(DistDir, "실행방법.txt"), text, new UTF8Encoding(true));
        }
    }
}
