using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The HUD's finish: what lifts a flat plate off the map. Plates get a
    // shade that darkens towards the bottom, a faint edge and a soft shadow
    // round them, bars a shaded fill, an alert a light sweeping across it
    // as it arrives, and a hovered tile a glow. The sprites are drawn here,
    // once, in code, so nothing ships as art.
    //
    // No text underlay: TextMeshPro's UNDERLAY_ON switches on cleanly on the
    // game's font materials, but nothing draws in game (2026-10-02), so the
    // variant is presumably stripped from the build.
    internal static class HudStyle
    {
        // ---- sprites ---------------------------------------------------------------

        private static Sprite _shade, _shadeLight, _frame, _ring, _barShade, _sheen;

        /// What a plate is tinted: a blue a little lighter than the game's
        /// panel colour, which the shade then darkens to well below it at
        /// the bottom, so a plate reads lit from above.
        internal static readonly Color PlateTint = new Color(0.125f, 0.2f, 0.3f, 0.94f);

        /// Corner radius and slice border of the plate sprites, in pixels
        /// (canvas units at their native size).
        private const int Corner = 5, Slice = 8;

        /// How far a line along a plate's edge stops short of its corners.
        internal const float CornerInset = Corner;

        /// A plate: a rounded rectangle, white at the top down to 45% at the
        /// bottom, 9-sliced so it fits any size and keeps its corners.
        internal static Sprite Shade => _shade != null ? _shade : (_shade = Rounded((inside, edge, t) => inside
            ? Color.Lerp(new Color(0.45f, 0.45f, 0.45f, 1f), Color.white, t) : Color.clear));

        /// The same plate for a light card: only down to 88%, so white stays
        /// white enough for dark type.
        internal static Sprite ShadeLight => _shadeLight != null ? _shadeLight : (_shadeLight = Rounded((inside, edge, t) => inside
            ? Color.Lerp(new Color(0.88f, 0.88f, 0.88f, 1f), Color.white, t) : Color.clear));

        /// The edge of a plate: the same rounded rectangle's outline, one
        /// pixel wide, for a faint line of the accent round it.
        internal static Sprite Frame => _frame != null ? _frame : (_frame = Rounded((inside, edge, t) => edge ? Color.white : Color.clear));

        /// A 9-sliced rounded rectangle, 32 by 64, coloured by colour(inside,
        /// on the edge, height 0 at the bottom to 1 at the top) with its
        /// corners smoothed.
        private static Sprite Rounded(System.Func<bool, bool, float, Color> colour)
        {
            const int w = 32, h = 64;
            var texture = NewTexture(w, h);
            var pixels = new Color[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    // Distance from the rounded rectangle's edge, inside > 0.
                    float cx = Mathf.Clamp(x + 0.5f, Corner, w - Corner), cy = Mathf.Clamp(y + 0.5f, Corner, h - Corner);
                    var inCorner = (x + 0.5f < Corner || x + 0.5f > w - Corner) && (y + 0.5f < Corner || y + 0.5f > h - Corner);
                    var d = inCorner
                        ? Corner - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy))
                        : Mathf.Min(Mathf.Min(x + 0.5f, w - x - 0.5f), Mathf.Min(y + 0.5f, h - y - 0.5f));
                    var cover = Mathf.Clamp01(d + 0.5f);
                    var col = colour(cover > 0f, d < 1.5f, y / (float)(h - 1));
                    col.a *= cover;
                    pixels[y * w + x] = col;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(Slice, Slice, Slice, Slice));
            Generated.Keep(sprite);
            return sprite;
        }

        /// A bar's fill: 72% at the left up to white at the right, so a fill
        /// brightens towards where it ends.
        internal static Sprite BarShade => _barShade != null ? _barShade : (_barShade = Gradient(32, 1, t => Color.Lerp(new Color(0.72f, 0.72f, 0.72f, 1f), Color.white, t)));

        /// A soft band of light, clear at both ends: the sweep across an alert.
        internal static Sprite Sheen => _sheen != null ? _sheen : (_sheen = Gradient(32, 1, t => new Color(1f, 1f, 1f, Mathf.Sin(t * Mathf.PI) * Mathf.Sin(t * Mathf.PI))));

        /// Corner size of the ring sprite, in canvas units at its native size.
        internal const float RingSize = 24f;

        /// A ring of soft dark (or, tinted, of glow) that falls away outside
        /// a rectangle and is clear inside it, 9-sliced so it fits any plate:
        /// stretched RingSize past each edge, it shades round the plate and
        /// never over it.
        internal static Sprite Ring
        {
            get
            {
                if (_ring != null) return _ring;
                const int size = 64, border = 24;
                var texture = NewTexture(size, size);
                var pixels = new Color[size * size];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        // Distance outside the clear middle, in pixels.
                        var dx = Mathf.Max(0f, Mathf.Max(border - 0.5f - x, x + 0.5f - (size - border)));
                        var dy = Mathf.Max(0f, Mathf.Max(border - 0.5f - y, y + 0.5f - (size - border)));
                        var d = Mathf.Sqrt(dx * dx + dy * dy) / border;
                        var a = d <= 0f ? 0f : Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                        pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                    }
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                _ring = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect, new Vector4(border, border, border, border));
                Generated.Keep(_ring);
                return _ring;
            }
        }

        // ---- icons ------------------------------------------------------------------

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _icons = new System.Collections.Generic.Dictionary<string, Sprite>();

        /// After Generated.DestroyAll: the sprites above are made again on demand.
        internal static void Released() => _icons.Clear();

        /// One of the HUD's own white icons, 64 pixels square, to tint:
        /// "menu" (three lines), "ring" (a circle, for a glyph to sit in) or
        /// "grip" (three diagonal strokes in the bottom-right corner, for a
        /// resize handle). Null for a name it doesn't draw.
        internal static Sprite Icon(string name)
        {
            if (_icons.TryGetValue(name, out var found) && found != null) return found;
            System.Func<float, float, float> shape;
            switch (name)
            {
                case "menu":
                    shape = (x, y) => Mathf.Min(Box(x, y, 32f, 21f, 17f, 3f, 3f), Mathf.Min(Box(x, y, 32f, 32f, 17f, 3f, 3f), Box(x, y, 32f, 43f, 17f, 3f, 3f)));
                    break;
                case "grip":
                    shape = (x, y) =>
                    {
                        var along = (64f - x) + y;
                        var d = float.MaxValue;
                        foreach (var at in new[] { 16f, 32f, 48f }) d = Mathf.Min(d, Mathf.Abs(along - at) / 1.4142f - 3f);
                        return d;
                    };
                    break;
                case "ring":
                    shape = (x, y) => Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(32f, 32f)) - 22f) - 2.5f;
                    break;
                default:
                    return null;
            }
            const int size = 64;
            var texture = NewTexture(size, size);
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - shape(x + 0.5f, y + 0.5f)));
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            Generated.Keep(sprite);
            _icons[name] = sprite;
            return sprite;
        }

        /// Signed distance, in pixels, from (x, y) to a rounded box centred at
        /// (cx, cy) with half-sizes hx, hy and corner radius r: below 0 inside.
        private static float Box(float x, float y, float cx, float cy, float hx, float hy, float r)
        {
            var qx = Mathf.Abs(x - cx) - hx + r;
            var qy = Mathf.Abs(y - cy) - hy + r;
            return Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f)) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        private static Texture2D NewTexture(int w, int h) =>
            Generated.Keep(new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });

        /// A w by h sprite whose colour runs along its long side, from
        /// colour(0) at the left or bottom to colour(1) at the right or top.
        private static Sprite Gradient(int w, int h, System.Func<float, Color> colour)
        {
            var texture = NewTexture(w, h);
            var pixels = new Color[w * h];
            var along = Mathf.Max(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = w >= h ? x : y;
                    pixels[y * w + x] = colour(along > 1 ? i / (float)(along - 1) : 1f);
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            Generated.Keep(sprite);
            return sprite;
        }

        // ---- plates ----------------------------------------------------------------

        /// Makes an Image a plate: the shaded rounded sprite in the plate's
        /// tint, a faint accent edge, and a soft shadow round it. The edge
        /// and the shadow are children that take no part in the plate's
        /// layout and catch no clicks; the shadow is clear inside the plate,
        /// so it never dims what is on it.
        internal static void Dress(Image plate, float shadow = 0.8f, bool edge = true)
        {
            if (plate == null) return;
            plate.sprite = Shade;
            plate.type = Image.Type.Sliced;
            plate.color = PlateTint;
            if (edge && plate.transform.Find("Edge") == null)
            {
                var go = new GameObject("Edge", typeof(RectTransform));
                go.transform.SetParent(plate.transform, false);
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                var line = go.AddComponent<Image>();
                line.sprite = Frame;
                line.type = Image.Type.Sliced;
                var accent = AccentColour;
                accent.a = 0.3f;
                line.color = accent;
                line.raycastTarget = false;
                var rt = line.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            if (shadow > 0f) Shadow(plate.rectTransform, shadow);
        }

        /// The soft shadow round a rectangle, as a child named "Shadow".
        internal static Image Shadow(RectTransform around, float strength)
        {
            var found = around.Find("Shadow");
            if (found != null) return found.GetComponent<Image>();
            var go = new GameObject("Shadow", typeof(RectTransform));
            go.transform.SetParent(around, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var image = go.AddComponent<Image>();
            image.sprite = Ring;
            image.type = Image.Type.Sliced;
            image.color = new Color(0f, 0f, 0f, strength);
            image.raycastTarget = false;
            var rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-RingSize, -RingSize);
            rt.offsetMax = new Vector2(RingSize, RingSize);
            return image;
        }

        /// Gives a bar's fill its shade.
        internal static void ShadeBar(Image fill)
        {
            if (fill == null) return;
            fill.sprite = BarShade;
            fill.type = Image.Type.Simple;
        }

        // ---- motion ----------------------------------------------------------------

        /// Eases in towards 1 and settles with a slight overshoot, for things
        /// that arrive.
        internal static float Overshoot(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            const float s = 1.6f;
            return t * t * ((s + 1f) * t + s) + 1f;
        }

        /// A pulse between 0 and 1, for something that needs attention now.
        internal static float Pulse(float rate = 6f) => 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * rate);
    }

    /// A band of light that crosses its parent once each time Play is
    /// called, clipped to the parent by a layer of its own (so the parent's
    /// shadow, outside it, is left alone).
    internal sealed class Sweep : MonoBehaviour
    {
        private RectTransform _band, _parent;
        private Image _image;
        private float _started = -1f;
        private const float Duration = 0.7f, Width = 90f;

        internal static Sweep Add(RectTransform parent, float strength = 0.22f)
        {
            var clip = new GameObject("Sweep clip", typeof(RectTransform));
            clip.transform.SetParent(parent, false);
            clip.AddComponent<LayoutElement>().ignoreLayout = true;
            clip.AddComponent<RectMask2D>();
            var crt = (RectTransform)clip.transform;
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = Vector2.zero;
            var go = new GameObject("Sweep", typeof(RectTransform));
            go.transform.SetParent(crt, false);
            var sweep = go.AddComponent<Sweep>();
            sweep._parent = parent;
            sweep._image = go.AddComponent<Image>();
            sweep._image.sprite = HudStyle.Sheen;
            sweep._image.color = new Color(1f, 1f, 1f, strength);
            sweep._image.raycastTarget = false;
            sweep._band = (RectTransform)go.transform;
            sweep._band.anchorMin = new Vector2(0f, 0f);
            sweep._band.anchorMax = new Vector2(0f, 1f);
            sweep._band.pivot = new Vector2(0f, 0.5f);
            sweep._band.sizeDelta = new Vector2(Width, 0f);
            sweep._band.localRotation = Quaternion.Euler(0f, 0f, -12f);
            go.SetActive(false);
            return sweep;
        }

        internal void Play()
        {
            _started = Time.unscaledTime;
            gameObject.SetActive(true);
        }

        private void Update()
        {
            if (_started < 0f) return;
            var t = (Time.unscaledTime - _started) / Duration;
            if (t >= 1f)
            {
                _started = -1f;
                gameObject.SetActive(false);
                return;
            }
            var eased = 1f - (1f - t) * (1f - t);
            _band.anchoredPosition = new Vector2(Mathf.Lerp(-Width * 1.5f, _parent.rect.width + Width * 0.5f, eased), 0f);
        }
    }

    /// A soft glow in the accent colour round a tile while the mouse is over
    /// it, fading in and out. Hover only: it changes nothing a click does.
    internal sealed class HoverGlow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private Image _glow;
        private bool _over;
        private float _level;

        /// When set, the glow shows only while this says the tile does
        /// something on a click.
        internal System.Func<bool> When;

        internal static HoverGlow Add(GameObject tile)
        {
            var glow = tile.GetComponent<HoverGlow>();
            if (glow != null) return glow;
            glow = tile.AddComponent<HoverGlow>();
            var go = new GameObject("Glow", typeof(RectTransform));
            go.transform.SetParent(tile.transform, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            glow._glow = go.AddComponent<Image>();
            glow._glow.sprite = HudStyle.Ring;
            glow._glow.type = Image.Type.Sliced;
            // The ring at half size, half its width past the edge: a tight
            // glow, not a halo, and clear over the tile itself.
            glow._glow.pixelsPerUnitMultiplier = 2f;
            glow._glow.raycastTarget = false;
            var rt = glow._glow.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-HudStyle.RingSize * 0.5f, -HudStyle.RingSize * 0.5f);
            rt.offsetMax = new Vector2(HudStyle.RingSize * 0.5f, HudStyle.RingSize * 0.5f);
            rt.localScale = Vector3.one;
            glow.Apply();
            return glow;
        }

        public void OnPointerEnter(PointerEventData eventData) => _over = When == null || When();
        public void OnPointerExit(PointerEventData eventData) => _over = false;

        private void OnDisable()
        {
            _over = false;
            _level = 0f;
            Apply();
        }

        private void Update()
        {
            var target = _over ? 1f : 0f;
            if (Mathf.Approximately(_level, target)) return;
            _level = Mathf.MoveTowards(_level, target, Time.unscaledDeltaTime / 0.12f);
            Apply();
        }

        private void Apply()
        {
            if (_glow == null) return;
            var c = AccentColour;
            c.a = 0.9f * _level;
            _glow.color = c;
            var on = _level > 0.001f;
            if (_glow.enabled != on) _glow.enabled = on;
        }
    }
}
