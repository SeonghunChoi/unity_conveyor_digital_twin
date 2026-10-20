using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 세션·순번 검사 (docs/protocol.md 4절).
    /// - sid가 바뀌면 (서버 재시작) 모든 토픽의 seq 기록을 초기화하고 SessionChanged를 알린다
    /// - 같은 sid에서 seq가 마지막 처리 값 이하이면 중복·과거 메시지로 보고 버린다
    /// 순서가 뒤바뀐 엣지를 재정렬하는 버퍼는 8일 차 장애 대응에서 추가한다.
    /// </summary>
    public sealed class MessageGate
    {
        readonly Dictionary<string, long> _lastSeq = new Dictionary<string, long>();

        public string Sid { get; private set; }
        public long Dropped { get; private set; }

        /// <summary>새 세션 시작 (이전 sid, 새 sid)</summary>
        public event Action<string, string> SessionChanged;

        /// <summary>status 메시지처럼 seq가 없는 메시지로 세션만 확인</summary>
        public void ObserveSession(string sid)
        {
            if (string.IsNullOrEmpty(sid) || sid == Sid)
                return;
            var old = Sid;
            Sid = sid;
            _lastSeq.Clear();
            SessionChanged?.Invoke(old, sid);
        }

        /// <summary>처리해도 되는 메시지면 true</summary>
        public bool Accept(string topic, Envelope message)
        {
            ObserveSession(message.Sid);
            if (_lastSeq.TryGetValue(topic, out var last) && message.Seq <= last)
            {
                Dropped++;
                return false;
            }
            _lastSeq[topic] = message.Seq;
            return true;
        }
    }
}
