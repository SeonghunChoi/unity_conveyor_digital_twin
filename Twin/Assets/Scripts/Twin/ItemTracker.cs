using System;
using System.Collections.Generic;
using ConveyorTwin.Shared;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>트윈이 추적 중인 물건 하나</summary>
    public sealed class TrackedItem
    {
        public int Id;
        public GameObject View;
        public double FrontM;          // 앞단 추정 위치 (입구 센서 = 0, 출구 센서 = 벨트 길이). 화면 표시용
        public double LengthM;         // 입구 센서가 가려진 시간 × 속도로 추정. 측정 전에는 기본값
        public bool LengthMeasured;
        public double? WeightKg;       // 입구 로드셀
        public long EntryTs;           // 입구 감지 시각 (서버 ts)
        public bool AtExit;            // 출구 센서가 감지함 (위치가 확정됨)

        /// <summary>입구 이후 벨트가 움직인 거리 (재생 시계 기준, 출구 앞에서 멈추지 않음). 잼 판단에 쓴다</summary>
        public double TravelM;
        public bool Jammed;
    }

    /// <summary>
    /// 광전 센서 엣지로 물건을 추적한다. 센서는 물건 ID를 모르므로 FIFO 순서가 곧 정체성이다.
    ///
    /// - 입구 감지(true): 물건 생성. 앞단 = 입구 센서 위치
    /// - 입구 해제(false): 가려진 시간 × 속도 = 물건 길이 추정
    /// - 이동: 수신한 벨트 속도로 위치를 "추정"한다. 단 출구 감지 전에는 출구 센서를 넘기지 않고,
    ///   앞 물건을 추월하지 않는다 (확인되지 않은 이동을 지어내지 않음)
    /// - 출구 감지(true): FIFO 맨 앞 물건의 앞단을 출구 센서 위치로 보정하고 추정 오차를 기록.
    ///   단 그 물건이 출구까지 올 만큼(벨트 길이의 75%) 이동하지 않았다면 짝짓지 않는다 (트윈이 보지 못한 물건)
    /// - 출구 해제(false): 물건이 벨트를 떠남
    ///
    /// 잼 판단 (docs/protocol.md 4절): 입구 이후 벨트가 움직인 거리가 벨트 길이 × 1.5를 넘었는데
    /// 출구 감지가 없으면 잼. 속도가 일정하면 "벨트 길이 / 속도 × 1.5초"와 같고, 모터 정지 시간은 빠진다.
    /// 시간 축은 컨트롤러의 재생 시계(서버 시각)다. 전송 지연이나 끊김으로 데이터가 없으면 재생이 멈추므로
    /// 판단도 멈추고, 늦은 데이터가 오면 그 구간을 그대로 재생한다 (이동 거리 보존).
    ///
    /// 한계: 센서가 양 끝에만 있어 걸린 위치는 알 수 없다. 트윈은 "입구와 출구 사이 어딘가"에서 걸렸다는 것만 안다.
    /// </summary>
    public sealed class ItemTracker
    {
        const double DefaultLengthM = 0.30;     // 길이 측정 전 표시용
        const float BoxWidthM = 0.20f;
        const float BoxHeightM = 0.15f;

        readonly List<TrackedItem> _items = new List<TrackedItem>();   // 입구 순서 (FIFO)
        readonly Func<TrackedItem, GameObject> _createView;
        /// <summary>
        /// 출구 감지를 이 물건과 짝지으려면 입구 이후 벨트 길이의 이 비율 이상 이동했어야 한다.
        /// 위치 추정 오차는 수 cm이므로 여유가 크다. 잼으로 멈춘 물건은 벨트 길이 × 1.5 넘게 이동한 것으로 계산된다.
        /// </summary>
        const double MinExitTravelRatio = 0.75;

        TrackedItem _entering;     // 입구 센서를 가리고 있는 물건
        bool _untrackedAtExit;     // 출구 센서를 가리고 있는 것이 추적 밖 물건
        int _nextId = 1;
        /// <summary>
        /// 재동기화 중 남은 거리. 트윈 시작·연결 복구 직후에는 트윈이 보지 못한 입구 감지로 들어온 물건이
        /// 벨트 위에 있을 수 있으므로, 벨트 길이 × 1.5만큼 벨트가 움직이는 동안 추적 밖 물건의 출구 엣지를 경고 없이 센다.
        /// </summary>
        double _resyncRemainingM;

        public IReadOnlyList<TrackedItem> Items => _items;
        public long Entered { get; private set; }
        public long Exited { get; private set; }
        public long Untracked { get; private set; }
        public bool Resyncing => _resyncRemainingM > 0;
        /// <summary>마지막 출구 보정량 (센서 위치 - 추정 위치, m). 양수면 추정이 늦었음</summary>
        public double LastCorrectionM { get; private set; }

        /// <summary>센서 엣지 순서가 맞지 않을 때 (HUD 경고용)</summary>
        public event Action<string> Warning;
        public event Action<TrackedItem> JamDetected;
        public event Action<TrackedItem> JamCleared;

        public ItemTracker(Func<TrackedItem, GameObject> createView)
        {
            _createView = createView;
            Resync();
        }

        /// <param name="playbackMs">현재 재생 시각. 입구 감지 ts 이후 재생된 만큼 앞으로 놓는다</param>
        public void OnEntryEdge(bool blocked, long ts, long playbackMs, double speed)
        {
            if (blocked)
            {
                if (_entering != null)
                    Warning?.Invoke("입구 감지가 연속 두 번 왔습니다 (해제 엣지 누락)");
                var item = new TrackedItem
                {
                    Id = _nextId++,
                    // 이 프레임에서 재생 시각이 ts를 지나친 만큼 (보통 한 프레임 이하)
                    FrontM = Math.Min(Equipment.BeltLengthM, Math.Max(0.0, speed * (playbackMs - ts) / 1000.0)),
                    LengthM = DefaultLengthM,
                    EntryTs = ts,
                };
                item.TravelM = item.FrontM;
                item.View = _createView(item);
                _items.Add(item);
                _entering = item;
                Entered++;
            }
            else
            {
                if (_entering == null)
                {
                    if (!Resyncing) Warning?.Invoke("입구 해제 엣지인데 진입 중인 물건이 없습니다");
                    return;
                }
                _entering.LengthM = Math.Max(0.05, speed * (ts - _entering.EntryTs) / 1000.0);
                _entering.LengthMeasured = true;
                _entering = null;
            }
        }

        /// <summary>입구 로드셀 무게: 입구 감지와 함께 오므로 가장 최근에 들어온 물건에 붙인다</summary>
        public void OnWeight(double kg)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].WeightKg == null)
                {
                    _items[i].WeightKg = kg;
                    return;
                }
            }
            if (!Resyncing) Warning?.Invoke("무게를 붙일 물건이 없습니다");
        }

        public void OnExitEdge(bool blocked)
        {
            if (blocked)
            {
                if (_untrackedAtExit && !Resyncing)
                    Warning?.Invoke("출구 감지가 연속 두 번 왔습니다 (해제 엣지 누락)");
                _untrackedAtExit = false;

                // FIFO 맨 앞 물건이라도 출구까지 올 만큼 이동하지 않았다면 그 물건이 아니다:
                // 트윈이 입구 감지를 보지 못한 물건(재동기화 전부터 벨트 위에 있던 물건)이 나간 것
                var item = _items.Find(i => !i.AtExit);
                if (item == null || item.TravelM < Equipment.BeltLengthM * MinExitTravelRatio)
                {
                    Untracked++;
                    _untrackedAtExit = true;
                    if (!Resyncing) Warning?.Invoke("추적 중이 아닌 물건이 출구에서 감지되었습니다");
                    return;
                }
                LastCorrectionM = Equipment.BeltLengthM - item.FrontM;
                item.FrontM = Equipment.BeltLengthM;
                item.AtExit = true;
                if (item.Jammed)
                {
                    item.Jammed = false;
                    JamCleared?.Invoke(item);
                }
            }
            else
            {
                if (_untrackedAtExit)
                {
                    // 추적 밖 물건이 출구를 빠져나감
                    _untrackedAtExit = false;
                    return;
                }
                var item = _items.Find(i => i.AtExit);
                if (item == null)
                {
                    if (!Resyncing) Warning?.Invoke("출구 해제 엣지인데 출구에 물건이 없습니다");
                    return;
                }
                Remove(item);
                Exited++;
            }
        }

        /// <summary>
        /// 매 프레임 진행.
        /// dt: 이번 프레임에 재생 시계가 흐른 시간(초), speed: 그 재생 시각의 벨트 속도
        /// </summary>
        public void Tick(double dt, double speed)
        {
            double move = speed * dt;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                double next = item.FrontM + move;
                // 출구 감지 전에는 출구 센서를 넘어가지 않는다
                if (!item.AtExit)
                    next = Math.Min(next, Equipment.BeltLengthM);
                // 앞 물건을 추월하지 않는다
                if (i > 0)
                    next = Math.Min(next, _items[i - 1].FrontM - _items[i - 1].LengthM);
                item.FrontM = Math.Max(item.FrontM, next);

                item.TravelM += move;
                if (!item.AtExit && !item.Jammed && item.TravelM > Equipment.BeltLengthM * Equipment.JamTimeFactor)
                {
                    item.Jammed = true;
                    JamDetected?.Invoke(item);
                }
            }

            if (_resyncRemainingM > 0)
                _resyncRemainingM -= move;
        }

        /// <summary>
        /// 추적 재동기화: 트윈 시작, 연결 복구처럼 센서 신호를 놓쳤을 수 있는 시점에 호출.
        /// 놓친 엣지로는 FIFO 짝이 어긋나므로 추적을 비우고 다시 시작한다.
        /// </summary>
        public void Resync()
        {
            Clear();
            _resyncRemainingM = Equipment.BeltLengthM * Equipment.JamTimeFactor;
        }

        public void Clear()
        {
            foreach (var item in _items)
                DestroyView(item);
            _items.Clear();
            _entering = null;
            _untrackedAtExit = false;
        }

        void Remove(TrackedItem item)
        {
            DestroyView(item);
            _items.Remove(item);
            if (_entering == item) _entering = null;
        }

        static void DestroyView(TrackedItem item)
        {
            if (item.View != null) UnityEngine.Object.Destroy(item.View);
        }

        /// <summary>물건 박스를 벨트 위 추정 위치에 놓는다</summary>
        public void Place(TrackedItem item, Vector3 entryPoint, Vector3 flowDir, float beltTopY)
        {
            float len = (float)item.LengthM;
            var center = entryPoint + flowDir * (float)(item.FrontM - item.LengthM * 0.5);
            center.y = beltTopY + BoxHeightM * 0.5f;
            var t = item.View.transform;
            t.SetPositionAndRotation(center, Quaternion.LookRotation(flowDir, Vector3.up));
            t.localScale = new Vector3(BoxWidthM, BoxHeightM, len);
        }
    }
}
