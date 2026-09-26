using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // A line chart drawn as one uGUI mesh: horizontal grid lines, one
    // polyline per series, and a vertical cursor line while hovered. The
    // points are in data units (seconds across, the figure up); the axis
    // labels are TextMeshPro texts the screen lays out beside it.
    internal sealed class LineChart : MaskableGraphic
    {
        internal sealed class Series
        {
            public Color Colour;
            public float Thickness = 3f;
            public readonly List<Vector2> Points = new List<Vector2>();
        }

        internal readonly List<Series> Lines = new List<Series>();
        internal float XMax = 1f;
        internal float YMax = 1f;
        internal int GridLines = 4;
        internal Color GridColour = new Color(1f, 1f, 1f, 0.08f);
        internal Color CursorColour = new Color(1f, 1f, 1f, 0.35f);
        /// Where the cursor line is, in data units; negative for none.
        internal float CursorX = -1f;

        internal Vector2 ToLocal(Vector2 p)
        {
            var r = rectTransform.rect;
            return new Vector2(r.xMin + r.width * Mathf.Clamp01(p.x / Mathf.Max(XMax, 1e-3f)),
                               r.yMin + r.height * Mathf.Clamp01(p.y / Mathf.Max(YMax, 1e-3f)));
        }

        internal float ToDataX(float localX)
        {
            var r = rectTransform.rect;
            return r.width <= 0f ? 0f : Mathf.Clamp01((localX - r.xMin) / r.width) * XMax;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            if (r.width <= 1f || r.height <= 1f) return;

            for (var i = 0; i <= GridLines; i++)
            {
                var y = r.yMin + r.height * i / Mathf.Max(1, GridLines);
                Rect(vh, r.xMin, y - 1f, r.xMax, y + 1f, i == 0 ? new Color(1f, 1f, 1f, 0.25f) : GridColour);
            }

            if (CursorX >= 0f)
            {
                var x = ToLocal(new Vector2(CursorX, 0f)).x;
                Rect(vh, x - 1f, r.yMin, x + 1f, r.yMax, CursorColour);
            }

            foreach (var s in Lines)
            {
                for (var i = 1; i < s.Points.Count; i++)
                    Segment(vh, ToLocal(s.Points[i - 1]), ToLocal(s.Points[i]), s.Thickness, s.Colour);
            }
        }

        private static void Rect(VertexHelper vh, float x0, float y0, float x1, float y1, Color c)
        {
            var i = vh.currentVertCount;
            vh.AddVert(new Vector3(x0, y0), c, Vector4.zero);
            vh.AddVert(new Vector3(x0, y1), c, Vector4.zero);
            vh.AddVert(new Vector3(x1, y1), c, Vector4.zero);
            vh.AddVert(new Vector3(x1, y0), c, Vector4.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }

        // A segment as a quad, lengthened by half its thickness at each end
        // so consecutive segments overlap at the joins instead of notching.
        private static void Segment(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color c)
        {
            var d = b - a;
            var len = d.magnitude;
            if (len < 1e-4f) return;
            d /= len;
            var half = thickness * 0.5f;
            var n = new Vector2(-d.y, d.x) * half;
            a -= d * half * 0.5f;
            b += d * half * 0.5f;
            var i = vh.currentVertCount;
            vh.AddVert(a - n, c, Vector4.zero);
            vh.AddVert(a + n, c, Vector4.zero);
            vh.AddVert(b + n, c, Vector4.zero);
            vh.AddVert(b - n, c, Vector4.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }
    }
}
