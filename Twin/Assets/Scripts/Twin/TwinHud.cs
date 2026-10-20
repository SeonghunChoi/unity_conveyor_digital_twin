using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 개발·검증용 텍스트 HUD. 트윈이 "받은 것"과 "판단한 것"을 그대로 보여 서버의 송신 로그와 비교할 수 있게 한다.
    /// 시연용 HMI 대시보드는 11~12일 차에 별도로 만든다.
    /// </summary>
    [RequireComponent(typeof(ConveyorTwinController))]
    public sealed class TwinHud : MonoBehaviour
    {
        ConveyorTwinController _twin;
        GUIStyle _style;
        /// <summary>표시용 배속 평활 시간</summary>
        const float RateSmoothingS = 0.3f;
        /// <summary>평활 배속이 이보다 크면 "따라잡는 중" 표시</summary>
        const double CatchUpShowRate = 1.1;

        readonly StringBuilder _sb = new StringBuilder();
        double _rate = 1.0;
        readonly List<string> _alarms = new List<string>();

        void Awake() => _twin = GetComponent<ConveyorTwinController>();

        void OnGUI()
        {
            if (_style == null)
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _twin.CollectAlarms(_alarms);
            _sb.Clear();
            int lines = 0;
            void Line(string s) { _sb.AppendLine(s); lines++; }

            Line($"브로커   {(_twin.BrokerConnected ? "<color=#7f7>연결</color>" : "<color=#f77>끊김</color>")}  {_twin.BrokerError}");
            Line($"설비     {Online()}   sid {_twin.Sid ?? "-"}");
            Line($"가동     {(_twin.RunState?.ToString().ToLowerInvariant() ?? "-")} {_twin.RunReason}");
            Line($"속도     {Fmt(_twin.Speed, "F3", "m/s", now)}");
            Line($"모터온도 {Fmt(_twin.Temp, "F1", "°C", now)}");
            Line($"모터전류 {Fmt(_twin.Current, "F2", "A", now)}");
            Line($"최근무게 {Fmt(_twin.Weight, "F2", "kg", now)}");
            var tr = _twin.Tracker;
            if (tr != null)
            {
                Line($"물건     벨트 위 {tr.Items.Count} / 입구 {tr.Entered} / 출구 {tr.Exited} / 추적 밖 배출 {tr.Untracked}");
                Line($"출구보정 {tr.LastCorrectionM * 1000:F0} mm (센서 위치 - 추정 위치)");
            }
            Line($"수신     {_twin.Received}건, 네트워크 지연 {_twin.LastLatencyMs} ms, 재정렬 {_twin.Reordered}건, 중복·과거 폐기 {_twin.Dropped}건");
            long delay = _twin.ScreenDelayMs;
            string delayText = $"{delay / 1000.0:F1}초 전 상태";
            if (delay > 1000) delayText = $"<color=#f96><b>{delayText}</b></color>";
            Line($"화면     {delayText} (서버 시각 기준 재생, 속도 {_twin.PlaybackSpeed:F3} m/s)" +
                 (_twin.PlaybackHolding ? "  <color=#fc6>다음 데이터 대기 중</color>" : ""));
            // 배속은 프레임마다 흔들리므로 표시용으로 부드럽게 한다
            if (Event.current.type == EventType.Repaint)
                _rate += (_twin.PlaybackRate - _rate) * Mathf.Clamp01(Time.unscaledDeltaTime / RateSmoothingS);
            if (_rate > CatchUpShowRate)
                Line($"         <color=#6cf><b>▶▶ 따라잡는 중 ×{_rate:F1}</b></color> (밀린 구간을 빨리 재생: 화면 속도가 실제 설비 속도보다 빠름)");

            Line(_alarms.Count == 0 ? "<b>상태     <color=#7f7>정상</color></b>" : "<b>상태</b>");
            foreach (var a in _alarms)
                Line($"  <color=#f66><b>● {a}</b></color>");
            foreach (var w in _twin.Warnings)
                Line($"<color=#fc6>{w}</color>");

            GUI.Box(new Rect(10, 10, 600, 16 + 20 * lines), _sb.ToString(), _style);
        }

        string Online()
        {
            switch (_twin.DeviceOnline)
            {
                case true: return "<color=#7f7>online</color>";
                case false: return $"<color=#f77>offline</color> ({DateTimeOffset.FromUnixTimeMilliseconds(_twin.OfflineSinceMs).LocalDateTime:HH:mm:ss}부터)";
                default: return "-";
            }
        }

        static string Fmt(SignalValue v, string format, string unit, long now)
        {
            if (!v.HasValue) return "-";
            string s = $"{v.Value.ToString(format)} {unit}  ({v.Quality.ToString().ToLowerInvariant()}, {(now - v.ReceivedMs) / 1000.0:F1}s 전)";
            if (v.Rejected > 0) s += $"  거부 {v.Rejected}건";
            return s;
        }
    }
}
