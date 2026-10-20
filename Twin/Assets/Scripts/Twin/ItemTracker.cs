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
        public double FrontM;          // 앞단 추정 위치 (입구 센서 = 0, 출구 센서 = 벨트 길이)
        public double LengthM;         // 입구 센서가 가려진 시간 × 속도로 추정. 측정 전에는 기본값
        public bool LengthMeasured;
        public double? WeightKg;       // 입구 로드셀
        public long EntryTs;           // 입구 감지 시각 (서버 ts)
        public bool AtExit;            // 출구 센서가 감지함 (위치가 확정됨)
    }

    /// <summary>
    /// 광전 센서 엣지로 물건을 추적한다. 센서는 물건 ID를 모르므로 FIFO 순서가 곧 정체성이다.
    ///
    /// - 입구 감지(true): 물건 생성. 앞단 = 입구 센서 위치
    /// - 입구 해제(false): 가려진 시간 × 속도 = 물건 길이 추정
    /// - 이동: 수신한 벨트 속도로 위치를 "추정"한다. 단 출구 감지 전에는 출구 센서를 넘기지 않는다
    ///   (확인되지 않은 이동을 지어내지 않음. 출구에서 기다리는 물건이 곧 잼 후보 → 8일 차)
    /// - 출구 감지(true): FIFO 맨 앞 물건의 앞단을 출구 센서 위치로 보정하고 추정 오차를 기록
    /// - 출구 해제(false): 물건이 벨트를 떠남
    /// </summary>
    public sealed class ItemTracker
    {
        const double DefaultLengthM = 0.30;     // 길이 측정 전 표시용
        const float BoxWidthM = 0.20f;
        const float BoxHeightM = 0.15f;

        readonly List<TrackedItem> _items = new List<TrackedItem>();   // 입구 순서 (FIFO)
        readonly Func<TrackedItem, GameObject> _createView;
        TrackedItem _entering;     // 입구 센서를 가리고 있는 물건
        int _nextId = 1;

        public IReadOnlyList<TrackedItem> Items => _items;
        public long Entered { get; private set; }
        public long Exited { get; private set; }
        /// <summary>마지막 출구 보정량 (센서 위치 - 추정 위치, m). 양수면 추정이 늦었음</summary>
        public double LastCorrectionM { get; private set; }

        /// <summary>센서 엣지 순서가 맞지 않을 때 (HUD 경고용)</summary>
        public event Action<string> Warning;

        public ItemTracker(Func<TrackedItem, GameObject> createView)
        {
            _createView = createView;
        }

        public void OnEntryEdge(bool blocked, long ts, long nowMs, double speed)
        {
            if (blocked)
            {
                if (_entering != null)
                    Warning?.Invoke("입구 감지가 연속 두 번 왔습니다 (해제 엣지 누락)");
                var item = new TrackedItem
                {
                    Id = _nextId++,
                    // 수신 지연 동안 이미 움직인 거리만큼 앞으로
                    FrontM = Math.Max(0.0, speed * (nowMs - ts) / 1000.0),
                    LengthM = DefaultLengthM,
                    EntryTs = ts,
                };
                item.View = _createView(item);
                _items.Add(item);
                _entering = item;
                Entered++;
            }
            else
            {
                if (_entering == null)
                {
                    Warning?.Invoke("입구 해제 엣지인데 진입 중인 물건이 없습니다");
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
            Warning?.Invoke("무게를 붙일 물건이 없습니다");
        }

        public void OnExitEdge(bool blocked)
        {
            if (blocked)
            {
                var item = _items.Find(i => !i.AtExit);
                if (item == null)
                {
                    Warning?.Invoke("추적 중이 아닌 물건이 출구에서 감지되었습니다");
                    return;
                }
                LastCorrectionM = Equipment.BeltLengthM - item.FrontM;
                item.FrontM = Equipment.BeltLengthM;
                item.AtExit = true;
            }
            else
            {
                var item = _items.Find(i => i.AtExit);
                if (item == null)
                {
                    Warning?.Invoke("출구 해제 엣지인데 출구에 물건이 없습니다");
                    return;
                }
                Remove(item);
                Exited++;
            }
        }

        /// <summary>매 프레임 위치 추정 진행</summary>
        public void Tick(double dt, double speed)
        {
            double move = speed * dt;
            foreach (var item in _items)
            {
                item.FrontM += move;
                // 출구 감지 전에는 출구 센서를 넘어가지 않는다
                if (!item.AtExit && item.FrontM > Equipment.BeltLengthM)
                    item.FrontM = Equipment.BeltLengthM;
            }
        }

        public void Clear()
        {
            foreach (var item in _items)
                DestroyView(item);
            _items.Clear();
            _entering = null;
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
