using System;
using System.Collections.Generic;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The HUD's look for the panels still drawn with IMGUI (Camera Utilities'
    // F4 panel, the replay controls): the same rounded plate as the canvas
    // panels, lit from above with a faint accent edge, buttons in the same
    // shape, the accent blue for anything switched on, and the closest font
    // to the game's that IMGUI can use. IMGUI can't take the game's own
    // TextMeshPro typeface or a sprite, so the textures are baked here with
    // their colours in, for a style's border to 9-slice.
    internal static class HudImgui
    {
        private const int Corner = 6, Size = 32, Tall = 64;

        /// What a 9-sliced texture from here wants as its style's border.
        internal static RectOffset Border => new RectOffset(Corner + 2, Corner + 2, Corner + 2, Corner + 2);

        private static Texture2D _panel, _pill, _button, _buttonHover, _on, _onHover, _tint, _tintHover;

        /// The panel plate: the canvas plates' tint, darkening to 45% at the
        /// bottom, with a faint accent edge.
        internal static Texture2D Panel => _panel != null ? _panel : (_panel = Rounded(Tall, t =>
        {
            var c = HudStyle.PlateTint;
            var k = Mathf.Lerp(0.45f, 1f, t);
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }, WithAlpha(AccentColour, 0.3f)));

        /// A button at rest and under the mouse: a faint light plate.
        internal static Texture2D Button => _button != null ? _button : (_button = Rounded(Size, t => new Color(1f, 1f, 1f, Mathf.Lerp(0.06f, 0.1f, t)), new Color(1f, 1f, 1f, 0.12f)));
        internal static Texture2D ButtonHover => _buttonHover != null ? _buttonHover : (_buttonHover = Rounded(Size, t => new Color(1f, 1f, 1f, Mathf.Lerp(0.13f, 0.2f, t)), new Color(1f, 1f, 1f, 0.3f)));

        /// A button switched on: the accent, lit from above.
        internal static Texture2D On => _on != null ? _on : (_on = Rounded(Size, t => Color.Lerp(Darker(AccentColour, 0.78f), AccentColour, t), Lighter(AccentColour, 0.35f)));
        internal static Texture2D OnHover => _onHover != null ? _onHover : (_onHover = Rounded(Size, t => Color.Lerp(AccentColour, Lighter(AccentColour, 0.25f), t), Lighter(AccentColour, 0.55f)));

        /// The same shape in white, lit from above, for a caller that tints
        /// it at draw time with GUI.backgroundColor (an army's own colour).
        internal static Texture2D Tint => _tint != null ? _tint : (_tint = Rounded(Size, t => new Color(Mathf.Lerp(0.8f, 1f, t), Mathf.Lerp(0.8f, 1f, t), Mathf.Lerp(0.8f, 1f, t), 1f), Color.white));
        internal static Texture2D TintHover => _tintHover != null ? _tintHover : (_tintHover = Rounded(Size, t => Color.white, Color.white));

        /// A label's backing over the map: a dark rounded pill, lit faintly
        /// from above, with a faint light edge so it holds over dark ground too.
        internal static Texture2D Pill => _pill != null ? _pill : (_pill = Rounded(Size, t => new Color(Mathf.Lerp(0.02f, 0.07f, t), Mathf.Lerp(0.03f, 0.1f, t), Mathf.Lerp(0.05f, 0.15f, t), 0.78f), new Color(1f, 1f, 1f, 0.14f)));

        private static GUIStyle _pillStyle;

        /// A style that draws Pill 9-sliced, for GUI.Box.
        internal static GUIStyle PillStyle => _pillStyle ?? (_pillStyle = new GUIStyle { normal = { background = Pill }, border = Border });

        /// Type on an accent button: the HUD's darkest blue.
        internal static readonly Color OnText = new Color(0.02f, 0.07f, 0.12f);

        private static Color WithAlpha(Color c, float a) { c.a = a; return c; }
        private static Color Darker(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);
        private static Color Lighter(Color c, float k) => Color.Lerp(c, Color.white, k);

        /// A Size-wide rounded rectangle, height tall, filled by fill(height
        /// 0 at the bottom to 1 at the top) with a one-pixel edge, its
        /// corners smoothed. Row 0 is the bottom, which IMGUI draws at the
        /// bottom, so the top is lit.
        private static Texture2D Rounded(int height, Func<float, Color> fill, Color edge)
        {
            var texture = new Texture2D(Size, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[Size * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < Size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float cx = Mathf.Clamp(px, Corner, Size - Corner), cy = Mathf.Clamp(py, Corner, height - Corner);
                    var inCorner = (px < Corner || px > Size - Corner) && (py < Corner || py > height - Corner);
                    var d = inCorner
                        ? Corner - Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy))
                        : Mathf.Min(Mathf.Min(px, Size - px), Mathf.Min(py, height - py));
                    var cover = Mathf.Clamp01(d + 0.5f);
                    var colour = d < 1.5f ? Color.Lerp(fill(y / (float)(height - 1)), edge, edge.a) : fill(y / (float)(height - 1));
                    if (d < 1.5f) colour.a = Mathf.Max(colour.a, edge.a);
                    colour.a *= cover;
                    pixels[y * Size + x] = colour;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // ---- type ------------------------------------------------------------------

        private static Font _font;
        private static bool _fontTried;

        /// The closest installed face to the game's (Rajdhani, a condensed
        /// squared-off sans; Bahnschrift is the same cut and on every
        /// Windows 10/11), or null for Unity's default.
        internal static Font Font
        {
            get
            {
                if (_fontTried) return _font;
                _fontTried = true;
                try
                {
                    var installed = new HashSet<string>(UnityEngine.Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
                    foreach (var name in new[] { "Rajdhani SemiBold", "Rajdhani", "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" })
                    {
                        if (!installed.Contains(name)) continue;
                        _font = UnityEngine.Font.CreateDynamicFontFromOSFont(name, 16);
                        if (_font == null) continue;
                        _font.hideFlags = HideFlags.HideAndDontSave;
                        break;
                    }
                }
                catch (Exception e)
                {
                    _log?.LogInfo($"HUD IMGUI font lookup failed ({e.Message}); using the default.");
                }
                return _font;
            }
        }

        /// Puts the HUD's IMGUI font on each style.
        internal static void UseFont(params GUIStyle[] styles)
        {
            var font = Font;
            if (font == null) return;
            foreach (var style in styles)
                if (style != null) style.font = font;
        }
    }
}
