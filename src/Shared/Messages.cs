namespace ConveyorTwin.Shared
{
    /// <summary>
    /// 모든 데이터 메시지의 공통 헤더.
    /// sid: 발행자 세션 ID (서버 재시작 시 변경, seq 초기화 판별용)
    /// seq: 토픽별로 1씩 증가하는 순번 (순서 정렬, 중복·과거 메시지 폐기용)
    /// ts : 발행 시각, Unix epoch 밀리초 (지연 측정용)
    /// </summary>
    public abstract class Envelope
    {
        public string Sid { get; set; }
        public long Seq { get; set; }
        public long Ts { get; set; }
    }

    /// <summary>수치형 측정값 (속도, 온도, 전류, 무게)</summary>
    public sealed class TelemetryMessage : Envelope
    {
        public double Value { get; set; }
        public string Unit { get; set; }
        public Quality Quality { get; set; }
    }

    /// <summary>광전 센서 엣지 이벤트. true = 물체가 빛을 가림(감지 시작)</summary>
    public sealed class SensorEdgeMessage : Envelope
    {
        public bool Blocked { get; set; }
    }

    /// <summary>컨베이어 가동 상태</summary>
    public sealed class RunStateMessage : Envelope
    {
        public RunState State { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>연결 상태. 정상 접속 시 online, Last Will로 offline 발행</summary>
    public sealed class StatusMessage
    {
        public bool Online { get; set; }
        public string Sid { get; set; }
        public long Ts { get; set; }
    }

    /// <summary>측정값 신뢰도 (OPC UA의 Quality 개념을 단순화)</summary>
    public enum Quality
    {
        Good,
        Uncertain,
        Bad
    }

    public enum RunState
    {
        Stopped,
        Running,
        Fault
    }
}
