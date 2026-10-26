using System;

namespace ConveyorTwin.Shared
{
    /// <summary>
    /// IRB 2600-20/1.65 기구학 (docs/cad-robot.md). 서버의 가상 로봇 컨트롤러가 목표 관절각을 만들 때,
    /// 트윈 에디터의 셀 배치 점검(RobotCellCheck)이 도달성을 확인할 때 쓴다.
    /// 트윈 런타임은 쓰지 않는다 — 트윈은 서버가 보낸 관절각만 따른다 (원칙 1).
    ///
    /// 각도는 ABB 부호(도): 베이스 좌표(RobotCell) 오른손 법칙. J1 위에서 볼 때 반시계, J2 팔 앞으로, J3 팔 아래로, J5 손목 아래로.
    /// </summary>
    public static class RobotKinematics
    {
        // ---- 링크 치수 (m), 베이스 좌표 (r, z) 평면 ----
        public const double J2Radial = 0.150;
        public const double J2Height = 0.445;
        public const double UpperArm = 0.700;          // J2 → J3
        public const double ForearmX = 0.795;          // J3 → J5 (홈 자세 수평 성분)
        public const double ForearmZ = 0.115;          // J3 → J5 (홈 자세 수직 성분)
        public const double WristToFlange = 0.085;     // J5 → 플랜지 면

        static readonly double Forearm = Math.Sqrt(ForearmX * ForearmX + ForearmZ * ForearmZ);
        static readonly double Beta0Deg = Math.Atan2(ForearmZ, ForearmX) * Rad2Deg;
        const double Rad2Deg = 180.0 / Math.PI;

        /// <summary>관절 가동 범위 (도, ABB 제품 자료)</summary>
        public static readonly double[] MinDeg = { -180, -95, -180, -400, -120, -400 };
        public static readonly double[] MaxDeg = { 180, 155, 75, 400, 120, 400 };

        /// <summary>관절 최대 속도 (도/s, ABB 제품 자료)</summary>
        public static readonly double[] MaxSpeedDegPerS = { 175, 175, 175, 360, 360, 500 };

        /// <summary>J2 → 손목 중심 최대 거리 (상완 + 전완)</summary>
        public static double MaxWristReach => UpperArm + Forearm;

        /// <summary>
        /// 공구가 수직으로 내려보는 자세의 해석적 IK (팔꿈치 위, J4 = 0).
        /// J6 = J1로 두어 공구의 수평 방향(요)을 베이스 X축에 고정한다 — 컨베이어에서 집은 박스 방향 그대로 팔레트에 놓인다.
        /// </summary>
        /// <param name="tcp">공구 중심점(흡착면 중심) 목표, 베이스 좌표 m</param>
        /// <param name="toolLength">플랜지 면 → 공구 중심점 거리 m</param>
        /// <param name="q">관절각 6개 (도)</param>
        /// <param name="reachRatio">J2→손목 거리 / 최대 거리</param>
        /// <returns>도달 가능하고 모든 관절이 가동 범위 안이면 true</returns>
        public static bool SolveTopDown(CellPoint tcp, double toolLength, out double[] q, out double reachRatio)
        {
            q = new double[6];
            double wx = tcp.X, wy = tcp.Y, wz = tcp.Z + toolLength + WristToFlange;   // 손목 중심(J5)
            double r = Math.Sqrt(wx * wx + wy * wy) - J2Radial;
            double z = wz - J2Height;
            double d = Math.Sqrt(r * r + z * z);
            reachRatio = d / MaxWristReach;
            if (d > UpperArm + Forearm || d < Math.Abs(UpperArm - Forearm))
                return false;

            double phi = Math.Atan2(z, r);
            double alpha = Math.Acos((UpperArm * UpperArm + d * d - Forearm * Forearm) / (2 * UpperArm * d));
            double e2 = phi + alpha;                                       // 상완 앙각 (수평 기준, rad)
            double er = UpperArm * Math.Cos(e2), ez = UpperArm * Math.Sin(e2);
            double e3 = Math.Atan2(z - ez, r - er);                        // J3 → 손목 앙각 (rad)

            double j1 = Math.Atan2(wy, wx) * Rad2Deg;
            double j2 = 90.0 - e2 * Rad2Deg;
            double j3 = Beta0Deg - j2 - e3 * Rad2Deg;
            double j5 = 90.0 - j2 - j3;                                    // 공구 방향 = −(J2+J3+J5) = −90°
            q[0] = j1; q[1] = j2; q[2] = j3; q[3] = 0; q[4] = j5; q[5] = j1;
            return WithinLimits(q);
        }

        public static bool WithinLimits(double[] q) => LimitMarginDeg(q, out _) >= 0;

        /// <summary>가동 범위까지 가장 가까운 여유(도)와 그 관절 인덱스. 음수면 범위 밖</summary>
        public static double LimitMarginDeg(double[] q, out int joint)
        {
            double m = double.MaxValue;
            joint = 0;
            for (int i = 0; i < 6; i++)
            {
                double mi = Math.Min(q[i] - MinDeg[i], MaxDeg[i] - q[i]);
                if (mi < m) { m = mi; joint = i; }
            }
            return m;
        }
    }
}
