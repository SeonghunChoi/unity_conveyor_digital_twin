using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;

namespace ConveyorTwin.Twin.OpcUa
{
    /// <summary>
    /// 로봇 OPC UA 값 검사 (원칙 4: 믿되 맹신하지 않는다). MQTT 쪽 MessageGate의 OPC UA판.
    /// MQTT에서는 sid·seq·quality를 직접 만들었지만, OPC UA에는 같은 정보가 표준으로 있다:
    ///  - StatusCode Bad → 버린다 (마지막 값 유지). Uncertain → 받되 센다
    ///  - SourceTimestamp가 그 노드의 마지막 처리 값보다 이전 → 과거 값으로 버린다 (순서 이상)
    ///  - 같으면 중복으로 조용히 버린다. 재연결해 구독을 다시 만들면 서버가 현재값을 다시 보내는데,
    ///    값이 그사이 바뀌지 않았으면 SourceTimestamp도 같다 — 이상이 아니라 정상 동작이다
    ///  - SourceTimestamp가 미래(같은 PC인데 2초 이상 앞섬) → 시계 이상으로 버린다
    ///  - 관절각: 6개가 아니거나 가동 범위(+여유)를 벗어나면 이상값으로 버린다
    ///  - 서버 시작 시각(Server.ServerStatus.StartTime)이 바뀌면 서버 재시작 → 시각 기록 초기화 (MQTT의 sid 변경)
    /// </summary>
    public sealed class RobotGate
    {
        const double LimitMarginDeg = 1.0;
        static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(2);

        readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>();

        public DateTime ServerStartUtc { get; private set; }
        public long Accepted { get; private set; }
        public long RejectedBad { get; private set; }
        public long RejectedOld { get; private set; }
        public long Duplicates { get; private set; }
        public long RejectedFuture { get; private set; }
        public long RejectedRange { get; private set; }
        public long Uncertain { get; private set; }

        /// <summary>서버 재시작 (이전 시작 시각, 새 시작 시각)</summary>
        public event Action<DateTime, DateTime> ServerRestarted;

        public void ObserveServerStart(DateTime startUtc)
        {
            if (startUtc == DateTime.MinValue || startUtc == ServerStartUtc)
                return;
            var old = ServerStartUtc;
            ServerStartUtc = startUtc;
            _last.Clear();
            if (old != DateTime.MinValue)
                ServerRestarted?.Invoke(old, startUtc);
        }

        /// <param name="reason">버린 이유 (받으면 null)</param>
        /// <returns>처리해도 되는 값이면 true</returns>
        public bool Accept(in RobotSample s, DateTime nowUtc, out string reason)
        {
            reason = null;
            if (StatusCodeIsBad(s.Status))
            {
                RejectedBad++;
                reason = $"StatusCode Bad (0x{s.Status:X8})";
                return false;
            }
            if (StatusCodeIsUncertain(s.Status))
                Uncertain++;

            if (s.SourceUtc > nowUtc + FutureTolerance)
            {
                RejectedFuture++;
                reason = $"SourceTimestamp가 {(s.SourceUtc - nowUtc).TotalSeconds:F1}s 미래";
                return false;
            }
            if (_last.TryGetValue(s.Node, out var last))
            {
                if (s.SourceUtc == last)
                {
                    Duplicates++;
                    return false;   // reason = null: 정상적인 중복
                }
                if (s.SourceUtc < last)
                {
                    RejectedOld++;
                    reason = $"과거 값 ({(last - s.SourceUtc).TotalMilliseconds:F0}ms 이전)";
                    return false;
                }
            }

            if (s.Node == RobotNodes.JointPositions && !JointsValid(s.Value, out reason))
            {
                RejectedRange++;
                return false;
            }

            _last[s.Node] = s.SourceUtc;
            Accepted++;
            return true;
        }

        static bool JointsValid(object value, out string reason)
        {
            reason = null;
            if (!(value is double[] q) || q.Length != 6)
            {
                reason = "관절각이 Double[6]이 아님";
                return false;
            }
            for (int i = 0; i < 6; i++)
            {
                if (double.IsNaN(q[i]) || q[i] < RobotKinematics.MinDeg[i] - LimitMarginDeg || q[i] > RobotKinematics.MaxDeg[i] + LimitMarginDeg)
                {
                    reason = $"J{i + 1} = {q[i]:F1}° 가동 범위 밖";
                    return false;
                }
            }
            return true;
        }

        // StatusCode 상위 2비트: 00 Good, 01 Uncertain, 10 Bad (OPC UA Part 4)
        static bool StatusCodeIsBad(uint code) => (code & 0x80000000u) != 0;
        static bool StatusCodeIsUncertain(uint code) => (code & 0xC0000000u) == 0x40000000u;
    }
}
