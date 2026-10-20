using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 세션·순번 검사 (docs/protocol.md 4절).
    /// - sid가 바뀌면 (서버 재시작) 모든 토픽의 seq 기록을 초기화하고 SessionChanged를 알린다
    /// - 같은 sid에서 seq가 마지막 처리 값 이하이면 중복·과거 메시지로 보고 버린다
    /// - seq가 건너뛰면 그만큼 유실된 것으로 본다 (LastGap)
    /// 순서 정렬은 컨트롤러의 재생 시계가 맡는다 (ts 순으로 처리한 뒤 이 검사를 거친다).
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

        /// <summary>
        /// 직전 Accept에서 건너뛴 seq 수 (마지막 처리 seq + 1보다 큰 seq가 왔을 때).
        /// 센서 엣지처럼 하나라도 잃으면 안 되는 토픽에서 유실 감지에 쓴다.
        /// </summary>
        public long LastGap { get; private set; }

        /// <summary>처리해도 되는 메시지면 true</summary>
        public bool Accept(string topic, Envelope message)
        {
            ObserveSession(message.Sid);
            LastGap = 0;
            if (_lastSeq.TryGetValue(topic, out var last))
            {
                if (message.Seq <= last)
                {
                    Dropped++;
                    return false;
                }
                LastGap = message.Seq - last - 1;
            }
            _lastSeq[topic] = message.Seq;
            return true;
        }

        /// <summary>
        /// 토픽의 seq 기록을 지운다. 유실이 이미 알려진 구간(설비 연결 끊김) 뒤에 같은 유실을 다시 감지하지 않기 위해.
        /// </summary>
        public void Forget(string topic) => _lastSeq.Remove(topic);
    }
}
