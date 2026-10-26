using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ConveyorTwin.DummyServer.Mqtt;
using ConveyorTwin.DummyServer.OpcUa;
using ConveyorTwin.DummyServer.Simulation;
using ConveyorTwin.Shared;

namespace ConveyorTwin.DummyServer;

/// <summary>
/// 서버 화면 상태. 브로커·발행·시뮬레이션은 백그라운드에서 돌고,
/// 이벤트를 큐에 쌓았다가 UI 스레드에서 100ms마다 한꺼번에 반영한다 (초당 수십 건이라 건별 Dispatcher 호출을 피함).
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    const int MaxLogRows = 1000;

    readonly EmbeddedBroker _broker = new();
    readonly DevicePublisher _publisher = new();
    readonly RobotOpcUaServer _opcua = new();
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentQueue<SentMessage> _pending = new();
    readonly DispatcherTimer _uiTimer;
    Task? _simTask;
    Task? _robotTask;

    volatile ConveyorSnapshot? _snapshot;
    volatile RobotStatus? _robot;
    volatile IReadOnlyCollection<string> _clients = Array.Empty<string>();
    volatile IReadOnlyCollection<string> _opcSessions = Array.Empty<string>();

    public ObservableCollection<SentMessage> Log { get; } = new();

    public string Sid => _publisher.Sid;
    public string BrokerEndpoint => $"{EmbeddedBroker.Host}:{EmbeddedBroker.Port}";

    string _brokerStatus = "시작 중";
    public string BrokerStatus { get => _brokerStatus; private set => Set(ref _brokerStatus, value); }

    public string OpcUaEndpoint => Shared.OpcUaEndpoint.Url;

    string _opcStatus = "시작 중";
    public string OpcUaStatus { get => _opcStatus; private set => Set(ref _opcStatus, value); }

    string _opcSessionsText = "-";
    public string OpcUaSessionsText { get => _opcSessionsText; private set => Set(ref _opcSessionsText, value); }

    string _clientsText = "-";
    public string ClientsText { get => _clientsText; private set => Set(ref _clientsText, value); }

    string _runState = "-";
    public string RunStateText { get => _runState; private set => Set(ref _runState, value); }

    string _speed = "-", _temp = "-", _current = "-", _items = "-";
    public string SpeedText { get => _speed; private set => Set(ref _speed, value); }
    public string TempText { get => _temp; private set => Set(ref _temp, value); }
    public string CurrentText { get => _current; private set => Set(ref _current, value); }
    public string ItemsText { get => _items; private set => Set(ref _items, value); }

    string _robotText = "-", _jointsText = "-";
    public string RobotText { get => _robotText; private set => Set(ref _robotText, value); }
    public string JointsText { get => _jointsText; private set => Set(ref _jointsText, value); }

    long _sentCount;
    public long SentCount { get => _sentCount; private set => Set(ref _sentCount, value); }

    bool _paused;
    /// <summary>로그 화면 갱신만 멈춘다 (발행은 계속)</summary>
    public bool Paused { get => _paused; set => Set(ref _paused, value); }

    // ---- 장애 버튼 (CLAUDE.md 설계 원칙 3: 장애는 현실 쪽에서 일어난다) ----
    /// <summary>전송 지연 장애의 지연 시간</summary>
    public const int FaultDelayMs = 3000;

    bool _faultDisconnect, _faultDelay, _faultShuffle, _faultAnomaly, _faultJam, _faultMotor;

    public bool FaultDisconnect
    {
        get => _faultDisconnect;
        set
        {
            if (!Set(ref _faultDisconnect, value)) return;
            if (value) _publisher.DropConnection();
            else _ = RunFaultActionAsync(() => _publisher.RestoreConnectionAsync(_cts.Token), "연결 복구");
        }
    }

    public bool FaultDelay
    {
        get => _faultDelay;
        set { if (Set(ref _faultDelay, value)) _publisher.DelayMs = value ? FaultDelayMs : 0; }
    }

    public bool FaultShuffle
    {
        get => _faultShuffle;
        set { if (Set(ref _faultShuffle, value)) _publisher.Shuffle = value; }
    }

    public bool FaultAnomaly
    {
        get => _faultAnomaly;
        set { if (Set(ref _faultAnomaly, value) && _sim != null) _sim.Anomaly = value; }
    }

    public bool FaultJam
    {
        get => _faultJam;
        set
        {
            if (!Set(ref _faultJam, value) || _sim == null) return;
            if (value) _sim.RequestJam();
            else _sim.ClearJam();
        }
    }

    public bool FaultMotor
    {
        get => _faultMotor;
        set { if (Set(ref _faultMotor, value) && _sim != null) _sim.MotorFault = value; }
    }

    ConveyorSimulator? _sim;

    public MainViewModel()
    {
        _publisher.Sent += m => _pending.Enqueue(m);
        _broker.ClientsChanged += c => _clients = c;
        _opcua.SessionsChanged += s => _opcSessions = s;
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FlushToUi(),
            Dispatcher.CurrentDispatcher);
    }

    public async Task StartAsync()
    {
        _uiTimer.Start();
        await _broker.StartAsync();
        BrokerStatus = "대기 중";
        await _publisher.ConnectAsync(_cts.Token);

        _sim = new ConveyorSimulator(_publisher);
        _sim.Updated += s => _snapshot = s;
        _simTask = Task.Run(() => _sim.RunAsync(_cts.Token));

        // 2단계: 로봇 OPC UA 서버 + 가상 로봇 컨트롤러 (출구에 멈춘 물건을 집어 팔레트에 적재)
        await _opcua.StartAsync();
        OpcUaStatus = "대기 중";
        var robot = new RobotController(_opcua, _sim);
        robot.Updated += r => _robot = r;
        _robotTask = Task.Run(() => robot.RunAsync(_cts.Token));
    }

    async Task RunFaultActionAsync(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception e) { BrokerStatus = $"{name} 실패: {e.GetBaseException().Message}"; }
    }

    public void ClearLog() => Log.Clear();

    void FlushToUi()
    {
        long added = 0;
        while (_pending.TryDequeue(out var m))
        {
            added++;
            if (Paused) continue;
            Log.Insert(0, m);
        }
        while (Log.Count > MaxLogRows)
            Log.RemoveAt(Log.Count - 1);
        SentCount += added;

        ClientsText = _clients.Count == 0 ? "-" : string.Join(", ", _clients);
        OpcUaSessionsText = _opcSessions.Count == 0 ? "-" : string.Join(", ", _opcSessions);

        if (_snapshot is { } s)
        {
            RunStateText = s.State.ToString().ToLowerInvariant();
            SpeedText = $"{s.SpeedMps:F3} m/s";
            TempText = $"{s.MotorTempC:F1} °C";
            CurrentText = $"{s.MotorCurrentA:F2} A";
            ItemsText = $"벨트 위 {s.ItemsOnBelt} / 로봇 인계 {s.ItemsDelivered}" + (s.Jammed ? " (걸림)" : "");
        }
        if (_robot is { } r)
        {
            RobotText = $"{r.Phase} · 사이클 {r.CycleCount} · 팔레트 {(r.PalletPresent ? $"{r.NextSlot}/{RobotCell.SlotCount}" : "교체 중")}" +
                        (r.LastCycleS > 0 ? $" · 직전 {r.LastCycleS:F1}s" : "");
            JointsText = string.Join(" ", r.JointsDeg.Select(q => q.ToString("F0").PadLeft(5)));
        }

        if (_simTask is { IsFaulted: true })
        {
            BrokerStatus = "오류: " + _simTask.Exception?.GetBaseException().Message;
            _simTask = null;
        }
        if (_robotTask is { IsFaulted: true })
        {
            OpcUaStatus = "오류: " + _robotTask.Exception?.GetBaseException().Message;
            _robotTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _uiTimer.Stop();
        _cts.Cancel();
        if (_simTask != null)
        {
            try { await _simTask; } catch (OperationCanceledException) { }
        }
        if (_robotTask != null)
        {
            try { await _robotTask; } catch (OperationCanceledException) { }
        }
        await _opcua.DisposeAsync();
        await _publisher.DisposeAsync();
        await _broker.DisposeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
