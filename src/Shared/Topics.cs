namespace ConveyorTwin.Shared
{
    /// <summary>
    /// MQTT 토픽 규약. 형식: dt/{deviceId}/{category}/{signal}
    /// 상세 규칙은 docs/protocol.md 참고.
    /// </summary>
    public static class Topics
    {
        public const string Root = "dt";
        public const string DefaultDeviceId = "conveyor01";

        // 연결 상태 (Last Will 대상, retained)
        public static string Status(string deviceId) => $"{Root}/{deviceId}/status";

        // 가동 상태 (retained)
        public static string RunState(string deviceId) => $"{Root}/{deviceId}/state/run";

        // 주기 측정값 (retained)
        public static string BeltSpeed(string deviceId) => $"{Root}/{deviceId}/telemetry/belt_speed";
        public static string MotorTemp(string deviceId) => $"{Root}/{deviceId}/telemetry/motor_temp";
        public static string MotorCurrent(string deviceId) => $"{Root}/{deviceId}/telemetry/motor_current";

        // 이벤트성 측정값 (not retained)
        public static string LoadWeight(string deviceId) => $"{Root}/{deviceId}/telemetry/load_weight";

        // 광전 센서 엣지 이벤트 (not retained)
        public static string EntrySensor(string deviceId) => $"{Root}/{deviceId}/sensor/entry";
        public static string ExitSensor(string deviceId) => $"{Root}/{deviceId}/sensor/exit";

        // 구독용 와일드카드: 해당 장치의 모든 토픽
        public static string AllOf(string deviceId) => $"{Root}/{deviceId}/#";
    }
}
