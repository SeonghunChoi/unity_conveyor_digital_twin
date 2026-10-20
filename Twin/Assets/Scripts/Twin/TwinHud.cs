using System;
using System.Text;
using UnityEngine;

namespace ConveyorTwin.Twin
{
    /// <summary>
    /// 개발·검증용 텍스트 HUD. 트윈이 "받은 것"을 그대로 보여 서버의 송신 로그와 비교할 수 있게 한다.
    /// 시연용 HMI 대시보드는 11~12일 차에 별도로 만든다.
    /// </summary>
    [RequireComponent(typeof(ConveyorTwinController))]
    public sealed class TwinHud : MonoBehaviour
    {
        ConveyorTwinController _twin;
        GUIStyle _style;
        readonly StringBuilder _sb = new StringBuilder();

        void Awake() => _twin = GetComponent<ConveyorTwinController>();

        void OnGUI()
        {
            if (_style == null)
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sb.Clear();
            _sb.AppendLine($"브로커   {(_twin.BrokerConnected ? "<color=#7f7>연결</color>" : "<color=#f77>끊김</color>")}  {_twin.BrokerError}");
            _sb.AppendLine($"설비     {Online()}   sid {_twin.Sid ?? "-"}");
            _sb.AppendLine($"가동     {(_twin.RunState?.ToString().ToLowerInvariant() ?? "-")} {_twin.RunReason}");
            _sb.AppendLine($"속도     {Fmt(_twin.Speed, "F3", "m/s", now)}");
            _sb.AppendLine($"모터온도 {Fmt(_twin.Temp, "F1", "°C", now)}");
            _sb.AppendLine($"모터전류 {Fmt(_twin.Current, "F2", "A", now)}");
            _sb.AppendLine($"최근무게 {Fmt(_twin.Weight, "F2", "kg", now)}");
            var tr = _twin.Tracker;
            if (tr != null)
            {
                _sb.AppendLine($"물건     벨트 위 {tr.Items.Count} / 입구 {tr.Entered} / 출구 {tr.Exited}");
                _sb.AppendLine($"출구보정 {tr.LastCorrectionM * 1000:F0} mm (센서 위치 - 추정 위치)");
            }
            _sb.AppendLine($"수신     {_twin.Received}건, 지연 {_twin.LastLatencyMs} ms, 중복·과거 폐기 {_twin.Dropped}건");
            foreach (var w in _twin.Warnings)
                _sb.AppendLine($"<color=#fc6>{w}</color>");

            GUI.Box(new Rect(10, 10, 520, 24 + 20 * (11 + _twin.Warnings.Count)), _sb.ToString(), _style);
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
            return $"{v.Value.ToString(format)} {unit}  ({v.Quality.ToString().ToLowerInvariant()}, {(now - v.ReceivedMs) / 1000.0:F1}s 전)";
        }
    }
}
