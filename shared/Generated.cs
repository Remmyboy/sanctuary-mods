using System.Collections.Generic;
using UnityEngine;

namespace SanctuaryHud
{
    // Textures, sprites and fonts a mod makes at run time. HideAndDontSave
    // keeps them out of the scene (and alive across loads), so nothing frees
    // them: a hot reload would leave every copy behind. Made through Keep,
    // they go with the mod's HudCanvas.Destroy. Caches holding them see
    // Unity's destroyed-is-null and make them again if asked.
    internal static class Generated
    {
        private static readonly List<Object> _made = new List<Object>();

        internal static T Keep<T>(T made) where T : Object
        {
            if (made == null) return null;
            made.hideFlags = HideFlags.HideAndDontSave;
            _made.Add(made);
            return made;
        }

        private static System.Reflection.MethodInfo _loadImage;
        private static bool _loadImageTried;

        /// Texture2D.LoadImage: decodes a PNG or JPG into the texture, false
        /// when it won't decode. It lives in UnityEngine.ImageConversionModule,
        /// built against netstandard 2.1 (the same wall the audio module is
        /// behind), so it is reached by reflection rather than referenced.
        internal static bool LoadImage(Texture2D texture, byte[] bytes, bool markNonReadable = false)
        {
            if (!_loadImageTried)
            {
                _loadImageTried = true;
                _loadImage = System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
                    ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                if (_loadImage == null) HudCore._log?.LogWarning("ImageConversion.LoadImage not found: images won't load.");
            }
            return _loadImage != null && texture != null && bytes != null &&
                   _loadImage.Invoke(null, new object[] { texture, bytes, markNonReadable }) is bool ok && ok;
        }

        /// A PNG as a kept texture (gone with DestroyAll), or null.
        internal static Texture2D DecodePng(byte[] bytes, string name = null, bool markNonReadable = false)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name ?? "png" };
            if (LoadImage(texture, bytes, markNonReadable)) return Keep(texture);
            Object.Destroy(texture);
            return null;
        }

        internal static void DestroyAll()
        {
            foreach (var o in _made)
                if (o != null) Object.Destroy(o);
            _made.Clear();
            Glyphs.Released();
            HudStyle.Released();
            HudImgui.Released();
            HudCore.ReleasedStyles();
        }
    }
}
