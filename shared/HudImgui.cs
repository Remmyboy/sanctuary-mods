using System;
using System.Collections.Generic;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // What is still drawn with IMGUI (the map's reclaim and wreck labels):
    // the label pill, baked with its colours in for a style's border to
    // 9-slice, and the closest font to the game's that IMGUI or an OS-font
    // fallback can use.
    internal static class HudImgui
    {
        private const int Corner = 6, Size = 32;

        /// What a 9-sliced texture from here wants as its style's border.
        internal static RectOffset Border => new RectOffset(Corner + 2, Corner + 2, Corner + 2, Corner + 2);

        private static Texture2D _pill;

        /// A label's backing over the map: a dark rounded pill, lit faintly
        /// from above, with a faint light edge so it holds over dark ground too.
        internal static Texture2D Pill => _pill != null ? _pill : (_pill = Rounded(Size, t => new Color(Mathf.Lerp(0.02f, 0.07f, t), Mathf.Lerp(0.03f, 0.1f, t), Mathf.Lerp(0.05f, 0.15f, t), 0.78f), new Color(1f, 1f, 1f, 0.14f)));

        private static GUIStyle _pillStyle;

        /// A style that draws Pill 9-sliced, for GUI.Box.
        internal static GUIStyle PillStyle => _pillStyle != null && _pillStyle.normal.background != null
            ? _pillStyle
            : (_pillStyle = new GUIStyle { normal = { background = Pill }, border = Border });

        /// After Generated.DestroyAll: the font is gone, look again on demand.
        internal static void Released()
        {
            _font = null;
            _fontTried = false;
        }


        /// A Size-wide rounded rectangle, height tall, filled by fill(height
        /// 0 at the bottom to 1 at the top) with a one-pixel edge, its
        /// corners smoothed. Row 0 is the bottom, which IMGUI draws at the
        /// bottom, so the top is lit.
        private static Texture2D Rounded(int height, Func<float, Color> fill, Color edge)
        {
            var texture = Generated.Keep(new Texture2D(Size, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            });
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
                        Generated.Keep(_font);
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
    }
}
