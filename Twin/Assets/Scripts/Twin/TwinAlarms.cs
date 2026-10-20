using System;
using System.Collections.Generic;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>알람 등급 (산업 HMI의 우선순위 개념을 단순화)</summary>
    public enum AlarmSeverity
    {
        /// <summary>설비가 멈췄거나 트윈이 현실을 볼 수 없음. 즉시 대응</summary>
        Critical,
        /// <summary>값을 믿기 어렵거나 이상 징후. 확인 필요</summary>
        Warning,
        /// <summary>안내 (자동으로 해소됨)</summary>
        Info,
    }

    /// <summary>이번 프레임에 성립한 알람 조건</summary>
    public readonly struct AlarmCondition
    {
        public readonly string Key;        // 같은 알람을 식별 (문구는 바뀌어도 키는 유지)
        public readonly AlarmSeverity Severity;
        public readonly string Text;

        public AlarmCondition(string key, AlarmSeverity severity, string text)
        {
            Key = key;
            Severity = severity;
            Text = text;
        }
    }

    /// <summary>알람 하나의 수명: 발생 → (확인) → 해소</summary>
    public sealed class Alarm
    {
        public string Key;
        public AlarmSeverity Severity;
        public string Text;
        public long RaisedMs;
        public long ClearedMs;        // 0이면 아직 활성
        public bool Acknowledged;

        public bool Active => ClearedMs == 0;
    }

    /// <summary>
    /// 알람 관리. 조건은 매 프레임 트윈이 판단해 넘기고, 여기서는 상태 전이만 관리한다.
    ///
    /// 산업 HMI 알람 관리(ISA-18.2)의 기본 개념을 따른다:
    /// - 발생(raise)과 해소(clear)는 조건이 결정한다. 사람이 끌 수 없다
    /// - 확인(acknowledge)은 운전자가 "봤다"는 표시일 뿐 알람을 없애지 않는다
    /// - 해소됐어도 확인하지 않은 알람은 목록에 남는다 (짧게 지나간 이상을 놓치지 않게)
    /// - 깜빡이는 조건이 알람을 반복 발생시키지 않도록 해소 판정에 짧은 유지 시간을 둔다
    /// </summary>
    public sealed class TwinAlarms
    {
        /// <summary>조건이 이 시간 동안 연속으로 사라져야 해소 (채터링 방지)</summary>
        const long ClearHoldMs = 1000;
        const int HistoryMax = 30;

        readonly Dictionary<string, Alarm> _active = new Dictionary<string, Alarm>();
        readonly Dictionary<string, long> _missingSince = new Dictionary<string, long>();
        readonly List<Alarm> _history = new List<Alarm>();   // 최근 발생 순 (활성 포함)
        readonly HashSet<string> _seen = new HashSet<string>();

        /// <summary>최근 발생 순 목록 (활성 + 해소 후 미확인 + 해소·확인된 이력)</summary>
        public IReadOnlyList<Alarm> History => _history;
        public int ActiveCount => _active.Count;
        public int UnacknowledgedCount
        {
            get
            {
                int n = 0;
                foreach (var a in _history) if (!a.Acknowledged) n++;
                return n;
            }
        }

        /// <summary>가장 높은 활성 등급 (없으면 null)</summary>
        public AlarmSeverity? HighestActive
        {
            get
            {
                AlarmSeverity? best = null;
                foreach (var a in _active.Values)
                    if (best == null || a.Severity < best) best = a.Severity;
                return best;
            }
        }

        public void Evaluate(List<AlarmCondition> conditions, long nowMs)
        {
            _seen.Clear();
            foreach (var c in conditions)
            {
                _seen.Add(c.Key);
                _missingSince.Remove(c.Key);
                if (_active.TryGetValue(c.Key, out var alarm))
                {
                    alarm.Text = c.Text;   // 경과 시간 등 문구는 갱신
                    continue;
                }
                alarm = new Alarm { Key = c.Key, Severity = c.Severity, Text = c.Text, RaisedMs = nowMs };
                // Info는 안내일 뿐이라 확인을 요구하지 않는다
                alarm.Acknowledged = c.Severity == AlarmSeverity.Info;
                _active.Add(c.Key, alarm);
                _history.Insert(0, alarm);
                Debug.LogWarning($"[Twin] 알람 발생 ({c.Severity}): {c.Text}");
            }

            List<string> cleared = null;
            foreach (var kv in _active)
            {
                if (_seen.Contains(kv.Key)) continue;
                if (!_missingSince.TryGetValue(kv.Key, out var since))
                {
                    _missingSince[kv.Key] = nowMs;
                    continue;
                }
                if (nowMs - since < ClearHoldMs) continue;
                (cleared ??= new List<string>()).Add(kv.Key);
            }
            if (cleared != null)
            {
                foreach (var key in cleared)
                {
                    var alarm = _active[key];
                    alarm.ClearedMs = nowMs;
                    _active.Remove(key);
                    _missingSince.Remove(key);
                    Debug.Log($"[Twin] 알람 해소 ({alarm.Severity}): {alarm.Text} ({(nowMs - alarm.RaisedMs) / 1000.0:F1}s)");
                }
            }

            // 이력 정리: 해소·확인된 것부터 오래된 순으로 버린다
            for (int i = _history.Count - 1; i >= 0 && _history.Count > HistoryMax; i--)
                if (!_history[i].Active && _history[i].Acknowledged)
                    _history.RemoveAt(i);
        }

        /// <summary>운전자 확인: 모든 알람에 "봤음" 표시 (활성 알람은 조건이 사라질 때까지 남는다)</summary>
        public void AcknowledgeAll()
        {
            foreach (var a in _history) a.Acknowledged = true;
        }

        public void Clear()
        {
            _active.Clear();
            _missingSince.Clear();
            _history.Clear();
        }
    }
}
