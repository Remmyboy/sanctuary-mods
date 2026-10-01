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
    // The whole is sized together, too: one grip, in the top-right corner of
    // the piece furthest right along the baseline, scales every piece from
    // the dock's bottom-left corner.
    internal static class BottomDock
    {
        private static ConfigEntry<float> _cfgScale;
        private static ConfigEntry<bool> _cfgLocked;

        private const float MinScale = 0.5f, MaxScale = 2f;

        internal static void Bind(ConfigFile config)
        {
            _cfgScale = config.Bind("BottomPanels", "Scale", 1f,
                new ConfigDescription("Size of the bottom panels together — the orders row, the unit card, the selection row and the build strip — " +
                    "on top of the HUD's own Scale. Dragging the grip in the top-right corner of the panels sets this too.",
                    new AcceptableValueRange<float>(MinScale, MaxScale)));
            _cfgLocked = config.Bind("BottomPanels", "Locked", false,
                "Keep the bottom panels the size they are: no resize grip.");
        }

        /// The live size, so a resize drag doesn't write the config file on
        /// every frame of it; stored when the drag ends.
        private static float _size = 1f;
        private static HudPanel.PanelGrip _grip;

        /// The size every piece is drawn at this frame: the HUD's, times
        /// the dock's own.
        internal static float Scale => SanctuaryHudPlugin.HudScale * _size;

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
        private static readonly List<RectTransform> _plates = new List<RectTransform>();
        private static readonly List<Image> _lines = new List<Image>();
        private static int _linesUsed;

        /// From Update, before the pieces tick: a fresh frame.
        internal static void Begin()
        {
            if (_grip != null && _grip.TakeResized() is float resized) _cfgScale.Value = resized;
            if (_grip == null || !_grip.Resizing) _size = Mathf.Clamp(_cfgScale.Value, MinScale, MaxScale);
            _pieces.Clear();
            _plates.Clear();
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
        /// y up, bottom-left origin), and its plate.
        internal static void Add(Rect rect, RectTransform plate)
        {
            if (rect.width <= 1f || rect.height <= 1f) return;
            _pieces.Add(rect);
            _plates.Add(plate);
        }

        /// From Update, after the pieces: the outline and the grip.
        internal static void End()
        {
            _linesUsed = 0;
            var root = HudCanvas.Root;
            if (root != null) Outline(root);
            for (var i = _linesUsed; i < _lines.Count; i++)
                if (_lines[i] != null && _lines[i].gameObject.activeSelf) _lines[i].gameObject.SetActive(false);
            PlaceGrip();
        }

        internal static void Shutdown()
        {
            foreach (var line in _lines) if (line != null) Object.Destroy(line.gameObject);
            _lines.Clear();
            _linesUsed = 0;
            if (_grip != null) Object.Destroy(_grip.gameObject);
            _grip = null;
        }

        // ---- the grip ------------------------------------------------------------

        /// In the top-right corner of the piece furthest right on the
        /// baseline: the corner the whole grows towards.
        private static void PlaceGrip()
        {
            RectTransform plate = null;
            var right = float.MinValue;
            for (var i = 0; i < _pieces.Count; i++)
            {
                var r = _pieces[i];
                if (_plates[i] == null || Mathf.Abs(r.yMin - Origin.y) > Touch || r.xMax <= right) continue;
                right = r.xMax;
                plate = _plates[i];
            }
            if (plate == null)
            {
                if (_grip != null && _grip.gameObject.activeSelf) _grip.gameObject.SetActive(false);
                return;
            }
            if (_grip == null)
            {
                _grip = HudPanel.PanelGrip.Create(plate, null, MinScale, MaxScale, () => _cfgLocked.Value);
                _grip.Get = () => _size;
                _grip.Set = size => _size = size;
                _grip.Anchor = () =>
                {
                    var root = HudCanvas.Root;
                    return root != null ? root.TransformPoint(root.rect.min + Origin) : Vector3.zero;
                };
            }
            else if (_grip.transform.parent != plate) _grip.transform.SetParent(plate, false);
            _grip.Place(new Vector2(1f, 1f));
            if (!_grip.gameObject.activeSelf) _grip.gameObject.SetActive(true);
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
            // Over the plates, which were made before the lines. Only when
            // something else has come after the lines: moving one every
            // frame would re-batch the whole canvas every frame.
            var t = image.transform;
            if (t.GetSiblingIndex() < t.parent.childCount - _lines.Count) t.SetAsLastSibling();
        }
    }
}
