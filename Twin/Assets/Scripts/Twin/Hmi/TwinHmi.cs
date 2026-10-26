using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;
using ConveyorTwin.Twin.OpcUa;
using ConveyorTwin.Twin.Robot;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace ConveyorTwin.Twin.Hmi
{
    /// <summary>
    /// 운전자용 HMI 대시보드 (UI Toolkit). 레이아웃은 TwinHmi.uxml, 스타일은 TwinHmi.uss.
    ///
    /// 표시 원칙 (ISA-101 고성능 HMI):
    /// - 정상은 회색, 이상만 색 (경고 주황, 중대 빨강, 안내 파랑)
    /// - 값마다 정상 운전 범위 띠와 60초 추세를 함께 보여 "지금 값"보다 "정상에서 벗어났는가"를 먼저 읽게 한다
    /// - 값이 믿을 수 없는 상태(stale, 오프라인, 화면 지연)는 값 옆과 상단 칩, 가운데 배너로 분명히 알린다
    ///
    /// 개발용 텍스트 HUD(TwinHud)는 기본으로 숨기고 "진단" 버튼으로 켠다.
    ///
    /// 2단계 8일 차: 로봇 셀(OPC UA)을 같은 화면에 합쳤다 — 상단 "로봇 OPC UA" 칩, 오른쪽 "로봇 셀" 카드(상태·사이클·팔레트·IO·관절 6개),
    /// 가운데 배너. 알람은 컨베이어와 한 목록이다 (RobotTwin이 ConveyorTwinController.AppendConditions로 더함).
    /// 카드에는 원시 신호만 보이고, "흡착 실패" 같은 판단은 알람 목록이 보인다.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TwinHmi : MonoBehaviour
    {
        [SerializeField] ConveyorTwinController twin;
        [SerializeField] ServerLauncher launcher;
        [SerializeField] TwinHud diagnosticHud;
        /// <summary>로봇 미러 (로봇 glb가 없어 셀이 없으면 null → 로봇 카드·칩을 숨김)</summary>
        [SerializeField] RobotTwin robot;

        /// <summary>한글 글리프 폰트 (OS 맑은 고딕을 동적으로 쓰는 에셋, Setup Scene이 만들어 지정)</summary>
        [SerializeField] FontAsset koreanFont;

        const float RefreshS = 0.25f;           // 화면 갱신 주기
        const int TrendSamples = 240;           // 60초 / 0.25초
        const int MaxAlarmRows = 5;   // 로봇 카드가 들어가며 9 → 5 (오른쪽 열 높이)
        /// <summary>관절이 가동 범위 끝에서 이만큼 안쪽이면 막대를 경고색으로</summary>
        const double JointNearLimitDeg = 10.0;
        const double ThroughputWindowS = 60.0;

        // ---- 표시 범위와 정상 운전 범위 (docs/protocol.md 7절) ----
        static readonly Range SpeedRange = new Range(0f, 1.0f, 0.45f, 0.55f);
        static readonly Range TempRange = new Range(0f, 100f, 25f, 60f);
        static readonly Range CurrentRange = new Range(0f, 5f, 1.6f, 2.6f);

        readonly struct Range
        {
            public readonly float Min, Max, NormalLo, NormalHi;
            public Range(float min, float max, float lo, float hi) { Min = min; Max = max; NormalLo = lo; NormalHi = hi; }
        }

        sealed class Kpi
        {
            public VisualElement Card, Band, Fill;
            public Label Value, Meta;
            public Sparkline Trend;
            public Range Range;
        }

        VisualElement _root;
        Kpi _speed, _temp, _current;
        Label _clock, _runState, _runReason, _brokerChip, _deviceChip, _serverChip, _dataChip, _bannerText;
        VisualElement _runCard, _chipBroker, _chipDevice, _chipServer, _chipData, _banner;
        Label _onBelt, _throughput, _inOut, _weight, _statNote, _alarmCounts, _alarmEmpty;
        VisualElement _alarmList;
        Button _btnAck, _btnDiag, _btnLaunch, _btnDialogLaunch, _btnDialogLater;
        VisualElement _dialog;
        Label _dialogBody;

        // ---- 로봇 셀 ----
        VisualElement _robotCard, _chipRobot;
        Label _robotChip, _robotState, _robotMeta, _robotCycles, _robotPallet, _robotExitWait;
        Label _ioVacuumOn, _ioVacuumOk, _ioMotors, _ioPallet;
        readonly VisualElement[] _jointRows = new VisualElement[6];
        readonly VisualElement[] _jointFills = new VisualElement[6];
        readonly Label[] _jointValues = new Label[6];

        // ---- 서버 연결 불가 다이얼로그 ----
        /// <summary>브로커가 이만큼 끊겨 있고 서버 프로세스도 없으면 묻는다</summary>
        const float AskAfterS = 5f;
        /// <summary>"나중에"를 누르면 이만큼 다시 묻지 않는다</summary>
        const float SnoozeS = 60f;
        /// <summary>실행 직후 서버가 뜨고 접속하기까지 기다리는 시간</summary>
        const float LaunchGraceS = 8f;
        float _brokerLostSince = -1f;
        float _snoozeUntil;
        float _nextProcessCheck;
        bool _serverProcessRunning = true;

        float _nextRefresh;
        double _rate = 1.0;
        readonly Queue<KeyValuePair<double, long>> _exits = new Queue<KeyValuePair<double, long>>();

        void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            ApplyKoreanFont();

            _speed = BindKpi("kpi-speed", SpeedRange);
            _temp = BindKpi("kpi-temp", TempRange);
            _current = BindKpi("kpi-current", CurrentRange);

            _clock = Q<Label>("clock");
            _runCard = Q<VisualElement>("card-run");
            _runState = Q<Label>("run-state");
            _runReason = Q<Label>("run-reason");
            _chipBroker = Q<VisualElement>("chip-broker"); _brokerChip = Q<Label>("chip-broker-value");
            _chipDevice = Q<VisualElement>("chip-device"); _deviceChip = Q<Label>("chip-device-value");
            _chipServer = Q<VisualElement>("chip-server"); _serverChip = Q<Label>("chip-server-value");
            _chipData = Q<VisualElement>("chip-data"); _dataChip = Q<Label>("chip-data-value");
            _banner = Q<VisualElement>("banner"); _bannerText = Q<Label>("banner-text");
            _onBelt = Q<Label>("stat-onbelt");
            _throughput = Q<Label>("stat-throughput");
            _inOut = Q<Label>("stat-inout");
            _weight = Q<Label>("stat-weight");
            _statNote = Q<Label>("stat-note");
            _alarmCounts = Q<Label>("alarm-counts");
            _alarmList = Q<VisualElement>("alarm-list");
            _alarmEmpty = Q<Label>("alarm-empty");
            _btnAck = Q<Button>("btn-ack");
            _btnDiag = Q<Button>("btn-diag");

            _btnLaunch = Q<Button>("btn-launch");
            _dialog = Q<VisualElement>("dialog-overlay");
            _dialogBody = Q<Label>("dialog-body");
            _btnDialogLaunch = Q<Button>("btn-dialog-launch");
            _btnDialogLater = Q<Button>("btn-dialog-later");

            _btnAck.clicked += () => twin.Alarms.AcknowledgeAll();
            _btnDiag.clicked += ToggleDiagnostics;
            _btnLaunch.clicked += LaunchServer;
            _btnDialogLaunch.clicked += LaunchServer;
            _btnDialogLater.clicked += () =>
            {
                _snoozeUntil = Time.unscaledTime + SnoozeS;
                _dialog.AddToClassList("hidden");
            };
            if (diagnosticHud != null) diagnosticHud.enabled = false;
            BindRobot();
        }

        void BindRobot()
        {
            _robotCard = Q<VisualElement>("card-robot");
            _chipRobot = Q<VisualElement>("chip-robot");
            _robotChip = Q<Label>("chip-robot-value");
            _robotState = Q<Label>("robot-state");
            _robotMeta = Q<Label>("robot-meta");
            _robotCycles = Q<Label>("robot-cycles");
            _robotPallet = Q<Label>("robot-pallet");
            _robotExitWait = Q<Label>("robot-exitwait");
            _ioVacuumOn = Q<Label>("io-vacuum-on");
            _ioVacuumOk = Q<Label>("io-vacuum-ok");
            _ioMotors = Q<Label>("io-motors");
            _ioPallet = Q<Label>("io-pallet");
            _robotCard.style.display = robot != null ? DisplayStyle.Flex : DisplayStyle.None;
            _chipRobot.style.display = robot != null ? DisplayStyle.Flex : DisplayStyle.None;

            // 관절 6개: 가동 범위(RobotKinematics) 안의 위치를 막대로, 0° 표시선
            var list = Q<VisualElement>("joint-list");
            list.Clear();
            for (int i = 0; i < 6; i++)
            {
                var row = new VisualElement();
                row.AddToClassList("joint-row");
                var name = new Label("J" + (i + 1));
                name.AddToClassList("joint-name");
                var bar = new VisualElement();
                bar.AddToClassList("joint-bar");
                var zero = new VisualElement();
                zero.AddToClassList("joint-zero");
                zero.style.left = Length.Percent((float)JointPercent(i, 0));
                var fill = new VisualElement();
                fill.AddToClassList("joint-fill");
                bar.Add(zero);
                bar.Add(fill);
                var value = new Label("-");
                value.AddToClassList("joint-value");
                row.Add(name);
                row.Add(bar);
                row.Add(value);
                list.Add(row);
                _jointRows[i] = row;
                _jointFills[i] = fill;
                _jointValues[i] = value;
            }
        }

        static double JointPercent(int i, double deg) =>
            (deg - RobotKinematics.MinDeg[i]) / (RobotKinematics.MaxDeg[i] - RobotKinematics.MinDeg[i]) * 100.0;

        /// <summary>
        /// 플레이 모드를 끝낼 때: 패널의 폰트 지정을 기본값으로 되돌린다.
        /// 종료 직전 패널이 한 번 더 그리는데, 그 사이 Unity의 미사용 에셋 정리가 런타임 폰트를 해제하면
        /// UI Toolkit 텍스트 생성(MeshGenerator.DrawText)에서 NullReferenceException이 났다.
        /// </summary>
        void OnDisable()
        {
            if (_root != null)
                _root.style.unityFontDefinition = StyleKeyword.Null;
        }

        /// <remarks>
        /// 폰트는 Setup Scene이 에디터에서 만든 에셋(koreanFont, OS 맑은 고딕을 동적으로 쓰는 DynamicOS 모드)을 쓴다.
        /// 런타임에 FontAsset.CreateFontAsset으로 만들면 플레이 모드 종료 때 글리프 아틀라스가 정리되면서
        /// 남은 폰트를 참조하는 UI Toolkit 텍스트 생성에서 예외가 났다.
        /// </remarks>
        void ApplyKoreanFont()
        {
            if (koreanFont == null)
            {
                Debug.LogWarning("[Twin] HMI: 한글 폰트 에셋이 없습니다 (Tools/Twin/Setup Scene 다시 실행). 한글이 깨질 수 있습니다.");
                return;
            }
            _root.style.unityFontDefinition = new StyleFontDefinition(koreanFont);
        }

        T Q<T>(string name) where T : VisualElement
        {
            var e = _root.Q<T>(name);
            if (e == null) throw new InvalidOperationException($"HMI 요소 '{name}'가 UXML에 없습니다");
            return e;
        }

        Kpi BindKpi(string id, Range range)
        {
            var k = new Kpi
            {
                Card = Q<VisualElement>(id),
                Value = Q<Label>(id + "-value"),
                Meta = Q<Label>(id + "-meta"),
                Range = range,
            };
            var bar = Q<VisualElement>(id + "-bar");
            k.Band = bar.Q(className: "bar-band");
            k.Fill = bar.Q(className: "bar-fill");
            float lo = Mathf.InverseLerp(range.Min, range.Max, range.NormalLo) * 100f;
            float hi = Mathf.InverseLerp(range.Min, range.Max, range.NormalHi) * 100f;
            k.Band.style.left = Length.Percent(lo);
            k.Band.style.width = Length.Percent(hi - lo);
            k.Trend = new Sparkline(TrendSamples, range.Min, range.Max, range.NormalLo, range.NormalHi);
            Q<VisualElement>(id + "-trend").Add(k.Trend);
            return k;
        }

        void ToggleDiagnostics()
        {
            if (diagnosticHud == null) return;
            diagnosticHud.enabled = !diagnosticHud.enabled;
            _btnDiag.EnableInClassList("active", diagnosticHud.enabled);
        }

        void Update()
        {
            if (twin == null || _root == null) return;
            _rate += (twin.PlaybackRate - _rate) * Mathf.Clamp01(Time.unscaledDeltaTime / 0.3f);
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshS;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool blind = !twin.BrokerConnected || twin.DeviceOnline != true;
            _clock.text = DateTime.Now.ToString("HH:mm:ss");

            UpdateChips(now);
            UpdateRunState(blind);
            UpdateKpi(_speed, twin.Speed, "F3", now, blind, SpeedSeverity());
            UpdateKpi(_temp, twin.Temp, "F1", now, blind, Severity(twin.Temp.Value, 60.0, ConveyorTwinController.TempHighC));
            UpdateKpi(_current, twin.Current, "F2", now, blind, Severity(twin.Current.Value, CurrentRange.NormalHi, ConveyorTwinController.CurrentHighA));
            UpdateItems();
            UpdateRobot();
            UpdateAlarms(now);
            UpdateBanner(now);
            UpdateServerRecovery();
        }

        // ---------- 서버 연결 불가 시 운전자에게 묻기 ----------
        void UpdateServerRecovery()
        {
            if (launcher == null) return;
            float t = Time.unscaledTime;
            if (twin.BrokerConnected)
            {
                _brokerLostSince = -1f;
                _serverProcessRunning = true;
                _btnLaunch.AddToClassList("hidden");
                _dialog.AddToClassList("hidden");
                return;
            }

            if (_brokerLostSince < 0f) _brokerLostSince = t;
            if (t >= _nextProcessCheck)
            {
                _nextProcessCheck = t + 1f;   // 프로세스 목록 조회는 1초에 한 번만
                _serverProcessRunning = ServerLauncher.IsServerProcessRunning();
            }

            bool launching = t - launcher.LastLaunchTime < LaunchGraceS;
            bool canLaunch = !_serverProcessRunning && !launching;
            _btnLaunch.EnableInClassList("hidden", !canLaunch);

            bool ask = canLaunch && t - _brokerLostSince > AskAfterS && t > _snoozeUntil;
            _dialog.EnableInClassList("hidden", !ask);
            if (ask)
            {
                _dialogBody.text =
                    $"브로커(127.0.0.1:1883)와 {t - _brokerLostSince:F0}초째 연결되지 않고, 더미 서버 프로세스도 보이지 않습니다.\n" +
                    "서버를 다시 실행할까요? 새 세션으로 시작하므로 물건 추적은 다시 맞춰집니다." +
                    (launcher.Error != null && launcher.State != ServerLauncher.LaunchState.Launched ? $"\n\n마지막 오류: {launcher.Error}" : "");
            }

            // 서버 칩: 프로세스는 있는데 응답이 없는 경우를 구분
            if (_serverProcessRunning && !launching)
                SetChip(_chipServer, _serverChip, "응답 없음 · 서버 창 확인", 2);
            else if (launching)
                SetChip(_chipServer, _serverChip, "실행 중… 접속 대기", 1);
            else
                SetChip(_chipServer, _serverChip, "꺼짐", 2);
        }

        void LaunchServer()
        {
            _dialog.AddToClassList("hidden");
            if (!launcher.TryLaunch())
                _snoozeUntil = Time.unscaledTime + 3f;   // 실패 사유를 다이얼로그에 다시 보여 줌
        }

        // ---------- 상단 연결 상태 ----------
        void UpdateChips(long now)
        {
            if (twin.BrokerConnected) SetChip(_chipBroker, _brokerChip, "연결", 0);
            else SetChip(_chipBroker, _brokerChip, "끊김 · 재접속 중", 2);

            switch (twin.DeviceOnline)
            {
                case true: SetChip(_chipDevice, _deviceChip, $"online · {twin.Sid}", 0); break;
                case false: SetChip(_chipDevice, _deviceChip, $"offline {(now - twin.OfflineSinceMs) / 1000}s", 2); break;
                default: SetChip(_chipDevice, _deviceChip, "-", 1); break;
            }

            if (launcher == null) SetChip(_chipServer, _serverChip, "-", 0);
            else switch (launcher.State)
            {
                case ServerLauncher.LaunchState.Checking: SetChip(_chipServer, _serverChip, "확인 중", 1); break;
                case ServerLauncher.LaunchState.AlreadyRunning: SetChip(_chipServer, _serverChip, "실행 중 (외부)", 0); break;
                case ServerLauncher.LaunchState.Launched: SetChip(_chipServer, _serverChip, $"자동 실행 (pid {launcher.ProcessId})", 0); break;
                case ServerLauncher.LaunchState.NotFound: SetChip(_chipServer, _serverChip, "실행 파일 없음", twin.BrokerConnected ? 0 : 2); break;
                case ServerLauncher.LaunchState.Failed: SetChip(_chipServer, _serverChip, "실행 실패", 2); break;
                default: SetChip(_chipServer, _serverChip, "자동 실행 꺼짐", 0); break;
            }

            if (robot != null)
            {
                switch (robot.LinkState)
                {
                    case RobotLinkState.Connected: SetChip(_chipRobot, _robotChip, $"Basic256Sha256 · {Math.Max(0, robot.LastJointLatencyMs)}ms", 0); break;
                    case RobotLinkState.Provisioning: SetChip(_chipRobot, _robotChip, "인증서 발급 중", 1); break;
                    case RobotLinkState.Failed: SetChip(_chipRobot, _robotChip, "연결 불가 · 인증서", 2); break;
                    case RobotLinkState.Reconnecting: SetChip(_chipRobot, _robotChip, "끊김 · 재접속 중", 2); break;
                    default: SetChip(_chipRobot, _robotChip, "접속 중", 1); break;
                }
            }

            long delay = twin.ScreenDelayMs;
            if (!twin.BrokerConnected || twin.DeviceOnline != true) SetChip(_chipData, _dataChip, "수신 없음", 2);
            else if (_rate > 1.1) SetChip(_chipData, _dataChip, $"따라잡는 중 ×{_rate:F1}", 1);
            else if (delay > 1000) SetChip(_chipData, _dataChip, $"{delay / 1000.0:F1}초 전 상태", 1);
            else SetChip(_chipData, _dataChip, $"실시간 · 지연 {Math.Max(0, twin.LastLatencyMs)}ms", 0);
        }

        static void SetChip(VisualElement chip, Label value, string text, int level)
        {
            value.text = text;
            chip.EnableInClassList("ok", level == 0);
            chip.EnableInClassList("warn", level == 1);
            chip.EnableInClassList("bad", level == 2);
        }

        // ---------- 운전 상태 ----------
        void UpdateRunState(bool blind)
        {
            string text = "-";
            bool fault = false;
            switch (twin.RunState)
            {
                case RunState.Running: text = "운전 중"; break;
                case RunState.Stopped: text = "정지"; break;
                case RunState.Fault: text = "고장 정지"; fault = true; break;
            }
            if (blind) text += " (확인 불가)";
            _runState.text = text;
            _runReason.text = fault ? twin.RunReason ?? "" : blind ? "마지막으로 받은 상태" : " ";
            _runCard.EnableInClassList("fault", fault);
            _runCard.EnableInClassList("unknown", blind || twin.RunState == null);
        }

        // ---------- 측정값 ----------
        int SpeedSeverity()
        {
            if (twin.RunState == RunState.Fault) return 2;
            if (!twin.Speed.HasValue) return 0;
            double v = twin.Speed.Value;
            return v < SpeedRange.NormalLo || v > SpeedRange.NormalHi ? 1 : 0;
        }

        static int Severity(double v, double warn, double bad) => v > bad ? 2 : v > warn ? 1 : 0;

        void UpdateKpi(Kpi k, SignalValue s, string format, long now, bool blind, int severity)
        {
            bool stale = s.IsStale(now);
            bool rejected = now - s.LastRejectMs < ConveyorTwinController.RejectAlarmHoldMs;
            if (rejected) severity = Math.Max(severity, 1);

            k.Value.text = s.HasValue ? s.Value.ToString(format) : "-";
            k.Meta.text =
                rejected ? $"이상값 거부: {s.LastRejectReason}" :
                stale ? $"stale · {(s.ArrivedMs > 0 ? (now - s.ArrivedMs) / 1000.0 : 0):F1}s 무수신" :
                blind ? "마지막 수신값" :
                s.HasValue ? $"{(now - s.ReceivedMs) / 1000.0:F1}s 전" : "-";

            k.Card.EnableInClassList("warn", severity == 1);
            k.Card.EnableInClassList("bad", severity == 2);
            k.Card.EnableInClassList("stale", stale || blind);

            float pos = s.HasValue ? Mathf.InverseLerp(k.Range.Min, k.Range.Max, (float)s.Value) * 100f : 0f;
            k.Fill.style.left = Length.Percent(Mathf.Clamp(pos, 0f, 99f));
            // 추세는 믿을 수 있는 값만 잇는다 (끊긴 구간은 비워 둠)
            k.Trend.Push(s.HasValue && !stale && !blind ? (float)s.Value : float.NaN);
        }

        // ---------- 물건 추적 ----------
        void UpdateItems()
        {
            var tr = twin.Tracker;
            if (tr == null) return;
            double t = Time.unscaledTimeAsDouble;
            _exits.Enqueue(new KeyValuePair<double, long>(t, tr.Exited));
            while (_exits.Count > 1 && t - _exits.Peek().Key > ThroughputWindowS) _exits.Dequeue();
            var oldest = _exits.Peek();
            double span = t - oldest.Key;
            _onBelt.text = tr.Items.Count.ToString();
            _throughput.text = span > 10.0 ? ((tr.Exited - oldest.Value) / span * 60.0).ToString("F1") : "-";
            _inOut.text = $"{tr.Entered} / {tr.Exited}";
            _weight.text = twin.Weight.HasValue ? $"{twin.Weight.Value:F1} kg" : "-";
            int jams = twin.JammedCount;
            _statNote.text =
                jams > 0 ? $"잼: 출구 미감지 {jams}개" :
                tr.Resyncing ? "재동기화 중: 트윈이 보기 전에 올라온 물건은 추적하지 않습니다" :
                $"출구 위치 보정 {tr.LastCorrectionM * 1000:F0} mm";
        }

        // ---------- 로봇 셀 ----------
        void UpdateRobot()
        {
            if (robot == null) return;
            bool connected = robot.LinkState == RobotLinkState.Connected;

            // 상태 한 줄: 원시 신호를 그대로 읽은 것 (정지 원인 판단은 알람 목록)
            string state;
            int level = 0;
            if (!connected) { state = "확인 불가 · 마지막 수신 자세"; level = 2; }
            else if (robot.EmergencyStop) { state = "비상정지"; level = 2; }
            else if (robot.ProtectiveStop) { state = "보호정지"; level = 2; }
            else if (robot.VacuumFault) { state = "흡착 실패 · 픽 위치 대기"; level = 2; }
            else if (!robot.MotorsOn) { state = "모터 OFF"; level = 1; }
            else if (robot.Execution == ProgramExecutionState.Stopped) { state = "프로그램 정지"; level = 1; }
            else state = "자동 운전 중";
            _robotState.text = state;
            _robotMeta.text = $"{ModeText(robot.Mode)} · 속도 {robot.SpeedOverride:F0}%";
            _robotCard.EnableInClassList("warn", level == 1);
            _robotCard.EnableInClassList("bad", level == 2);
            _robotCard.EnableInClassList("stale", !connected || robot.JointDataBad);

            _robotCycles.text = robot.CycleCount.ToString();
            _robotPallet.text = robot.PalletPresent ? $"{robot.BoxesOnPallet} / {RobotCell.SlotCount}" : "교체 중";
            double wait = robot.ExitWaitS;
            _robotExitWait.text = wait > 0 ? $"{wait:F1}s" : "-";

            SetIo(_ioVacuumOn, robot.VacuumOn, false);
            SetIo(_ioVacuumOk, robot.VacuumOk, robot.VacuumFault);   // 흡착 실패는 압력 스위치 쪽에 표시
            SetIo(_ioMotors, robot.MotorsOn, connected && robot.EmergencyStop);
            SetIo(_ioPallet, robot.PalletPresent, false);

            for (int i = 0; i < 6; i++)
            {
                double q = robot.JointsDeg[i];
                _jointFills[i].style.left = Length.Percent(Mathf.Clamp((float)JointPercent(i, q), 0f, 99f));
                _jointValues[i].text = $"{q:F1}°";
                bool near = q - RobotKinematics.MinDeg[i] < JointNearLimitDeg || RobotKinematics.MaxDeg[i] - q < JointNearLimitDeg;
                _jointRows[i].EnableInClassList("warn", near);
            }
        }

        static void SetIo(Label io, bool on, bool bad)
        {
            io.EnableInClassList("on", on && !bad);
            io.EnableInClassList("bad", bad);
        }

        static string ModeText(OperationalMode m)
        {
            switch (m)
            {
                case OperationalMode.Automatic: return "자동";
                case OperationalMode.AutomaticExternal: return "자동(외부)";
                case OperationalMode.ManualReducedSpeed: return "수동 감속";
                case OperationalMode.ManualHighSpeed: return "수동 고속";
                default: return "-";
            }
        }

        // ---------- 알람 ----------
        void UpdateAlarms(long now)
        {
            var alarms = twin.Alarms;
            int unacked = alarms.UnacknowledgedCount;
            _alarmCounts.text = alarms.ActiveCount == 0 && unacked == 0 ? "없음" : $"활성 {alarms.ActiveCount} · 미확인 {unacked}";
            _btnAck.EnableInClassList("hidden", unacked == 0);

            _alarmList.Clear();
            bool blink = (now / 500) % 2 == 0;
            int rows = 0;
            foreach (var a in alarms.History)
            {
                if (!a.Active && a.Acknowledged) continue;
                if (++rows > MaxAlarmRows) break;
                _alarmList.Add(AlarmRow(a, now, blink));
            }
            _alarmEmpty.EnableInClassList("hidden", rows > 0);
        }

        static VisualElement AlarmRow(Alarm a, long now, bool blink)
        {
            var row = new VisualElement();
            row.AddToClassList("alarm-row");
            row.AddToClassList(a.Severity == AlarmSeverity.Critical ? "critical" : a.Severity == AlarmSeverity.Warning ? "warning" : "info");
            if (!a.Active) row.AddToClassList("cleared");
            else if (!a.Acknowledged && blink) row.AddToClassList("blink");

            var tag = new Label(a.Severity == AlarmSeverity.Critical ? "중대" : a.Severity == AlarmSeverity.Warning ? "경고" : "안내");
            tag.AddToClassList("alarm-tag");
            var body = new VisualElement();
            body.AddToClassList("alarm-body");
            var text = new Label(a.Text);
            text.AddToClassList("alarm-text");
            long dur = (a.Active ? now : a.ClearedMs) - a.RaisedMs;
            string state = a.Active ? (a.Acknowledged ? "확인됨" : "미확인") : "해소 · 미확인";
            var meta = new Label($"{DateTimeOffset.FromUnixTimeMilliseconds(a.RaisedMs).LocalDateTime:HH:mm:ss} · {dur / 1000}s · {state}");
            meta.AddToClassList("alarm-meta");
            body.Add(text);
            body.Add(meta);
            row.Add(tag);
            row.Add(body);
            return row;
        }

        // ---------- 가운데 배너: 화면을 믿을 수 있는지 ----------
        void UpdateBanner(long now)
        {
            string text = null;
            string level = null;
            if (!twin.BrokerConnected) { text = "브로커 연결 끊김 · 재접속 시도 중 (화면은 마지막 수신 상태)"; level = "bad"; }
            else if (twin.DeviceOnline == false)
            {
                var since = DateTimeOffset.FromUnixTimeMilliseconds(twin.OfflineSinceMs).LocalDateTime;
                text = $"설비 오프라인 · {since:HH:mm:ss}부터 수신 없음 (화면은 마지막 수신 상태)";
                level = "bad";
            }
            else if (twin.JammedCount > 0) { text = $"잼 감지 · 출구 미감지 물건 {twin.JammedCount}개"; level = "bad"; }
            else if (twin.RunState == RunState.Fault) { text = $"모터 고장 정지 · {twin.RunReason}"; level = "bad"; }
            // 로봇 셀 (2단계): 컨베이어 문제가 없을 때 로봇 상태를 알린다
            else if (robot != null && robot.LinkState == RobotLinkState.Reconnecting) { text = "로봇 OPC UA 통신 끊김 · 로봇은 마지막 수신 자세 (회색)"; level = "bad"; }
            else if (robot != null && robot.EmergencyStop) { text = "로봇 비상정지 · 모터 전원 차단"; level = "bad"; }
            else if (robot != null && robot.VacuumFault) { text = "로봇 흡착 실패 · 진공 출력 ON, 압력 스위치 OFF"; level = "bad"; }
            else if (robot != null && robot.JointDataBad) { text = "로봇 관절각 측정 이상 (StatusCode Bad) · 마지막 정상 자세"; level = "warn"; }
            else if (_rate > 1.1) { text = $"▶▶ 밀린 구간을 빨리 재생 중 ×{_rate:F1} (화면 속도가 실제보다 빠름)"; level = "info"; }
            else if (twin.ScreenDelayMs > 1000) { text = $"화면은 {twin.ScreenDelayMs / 1000.0:F1}초 전 상태 · 전송 지연"; level = "warn"; }

            _banner.EnableInClassList("hidden", text == null);
            _banner.EnableInClassList("bad", level == "bad");
            _banner.EnableInClassList("warn", level == "warn");
            _banner.EnableInClassList("info", level == "info");
            if (text != null) _bannerText.text = text;
        }
    }
}
