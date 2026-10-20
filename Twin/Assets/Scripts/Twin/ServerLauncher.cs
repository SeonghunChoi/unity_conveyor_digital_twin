using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 더미 서버 실행 관리 (CLAUDE.md 아키텍처: 서버 자동 실행).
    ///
    /// - 트윈 시작 시: 127.0.0.1:1883에 TCP 접속을 시도해 응답이 없으면 서버 exe를 찾아 실행
    /// - 실행 중 서버가 사라지면: 스스로 다시 띄우지 않는다. HMI가 운전자에게 묻고 TryLaunch()를 부른다.
    ///   서버는 "현실" 역할이라 트윈이 몰래 되살리면 장애를 감추게 된다 (원칙 1, 3).
    ///   실제 현장이라면 트윈은 PLC·브로커를 재부팅할 권한이 없고 점검 요청을 띄우는 정도다 → 이 기능은 시연 편의용
    /// - 실행 파일 찾는 순서: 환경 변수 CONVEYOR_SERVER_EXE → 빌드 폴더의 Server/ → 개발 중 dotnet publish 결과 → Debug 빌드
    /// - 트윈을 닫아도 서버는 끄지 않는다 (트윈을 다시 켜도 같은 세션에 이어 붙음)
    /// </summary>
    public sealed class ServerLauncher : MonoBehaviour
    {
        public const string ExeName = "ConveyorTwin.DummyServer.exe";
        const string ProcessName = "ConveyorTwin.DummyServer";
        const string EnvVar = "CONVEYOR_SERVER_EXE";
        const string BrokerHost = "127.0.0.1";
        const int BrokerPort = 1883;
        const int ProbeTimeoutMs = 500;

        public enum LaunchState
        {
            Checking,
            AlreadyRunning,     // 시작할 때 이미 서버가 있었음 (VS 등에서 직접 실행)
            Launched,           // 트윈이 실행함
            NotFound,           // 실행 파일을 찾지 못함
            Failed,             // 실행 실패
            Disabled,
        }

        [SerializeField] bool autoLaunch = true;

        public LaunchState State { get; private set; } = LaunchState.Checking;
        public string ExePath { get; private set; }
        public int ProcessId { get; private set; }
        public string Error { get; private set; }
        public int LaunchCount { get; private set; }
        /// <summary>마지막으로 실행을 시도한 시각 (Time.unscaledTime). 실행 직후 접속까지 몇 초 걸리는 동안 다시 묻지 않기 위해</summary>
        public float LastLaunchTime { get; private set; } = -999f;

        async void Start()
        {
            if (!autoLaunch)
            {
                State = LaunchState.Disabled;
                return;
            }

            bool running = await Task.Run(() => IsBrokerListening());
            if (this == null) return;   // 대기 중 플레이 모드 종료
            if (running)
            {
                State = LaunchState.AlreadyRunning;
                return;
            }
            TryLaunch();
        }

        /// <summary>서버 프로세스가 떠 있는지 (응답 여부와 별개)</summary>
        public static bool IsServerProcessRunning()
        {
            try
            {
                var ps = Process.GetProcessesByName(ProcessName);
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>서버 실행. 이미 프로세스가 있으면 실행하지 않는다 (같은 포트 충돌 방지)</summary>
        public bool TryLaunch()
        {
            if (IsServerProcessRunning())
            {
                Error = "서버 프로세스가 이미 실행 중입니다 (응답이 없으면 서버 창을 확인하세요)";
                return false;
            }

            ExePath = FindServerExe();
            if (ExePath == null)
            {
                State = LaunchState.NotFound;
                Error = $"{ExeName}을(를) 찾을 수 없습니다. dotnet build src 또는 환경 변수 {EnvVar}를 확인하세요.";
                Debug.LogWarning("[Twin] " + Error);
                return false;
            }

            try
            {
                var p = Process.Start(new ProcessStartInfo(ExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(ExePath),
                });
                ProcessId = p?.Id ?? 0;
                State = LaunchState.Launched;
                Error = null;
                LaunchCount++;
                LastLaunchTime = Time.unscaledTime;
                Debug.Log($"[Twin] 서버 실행 ({LaunchCount}회째): {ExePath} (pid {ProcessId})");
                return true;
            }
            catch (Exception e)
            {
                State = LaunchState.Failed;
                Error = e.Message;
                Debug.LogWarning($"[Twin] 서버 실행 실패: {e.Message}");
                return false;
            }
        }

        static bool IsBrokerListening()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(BrokerHost, BrokerPort);
                    return connect.Wait(ProbeTimeoutMs) && client.Connected;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>실행 파일 후보를 순서대로 찾는다</summary>
        static string FindServerExe()
        {
            var env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return env;

            // 빌드: <빌드 폴더>/Twin_Data → <빌드 폴더>/Server/ (13일 차 배포 구성)
            // 에디터: <저장소>/Twin/Assets → <저장소>/src/DummyServer/bin/...
            var data = Application.dataPath;
            var candidates = new[]
            {
                Path.Combine(data, "..", "Server", ExeName),
                Path.Combine(data, "..", "..", "src", "DummyServer", "bin", "Release", "net8.0-windows", "win-x64", "publish", ExeName),
                Path.Combine(data, "..", "..", "src", "DummyServer", "bin", "Release", "net8.0-windows", ExeName),
                Path.Combine(data, "..", "..", "src", "DummyServer", "bin", "Debug", "net8.0-windows", ExeName),
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            return null;
        }
    }
}
