using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ConveyorTwin.Twin.OpcUa;
using ConveyorTwin.Twin.Robot;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 배포 빌드 자체 점검 모드 (2단계 9일 차, 클린 PC 테스트용).
    ///   ConveyorTwin.exe -selfcheck 120 -selfcheckOut C:\results
    /// 정해진 시간 동안 평소처럼 돌면서 연결 상태·알람 타임라인·인계 오차를 &lt;out&gt;/selfcheck.log에 남기고,
    /// HMI가 포함된 화면을 &lt;out&gt;/shot_*.png로 찍은 뒤 종료한다 (종료 코드 0 = 통과).
    /// 사람이 화면을 볼 수 없는 Windows 샌드박스에서 결과를 밖으로 꺼내기 위한 것이다. 인수가 없으면 아무것도 하지 않는다.
    /// 로봇이 처음 접속하면 "[PlayState] robot connected" 줄을 남긴다 — 샌드박스 안의 장애 조작 스크립트가 이 줄을 보고 시작한다.
    /// </summary>
    public sealed class SelfCheck : MonoBehaviour
    {
        float _seconds;
        string _outDir;
        string _log;
        float _connectedAt = -1;
        float _nextSample;
        bool _normalShot;
        int _lastGrips;
        int _maxJams;
        readonly List<double> _gripOffsets = new List<double>();
        readonly HashSet<string> _active = new HashSet<string>();
        readonly HashSet<string> _shot = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-selfcheck");
            if (i < 0) return;
            float seconds = i + 1 < args.Length && float.TryParse(args[i + 1], out var s) ? s : 90f;
            int o = Array.IndexOf(args, "-selfcheckOut");
            string dir = o >= 0 && o + 1 < args.Length ? args[o + 1] : Path.Combine(Application.persistentDataPath, "selfcheck");
            var go = new GameObject("SelfCheck");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfCheck>().Init(seconds, dir);
        }

        void Init(float seconds, string dir)
        {
            _seconds = seconds;
            _outDir = dir;
            Directory.CreateDirectory(_outDir);
            _log = Path.Combine(_outDir, "selfcheck.log");
            File.WriteAllText(_log, "", new UTF8Encoding(true));
            Line($"자체 점검 시작 {seconds:F0}s · Unity {Application.unityVersion} · {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · " +
                 $"{SystemInfo.operatingSystem} · GPU {SystemInfo.graphicsDeviceName} · 데이터 {Application.persistentDataPath}");
            Application.logMessageReceived += OnLog;
        }

        void OnDestroy() => Application.logMessageReceived -= OnLog;

        void OnLog(string msg, string stack, LogType type)
        {
            if (msg.StartsWith("[Robot]") || msg.StartsWith("[Twin]") || type == LogType.Exception)
                Line((type == LogType.Exception ? "예외 " : "") + msg);
        }

        void Line(string text)
        {
            try { File.AppendAllText(_log, $"{Time.realtimeSinceStartup,7:F1}s  {text}{Environment.NewLine}"); }
            catch (IOException) { }
        }

        void Update()
        {
            var conveyor = FindAnyObjectByType<ConveyorTwinController>();
            var robot = FindAnyObjectByType<RobotTwin>();
            if (conveyor == null) return;
            float t = Time.realtimeSinceStartup;
            if (t >= _nextSample)
            {
                _nextSample = t + 0.25f;
                Sample(conveyor, robot, t);
            }
            if (t >= _seconds) Finish(conveyor, robot);
        }

        void Sample(ConveyorTwinController conveyor, RobotTwin robot, float t)
        {
            _maxJams = Math.Max(_maxJams, conveyor.JammedCount);
            if (robot != null && _connectedAt < 0 && robot.LinkState == RobotLinkState.Connected)
            {
                _connectedAt = t;
                Line("[PlayState] robot connected");
            }
            if (!_normalShot && _connectedAt > 0 && t - _connectedAt > 6f)
            {
                _normalShot = true;
                Shot("normal");
            }

            // 알람 타임라인
            var now = new HashSet<string>(conveyor.Alarms.History.Where(a => a.Active).Select(a => a.Key));
            foreach (var k in now.Except(_active))
            {
                Line($"+ {k}: {conveyor.Alarms.History.First(a => a.Active && a.Key == k).Text}");
                if (k.StartsWith("robot_") && _connectedAt > 0 && _shot.Add(k)) Shot(k);
            }
            foreach (var k in _active.Except(now)) Line($"- {k}");
            _active.Clear();
            _active.UnionWith(now);

            // 집기 오차 (박스를 TCP에 맞추기 전, 믿을 수 있는 구간만)
            if (robot != null && robot.GripCount != _lastGrips)
            {
                _lastGrips = robot.GripCount;
                if (!robot.LastGripDuringBadData) _gripOffsets.Add(robot.LastGripOffsetM);
            }
        }

        void Shot(string name)
        {
            // 화면(HMI 포함)을 그대로 저장. 프레임 끝에 기록된다
            ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"shot_{name}.png"));
            Line($"스크린샷 shot_{name}.png");
        }

        bool _finished;

        void Finish(ConveyorTwinController conveyor, RobotTwin robot)
        {
            if (_finished) return;
            _finished = true;
            Shot("end");
            var launcher = FindAnyObjectByType<ServerLauncher>();
            bool robotOk = robot != null && robot.LinkState == RobotLinkState.Connected && _gripOffsets.Count > 0;
            bool ok = conveyor.BrokerConnected && conveyor.DeviceOnline == true && robotOk && _maxJams == 0;

            Line("---- 결과 ----");
            Line($"서버: {launcher?.State} {launcher?.ExePath}");
            Line($"컨베이어 MQTT: {(conveyor.BrokerConnected ? "연결" : "끊김")}, 설비 {conveyor.DeviceOnline}, 입구 {conveyor.Tracker.Entered} / 출구 {conveyor.Tracker.Exited}, 최대 잼 {_maxJams}");
            if (robot != null)
            {
                var g = robot.Gate;
                Line($"로봇 OPC UA: {robot.LinkState} {robot.LinkError}, 검증 통과 {g.Accepted} / Bad {g.RejectedBad} / 과거 {g.RejectedOld} / 범위 밖 {g.RejectedRange}");
                Line($"사이클 {robot.CycleCount}, 집기 {robot.GripCount}, 팔레트 표시 {robot.BoxesOnPallet} (관측 {robot.BoxesObserved}, 추정 {robot.BoxesEstimated})");
            }
            if (_gripOffsets.Count > 0)
                Line($"집기 오차 (맞추기 전): 평균 {_gripOffsets.Average() * 1000:F1}mm, 최대 {_gripOffsets.Max() * 1000:F1}mm ({_gripOffsets.Count}회)");
            Line(ok ? "결과: 통과" : "결과: 실패");
            // 스크린샷이 기록될 시간을 준 뒤 종료
            Invoke(ok ? nameof(QuitOk) : nameof(QuitFail), 1.0f);
        }

        void QuitOk() => Application.Quit(0);
        void QuitFail() => Application.Quit(1);
    }
}
