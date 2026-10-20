using UnityEngine;
using UnityEngine.UIElements;

namespace ConveyorTwin.Twin.Hmi
{
    /// <summary>
    /// 최근 값 추세선 (Painter2D). 정상 운전 범위를 옅은 띠로 깔아, 값이 범위를 벗어난 순간이 바로 보이게 한다.
    /// 값이 없는 구간(NaN)은 선을 끊는다 (끊김을 그럴듯한 선으로 메우지 않음).
    /// </summary>
    public sealed class Sparkline : VisualElement
    {
        readonly float[] _values;
        int _head;          // 다음에 쓸 위치
        int _count;
        readonly float _min, _max, _normalLo, _normalHi;

        static readonly Color BandColor = new Color(0.31f, 0.34f, 0.37f, 0.55f);
        static readonly Color LineColor = new Color(0.86f, 0.88f, 0.90f);
        static readonly Color AlarmColor = new Color(1.0f, 0.62f, 0.2f);

        public Sparkline(int capacity, float min, float max, float normalLo, float normalHi)
        {
            _values = new float[capacity];
            _min = min;
            _max = max;
            _normalLo = normalLo;
            _normalHi = normalHi;
            style.flexGrow = 1;
            generateVisualContent += Draw;
        }

        public void Push(float value)
        {
            _values[_head] = value;
            _head = (_head + 1) % _values.Length;
            if (_count < _values.Length) _count++;
            MarkDirtyRepaint();
        }

        float Y(float v, float h) => h - Mathf.InverseLerp(_min, _max, Mathf.Clamp(v, _min, _max)) * h;

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 2 || r.height < 2) return;
            var p = ctx.painter2D;

            // 정상 범위 띠
            float top = Y(_normalHi, r.height), bottom = Y(_normalLo, r.height);
            p.fillColor = BandColor;
            p.BeginPath();
            p.MoveTo(new Vector2(0, top));
            p.LineTo(new Vector2(r.width, top));
            p.LineTo(new Vector2(r.width, bottom));
            p.LineTo(new Vector2(0, bottom));
            p.ClosePath();
            p.Fill();

            if (_count < 2) return;
            float dx = r.width / (_values.Length - 1);
            int start = (_head - _count + _values.Length) % _values.Length;
            p.lineWidth = 1.6f;
            p.lineJoin = LineJoin.Round;

            // 정상 범위 밖이면 선 색을 바꾸기 위해 구간마다 그린다
            for (int i = 1; i < _count; i++)
            {
                float a = _values[(start + i - 1) % _values.Length];
                float b = _values[(start + i) % _values.Length];
                if (float.IsNaN(a) || float.IsNaN(b)) continue;
                float x0 = (_values.Length - _count + i - 1) * dx, x1 = x0 + dx;
                bool outside = b < _normalLo || b > _normalHi;
                p.strokeColor = outside ? AlarmColor : LineColor;
                p.BeginPath();
                p.MoveTo(new Vector2(x0, Y(a, r.height)));
                p.LineTo(new Vector2(x1, Y(b, r.height)));
                p.Stroke();
            }
        }
    }
}
