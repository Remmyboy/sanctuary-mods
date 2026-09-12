using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The fog, so an empty patch of map reads as "nothing there" rather than
    // "nothing known". Without it the mini-map is quietly misleading: the
    // clearest part of the map is the part nobody has looked at.
    //
    // The whole map is shaded, and the units you share intel with lift the
    // shade around themselves.
    //
    // The obvious source for this is the game's own fog buffer — FowPass
    // renders the focused army's intel into a render texture and publishes it
    // as the global _FowBuffer — and that was the first implementation. It is
    // wrong, and instructively so: the pass builds its renderer list from
    // `ctx.cullingResults` of the *main* camera, and only overrides the
    // matrices to the map-wide orthographic view. So the vision volumes drawn
    // into it are culled to whatever the player is currently looking at, and
    // zooming in erases vision from the rest of the map. On a mini-map that
    // showed fog sitting over the player's own base.
    //
    // Coverage is therefore computed here instead, from the vision radius each
    // unit carries on its template. It is an approximation — it takes no
    // account of terrain blocking sight — but it is view-independent, costs no
    // GPU work at all, and is the same circle the game's own intel is built
    // from.
    internal static class FogOverlay
    {
        /// The mask to draw over the map, or null when there is no fog to
        /// show.
        internal static Texture2D Mask { get; private set; }

        /// Coarse on purpose: it is stretched over a few hundred pixels and
        /// filtered, and vision circles are soft-edged anyway.
        private const int Size = 128;

        private static Color32[] _pixels;
        private static byte[] _shade;
        private static float _nextBuild;

        /// Rebuilds the mask from the contacts' own vision. Pure CPU, so
        /// unlike the render-texture version this is safe to call from Update.
        internal static void Rebuild(bool wanted, float darkness)
        {
            if (!wanted || !InMatch || !MapSurface.Ready || !Contacts.FogApplies)
            {
                if (Mask != null) Release();
                return;
            }

            if (Time.unscaledTime < _nextBuild) return;
            _nextBuild = Time.unscaledTime + 0.2f;

            var width = MapSurface.Width;
            var length = MapSurface.Length;
            if (width <= 0f || length <= 0f) return;

            if (Mask == null)
                Mask = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            if (_pixels == null || _pixels.Length != Size * Size) _pixels = new Color32[Size * Size];
            if (_shade == null || _shade.Length != Size * Size) _shade = new byte[Size * Size];

            // Dark everywhere, then vision lifts it. Seen ground keeps a light
            // tint rather than going fully clear, so the map still reads as a
            // map rather than as two separate pictures.
            var dark = (byte)Mathf.Clamp(darkness * 255f, 0f, 255f);
            var lit = (byte)(dark * 0.28f);
            for (var i = 0; i < _shade.Length; i++) _shade[i] = dark;

            var contacts = Contacts.Live;
            if (contacts != null)
            {
                // A Texture2D is addressed from the bottom up and world z runs
                // up the map the same way, so row 0 is z = 0 and no flip is
                // needed here. Per-axis radii, because a map need not be square.
                for (var c = 0; c < contacts.Count; c++)
                {
                    var contact = contacts[c];
                    if (contact.VisionRadius <= 0f) continue;

                    var cx = contact.X / width * Size;
                    var cy = contact.Z / length * Size;
                    var rx = Mathf.Max(1f, contact.VisionRadius / width * Size);
                    var ry = Mathf.Max(1f, contact.VisionRadius / length * Size);

                    var x0 = Mathf.Max(0, Mathf.FloorToInt(cx - rx));
                    var x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(cx + rx));
                    var y0 = Mathf.Max(0, Mathf.FloorToInt(cy - ry));
                    var y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(cy + ry));

                    for (var y = y0; y <= y1; y++)
                    {
                        var dy = (y + 0.5f - cy) / ry;
                        var row = y * Size;
                        for (var x = x0; x <= x1; x++)
                        {
                            var dx = (x + 0.5f - cx) / rx;
                            var d = dx * dx + dy * dy;
                            if (d > 1f) continue;
                            // Soft only at the very edge, so overlapping
                            // circles read as one lit area rather than a
                            // string of blobs.
                            var t = Mathf.InverseLerp(0.7f, 1f, Mathf.Sqrt(d));
                            var value = (byte)Mathf.Lerp(lit, dark, t);
                            if (value < _shade[row + x]) _shade[row + x] = value;
                        }
                    }
                }
            }

            for (var i = 0; i < _shade.Length; i++) _pixels[i] = new Color32(0, 0, 0, _shade[i]);
            Mask.SetPixels32(_pixels);
            Mask.Apply(false);
        }

        internal static void Release()
        {
            if (Mask != null) UnityEngine.Object.Destroy(Mask);
            Mask = null;
            _pixels = null;
            _shade = null;
            _nextBuild = 0f;
        }
    }
}
