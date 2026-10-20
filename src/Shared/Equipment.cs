namespace ConveyorTwin.Shared
{
    /// <summary>
    /// 설비 치수 (mk GUF-P 2000 CAD 실측, docs/protocol.md 6절).
    /// 서버는 물건 이동 시뮬레이션에, 트윈은 잼 판단에 같은 값을 쓴다.
    /// </summary>
    public static class Equipment
    {
        /// <summary>벨트 길이 = 입구·출구 센서 간 거리 (드럼 중심 간 거리), m</summary>
        public const double BeltLengthM = 1.95;

        /// <summary>잼 판단 여유 배수: 입구 감지 후 BeltLengthM / 속도 × 이 값 안에 출구 감지가 없으면 잼</summary>
        public const double JamTimeFactor = 1.5;
    }
}
