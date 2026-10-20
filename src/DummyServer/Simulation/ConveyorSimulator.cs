using System.Diagnostics;
using ConveyorTwin.DummyServer.Mqtt;
using ConveyorTwin.Shared;

namespace ConveyorTwin.DummyServer.Simulation;

/// <summary>UI 표시용 현재 상태 스냅샷</summary>
public sealed record ConveyorSnapshot(
    RunState State, double SpeedMps, double MotorTempC, double MotorCurrentA,
    int ItemsOnBelt, long ItemsDelivered);

/// <summary>
/// 가상 컨베이어 ("현실" 역할). 물리 상태를 진행시키고 센서가 볼 원시 신호만 발행한다.
/// 잼 같은 판단은 보내지 않는다 (트윈의 몫, CLAUDE.md 설계 원칙 2).
///
/// 물건 추적: 각 물건의 앞단 위치(입구 센서 = 0m)를 벨트 속도로 적분하고,
/// 앞단·뒷단이 센서 위치를 지나는 순간 엣지를 발행한다. 속도가 0이 되면 물건도 멈춘다.
/// </summary>
public sealed class ConveyorSimulator
{
    // ---- 정상 운전 기준값 (docs/protocol.md 7절: 서버 구현 시 조정) ----
    const double NominalSpeedMps = 0.5;
    const double SpeedNoise = 0.004;
    const double NoLoadCurrentA = 1.8;      // 0.37kW 감속 모터 무부하 전류 (가정)
    const double CurrentPerKgA = 0.02;      // 벨트 위 하중 1kg당 전류 증가 (가정)
    const double CurrentNoise = 0.03;
    const double AmbientTempC = 25.0;
    const double TempTimeConstantS = 90.0;  // 모터 온도 1차 지연 시정수
    const double TempNoise = 0.05;

    // ---- 물건 (박스) ----
    const double ItemLengthM = 0.30;
    const double MinGapM = 0.40;            // 앞 물건 뒷단과의 최소 간격
    const double SpawnIntervalMinS = 2.5;
    const double SpawnIntervalMaxS = 5.0;
    const double ItemWeightMinKg = 2.0;
    const double ItemWeightMaxKg = 15.0;

    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    sealed class Item
    {
        public double FrontM;       // 앞단 위치 (입구 센서 = 0)
        public double WeightKg;
        public bool EntryCleared;   // 뒷단이 입구 센서를 지남
        public bool ExitBlocked;    // 앞단이 출구 센서에 도달
    }

    readonly DevicePublisher _pub;
    readonly string _deviceId;
    readonly Random _rng = new();
    readonly List<Item> _items = new();

    RunState _state = RunState.Running;
    double _speed;
    double _temp = AmbientTempC;
    double _current;
    double _nextSpawnS;
    long _delivered;

    public event Action<ConveyorSnapshot>? Updated;

    public ConveyorSimulator(DevicePublisher publisher, string deviceId = Topics.DefaultDeviceId)
    {
        _pub = publisher;
        _deviceId = deviceId;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _pub.PublishRunStateAsync(_state, null, ct);

        var clock = Stopwatch.StartNew();
        double last = 0, nextSpeedPub = 0, nextCurrentPub = 0, nextTempPub = 0;
        _nextSpawnS = 1.0;

        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(ct))
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - last;
            last = now;

            Step(dt);
            await MoveItemsAsync(dt, ct);
            if (now >= _nextSpawnS)
                await TrySpawnAsync(now, ct);

            // 주기 측정값: 주기는 SignalSpec이 기준 (트윈의 stale 판정과 같은 값)
            if (now >= nextSpeedPub)
            {
                nextSpeedPub = now + Signals.BeltSpeed.PeriodMs / 1000.0;
                await _pub.PublishTelemetryAsync(Signals.BeltSpeed, Topics.BeltSpeed(_deviceId), _speed, Quality.Good, ct);
            }
            if (now >= nextCurrentPub)
            {
                nextCurrentPub = now + Signals.MotorCurrent.PeriodMs / 1000.0;
                await _pub.PublishTelemetryAsync(Signals.MotorCurrent, Topics.MotorCurrent(_deviceId), _current, Quality.Good, ct);
            }
            if (now >= nextTempPub)
            {
                nextTempPub = now + Signals.MotorTemp.PeriodMs / 1000.0;
                await _pub.PublishTelemetryAsync(Signals.MotorTemp, Topics.MotorTemp(_deviceId), _temp, Quality.Good, ct);
            }

            Updated?.Invoke(new ConveyorSnapshot(_state, _speed, _temp, _current, _items.Count, _delivered));
        }
    }

    /// <summary>모터·벨트 물리량 갱신</summary>
    void Step(double dt)
    {
        bool running = _state == RunState.Running;
        _speed = running ? NominalSpeedMps + Noise(SpeedNoise) : 0.0;

        double loadKg = _items.Sum(i => i.WeightKg);
        _current = running ? NoLoadCurrentA + loadKg * CurrentPerKgA + Noise(CurrentNoise) : 0.0;

        // 정상 상태 온도는 전류에 비례해 오르고, 1차 지연으로 따라간다
        double targetTemp = AmbientTempC + (running ? 12.0 * _current : 0.0);
        _temp += (targetTemp - _temp) * (dt / TempTimeConstantS) + Noise(TempNoise) * dt;
    }

    async Task MoveItemsAsync(double dt, CancellationToken ct)
    {
        double move = _speed * dt;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            item.FrontM += move;

            if (!item.EntryCleared && item.FrontM >= ItemLengthM)
            {
                item.EntryCleared = true;
                await _pub.PublishEdgeAsync(Topics.EntrySensor(_deviceId), false, ct);
            }
            if (!item.ExitBlocked && item.FrontM >= Equipment.BeltLengthM)
            {
                item.ExitBlocked = true;
                await _pub.PublishEdgeAsync(Topics.ExitSensor(_deviceId), true, ct);
            }
            if (item.FrontM - ItemLengthM >= Equipment.BeltLengthM)
            {
                await _pub.PublishEdgeAsync(Topics.ExitSensor(_deviceId), false, ct);
                _items.RemoveAt(i--);
                _delivered++;
            }
        }
    }

    /// <summary>입구에 물건 투입: 입구 센서 감지 + 입구 로드셀 무게</summary>
    async Task TrySpawnAsync(double now, CancellationToken ct)
    {
        if (_state != RunState.Running)
            return;
        var last = _items.Count > 0 ? _items[^1] : null;
        if (last != null && last.FrontM - ItemLengthM < MinGapM)
            return; // 앞 물건과 간격 확보 전까지 대기

        var item = new Item { WeightKg = ItemWeightMinKg + _rng.NextDouble() * (ItemWeightMaxKg - ItemWeightMinKg) };
        _items.Add(item);
        _nextSpawnS = now + SpawnIntervalMinS + _rng.NextDouble() * (SpawnIntervalMaxS - SpawnIntervalMinS);

        await _pub.PublishEdgeAsync(Topics.EntrySensor(_deviceId), true, ct);
        await _pub.PublishTelemetryAsync(Signals.LoadWeight, Topics.LoadWeight(_deviceId), item.WeightKg, Quality.Good, ct);
    }

    double Noise(double sigma)
    {
        // Box-Muller 정규 분포
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
