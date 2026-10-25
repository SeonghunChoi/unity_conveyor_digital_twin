namespace ConveyorTwin.Shared
{
    /// <summary>
    /// 로봇 셀 배치 (2단계, docs/robot-cell.md).
    /// 서버는 가상 로봇 컨트롤러의 픽·적재 목표에, 트윈은 셀 배치 점검과 물건 인계 표시에 같은 값을 쓴다.
    ///
    /// 좌표계 = 로봇 베이스 좌표계 (ABB base frame): 원점은 J1 축과 바닥면의 교점,
    /// +X = 홈 자세에서 팔이 뻗는 방향(컨베이어 출구 쪽), +Z = 위, 오른손 좌표계, 단위 m.
    /// 트윈 씬에서 로봇은 Unity (0, 0, 1.70)에 놓이고 팔이 −Z(컨베이어 쪽)를 향한다.
    ///   Unity 월드 = (0, 0, 1.70) + (y, z, −x)
    /// </summary>
    public static class RobotCell
    {
        // ---- 물건 (1단계 박스와 같은 크기: 서버 ConveyorSimulator.ItemLengthM, 트윈 ItemTracker) ----
        public const double BoxLengthM = 0.30;   // 반송 방향
        public const double BoxWidthM = 0.20;
        public const double BoxHeightM = 0.15;

        // ---- 그리퍼 (진공 흡착식, 프리미티브로 표현) ----
        /// <summary>플랜지 면 → 흡착 패드 면 거리</summary>
        public const double GripperLengthM = 0.15;

        // ---- 픽 위치: 출구 센서(구동 드럼 중심)에 앞단이 닿아 멈춘 박스의 윗면 중심 ----
        public static readonly CellPoint PickTop = new CellPoint(0.875, 0.0, 1.0);

        // ---- 팔레트: EUR 1200×800×144, 긴 변이 베이스 X축과 평행, 로봇 오른쪽(−Y) ----
        public const double PalletLengthM = 1.20;
        public const double PalletWidthM = 0.80;
        public const double PalletHeightM = 0.144;
        /// <summary>팔레트 바닥면 중심</summary>
        public static readonly CellPoint PalletCenter = new CellPoint(0.0, -1.0, 0.0);

        // ---- 적재 패턴: 박스 긴 변을 팔레트 긴 변 방향으로 4 × 4, 3단 (박스가 팔레트를 꽉 채운다) ----
        public const int SlotsAlongLength = 4;   // 0.30m × 4 = 1.20m
        public const int SlotsAlongWidth = 4;    // 0.20m × 4 = 0.80m
        public const int Layers = 3;
        public const int SlotsPerLayer = SlotsAlongLength * SlotsAlongWidth;
        public const int SlotCount = SlotsPerLayer * Layers;

        /// <summary>
        /// 적재 순서 index(0부터)의 박스 윗면 중심. 한 단 안에서는 로봇에서 먼 줄부터 채워
        /// 이미 놓인 박스 위를 그리퍼가 지나가지 않게 한다.
        /// </summary>
        public static CellPoint SlotTop(int index)
        {
            int layer = index / SlotsPerLayer;
            int inLayer = index % SlotsPerLayer;
            int row = inLayer / SlotsAlongLength;      // 0 = 로봇에서 가장 먼 줄
            int col = inLayer % SlotsAlongLength;

            double x = PalletCenter.X - PalletLengthM / 2 + BoxLengthM * (col + 0.5);
            double y = PalletCenter.Y - PalletWidthM / 2 + BoxWidthM * (row + 0.5);
            double z = PalletCenter.Z + PalletHeightM + BoxHeightM * (layer + 1);
            return new CellPoint(x, y, z);
        }
    }

    /// <summary>셀 좌표의 점 (m)</summary>
    public readonly struct CellPoint
    {
        public readonly double X, Y, Z;
        public CellPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }
}
