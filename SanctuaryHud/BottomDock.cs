using System.Collections.Generic;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // What makes the bottom stand-ins one panel rather than five.
    //
    // Every piece docks against its neighbours on one baseline: a left
    // column the width of the game's own orders and information panels —
    // the unit card sitting straight on the orders row — and the build area
    // against its right edge: the selection row and the options along the
    // bottom, the tier tabs and the queue above them. No gaps. Each piece
    // keeps its own plate (they abut exactly, so the fills read as one), but
    // the hairlines are drawn here: one along every exposed top edge of the
    // combined shape, thin dividers where two pieces meet, and the exposed
    // sides of a taller column, so the outline is the outline of the whole.
    //
    // With PanelArt on, the pieces wear the game's own dashed panel sprite
    // instead, each framed as the game frames its panels, and the outline
    // is left off.
    internal static class BottomDock
    {
        internal static ConfigEntry<bool> PanelArt;

        internal static void Bind(ConfigFile config)
        {
            PanelArt = config.Bind("SanctuaryUI", "PanelArt", false,
                "Dress the bottom panels in the game's own dashed panel art, as its panels are, instead of the HUD's plain plate with its one outline. Off reads cleaner.");
        }

        /// One row of tiles, plate included: the height every piece on the
        /// baseline shares.
        internal const float Pad = 12f;
        internal const float TileHeight = 104f;
        internal const float RowHeight = TileHeight + Pad * 2f;

        /// The bottom-left corner the whole thing grows from, in canvas
        /// units: where the game's own orders panel sits.
        internal static Vector2 Origin { get; private set; } = new Vector2(28f, 28f);

        /// How wide the left column is this frame: the wider of the orders
        /// row and the card, 0 while neither shows.
        internal static float ColumnWidth { get; private set; }

        /// How tall the orders row is this frame, 0 while it doesn't show.
        internal static float OrdersHeight { get; private set; }

        internal static float ColumnRight => Origin.x + ColumnWidth;

        private static readonly List<Rect> _pieces = new List<Rect>();
        private static readonly List<Image> _lines = new List<Image>();
        private static int _linesUsed;

        /// From Update, before the pieces tick: a fresh frame.
        internal static void Begin()
        {
            _pieces.Clear();
            // Lines from a match that has ended went with its scene.
            _lines.RemoveAll(line => line == null);
            ColumnWidth = 0f;
            OrdersHeight = 0f;
            try
            {
                var ui = SanctuaryUIManager.Instance;
                if (ui != null && ui.TryGetPanel(UIPanelType.Orders, out var orders) && HudCanvas.LocalRect(orders, out var rect))
                    Origin = new Vector2(rect.x, rect.y);
            }
            catch { /* the last origin stands */ }
        }

        /// A piece of the left column, placed: the column grows to fit it.
        internal static void Column(float width, float ordersHeight = -1f)
        {
            if (width > ColumnWidth) ColumnWidth = width;
            if (ordersHeight >= 0f) OrdersHeight = ordersHeight;
        }

        /// A piece that showed this frame, where it was put (canvas units,
        /// y up, bottom-left origin).
        internal static void Add(Rect rect)
        {
            if (rect.width > 1f && rect.height > 1f) _pieces.Add(rect);
        }

        /// From Update, after the pieces: the outline.
        internal static void End()
        {
            _linesUsed = 0;
            var root = HudCanvas.Root;
            if (root != null && (PanelArt == null || !PanelArt.Value)) Outline(root);
            for (var i = _linesUsed; i < _lines.Count; i++)
                if (_lines[i] != null && _lines[i].gameObject.activeSelf) _lines[i].gameObject.SetActive(false);
        }

        internal static void Shutdown()
        {
            foreach (var line in _lines) if (line != null) Object.Destroy(line.gameObject);
            _lines.Clear();
            _linesUsed = 0;
        }

        // ---- the outline ---------------------------------------------------------

        private const float Line = 2f;
        private const float Touch = 1.5f;

        private static void Outline(RectTransform root)
        {
            var accent = AccentColour;
            var top = accent;
            top.a = 0.6f;
            var side = accent;
            side.a = 0.35f;
            var divider = accent;
            divider.a = 0.35f;

            for (var i = 0; i < _pieces.Count; i++)
            {
                var r = _pieces[i];
                // The top edge, less whatever sits on it.
                foreach (var seg in Exposed(r.xMin, r.xMax, i, above: true))
                    Put(root, seg.x, r.yMax - Line, seg.y - seg.x, Line, top);
                // The sides, less whatever stands beside them.
                foreach (var seg in ExposedSide(r, i, left: true))
                    Put(root, r.xMin, seg.x, Line, seg.y - seg.x, side);
                foreach (var seg in ExposedSide(r, i, left: false))
                    Put(root, r.xMax - Line, seg.x, Line, seg.y - seg.x, side);
                // A divider where another piece stands against the right edge.
                for (var j = 0; j < _pieces.Count; j++)
                {
                    if (j == i) continue;
                    var o = _pieces[j];
                    if (Mathf.Abs(o.xMin - r.xMax) > Touch) continue;
                    var y0 = Mathf.Max(r.yMin, o.yMin) + Pad;
                    var y1 = Mathf.Min(r.yMax, o.yMax) - Pad;
                    if (y1 > y0) Put(root, r.xMax - Line / 2f, y0, Line, y1 - y0, divider);
                }
            }
        }

        private static readonly List<Vector2> _segments = new List<Vector2>();

        /// The parts of [x0, x1] along piece i's top not covered by a piece
        /// standing on it.
        private static List<Vector2> Exposed(float x0, float x1, int i, bool above)
        {
            _segments.Clear();
            _segments.Add(new Vector2(x0, x1));
            var r = _pieces[i];
            for (var j = 0; j < _pieces.Count; j++)
            {
                if (j == i) continue;
                var o = _pieces[j];
                if (Mathf.Abs(o.yMin - r.yMax) > Touch) continue;
                Subtract(_segments, o.xMin, o.xMax);
            }
            return _segments;
        }

        private static readonly List<Vector2> _sideSegments = new List<Vector2>();

        /// The parts of piece i's left or right edge with nothing against them.
        private static List<Vector2> ExposedSide(Rect r, int i, bool left)
        {
            _sideSegments.Clear();
            _sideSegments.Add(new Vector2(r.yMin, r.yMax));
            var edge = left ? r.xMin : r.xMax;
            for (var j = 0; j < _pieces.Count; j++)
            {
                if (j == i) continue;
                var o = _pieces[j];
                var other = left ? o.xMax : o.xMin;
                if (Mathf.Abs(other - edge) > Touch) continue;
                Subtract(_sideSegments, o.yMin, o.yMax);
            }
            return _sideSegments;
        }

        private static void Subtract(List<Vector2> segments, float a, float b)
        {
            for (var k = segments.Count - 1; k >= 0; k--)
            {
                var s = segments[k];
                if (b <= s.x || a >= s.y) continue;
                segments.RemoveAt(k);
                if (a > s.x) segments.Insert(k, new Vector2(s.x, a));
                if (b < s.y) segments.Insert(k, new Vector2(b, s.y));
            }
        }

        private static void Put(RectTransform root, float x, float y, float w, float h, Color colour)
        {
            if (w <= 0.5f || h <= 0.5f) return;
            while (_linesUsed >= _lines.Count)
            {
                var line = HudCanvas.Fill(root, "Outline", colour);
                var rt = line.rectTransform;
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                _lines.Add(line);
            }
            var image = _lines[_linesUsed++];
            if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
            image.color = colour;
            image.rectTransform.anchoredPosition = new Vector2(x, y);
            image.rectTransform.sizeDelta = new Vector2(w, h);
            // Over the plates, which were made before the lines.
            image.transform.SetAsLastSibling();
        }
    }
}
