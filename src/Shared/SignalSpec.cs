namespace ConveyorTwin.Shared
{
    /// <summary>
    /// 신호별 사양. 서버는 발행 주기와 정상 범위로 값을 생성하고,
    /// 트윈은 같은 사양으로 이상값과 데이터 끊김(stale)을 판정한다.
    /// </summary>
    public sealed class SignalSpec
    {
        public string Name { get; }
        public string Unit { get; }
        public int PeriodMs { get; }      // 0 = 이벤트성 (주기 없음)
        public double ValidMin { get; }   // 센서 측정 가능 범위 하한
        public double ValidMax { get; }   // 센서 측정 가능 범위 상한
        public int Qos { get; }
        public bool Retained { get; }

        public SignalSpec(string name, string unit, int periodMs, double validMin, double validMax, int qos, bool retained)
        {
            Name = name;
            Unit = unit;
            PeriodMs = periodMs;
            ValidMin = validMin;
            ValidMax = validMax;
            Qos = qos;
            Retained = retained;
        }

        public bool IsInRange(double value) => value >= ValidMin && value <= ValidMax;

        /// <summary>마지막 수신 후 이 시간이 지나면 stale로 판정 (주기의 3배)</summary>
        public int StaleAfterMs => PeriodMs > 0 ? PeriodMs * 3 : 0;
    }

    public static class Signals
    {
        public static readonly SignalSpec BeltSpeed    = new SignalSpec("belt_speed",    "m/s",  500,  0.0,   2.0, 0, true);
        public static readonly SignalSpec MotorTemp    = new SignalSpec("motor_temp",    "degC", 1000, -20.0, 150.0, 0, true);
        public static readonly SignalSpec MotorCurrent = new SignalSpec("motor_current", "A",    500,  0.0,  10.0, 0, true);
        public static readonly SignalSpec LoadWeight   = new SignalSpec("load_weight",   "kg",   0,    0.0,  50.0, 1, false);
    }
}
