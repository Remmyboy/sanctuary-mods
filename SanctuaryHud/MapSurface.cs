using System;
using System.IO;
using System.Linq;
using System.Reflection;
using EM.Map;
using UnityEngine;

namespace SanctuaryHud
{
    // The map itself: how big it is, what it looks like from above, and how a
    // world position becomes a point on the panel.
    //
    // All of this comes from the game's own C# rather than through the Lua
    // bridge. The map is loaded before the client's Lua VM exists, none of
    // these numbers change during a match, and MapManager hands them over
    // directly — so there is nothing here worth a chunk call.
    internal static class MapSurface
    {
        /// World extents. The world runs x in [0, Width], z in [0, Length]
        /// with no offset (common/mapUtils.lua IsPositionOnMap), and the two
        /// differ wildly between maps — 256 on Ambush Pass, 2048 on Alpha 7
        /// Quarantine — so nothing may assume a scale.
        internal static float Width { get; private set; }
        internal static float Length { get; private set; }

        internal static string MapName { get; private set; }

        /// The map's own preview image, or null when the map has none and the
        /// panel falls back to a flat backdrop.
        internal static Texture2D Backdrop { get; private set; }

        internal static bool Ready => Width > 0f && Length > 0f;

        /// The map path the current state was built from, so a new match is
        /// noticed without polling the file system.
        private static string _loadedFrom;
        private static Texture2D _backdropTexture;
        private static bool _loggedNoPreview;

        /// True when this map's preview covers only the centred half of the
        /// world in each axis — see HalfFramedMaps.
        private static bool _halfFramed;

        /// Set by the plugin from `Map · FramingOverride`.
        internal static string FramingOverride;

        // Six shipped maps have a stale preview.png that covers only the
        // centred half of the world in each axis, a quarter of the area. They
        // look like maps that were scaled up x2 without the preview being
        // regenerated — note that the Survival variants of three of them ship
        // correct full-map previews.
        //
        // Measured, not guessed: for all 150 shipped maps, each army's Spawn
        // marker was projected under both framings and the result checked
        // against the vivid start circle baked into the image. 144 maps agree
        // with the full-map framing and these six with the half. Redo that
        // measurement if the game ever reissues its previews.
        private static readonly string[] HalfFramedMaps =
        {
            "Seton_s_Clutch", "The_Forge", "There_Is_Time",
            "Theta_Passage", "Two_Step_Shuffle", "White_Desert",
        };

        /// Picks up a newly loaded map, and drops everything when the match
        /// ends. Cheap enough to call every frame: the common case is one
        /// string comparison.
        internal static void Refresh()
        {
            string path = null;
            try
            {
                if (MapManager.IsMapLoaded) path = MapManager.mapFile?.filePath;
            }
            catch
            {
                // Between matches MapManager can be mid-teardown; treat that
                // as no map rather than letting it take the frame down.
            }

            if (string.IsNullOrEmpty(path))
            {
                if (_loadedFrom != null) Clear();
                return;
            }

            if (path == _loadedFrom) return;
            Clear();
            _loadedFrom = path;

            try
            {
                Width = MapManager.mapFile.width;
                Length = MapManager.mapFile.length;
                MapName = MapManager.mapFile.name;
            }
            catch (Exception e)
            {
                HudCore._log?.LogWarning($"MiniMap: map size unavailable ({e.Message}); the panel stays hidden.");
                return;
            }

            var dataName = Path.GetFileNameWithoutExtension(path) ?? "";
            _halfFramed = ResolveFraming(dataName);
            LoadBackdrop(path);
            HudCore._log?.LogInfo(
                $"MiniMap: {MapName} ({dataName}) {Width}x{Length}, framing {(_halfFramed ? "HALF" : "FULL")}, " +
                $"backdrop {(Backdrop != null ? "loaded" : "missing")}.");
        }

        // Every map folder ships a preview.png beside its .sanmap, and the
        // file is simply read and decoded here.
        //
        // The game's own loader, LobbyManager.GetMapPicture, looks like the
        // tidier route and was the first thing tried — but it goes through the
        // gamedata index, and starting a second match or replay in one session
        // calls this at a moment when that index throws. Once it failed the
        // map had no picture for the rest of the game. Reading the file is not
        // dependent on any game state, so it cannot fail that way.
        private static void LoadBackdrop(string mapPath)
        {
            try
            {
                var relative = mapPath;
                if (Path.IsPathRooted(relative))
                {
                    var root = Application.dataPath.Replace('\\', '/');
                    var full = relative.Replace('\\', '/');
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                    relative = full.Substring(root.Length).TrimStart('/');
                }

                var folder = Path.GetDirectoryName(relative);
                if (folder == null) return;
                var preview = Path.Combine(Application.dataPath, Path.Combine(folder, "preview.png"));
                if (!File.Exists(preview))
                {
                    if (!_loggedNoPreview)
                    {
                        _loggedNoPreview = true;
                        HudCore._log?.LogInfo($"MiniMap: no preview.png at {preview}; drawing a flat backdrop.");
                    }
                    return;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!LoadPng(texture, File.ReadAllBytes(preview)))
                {
                    UnityEngine.Object.Destroy(texture);
                    HudCore._log?.LogWarning("MiniMap: could not decode the map preview; drawing a flat backdrop.");
                    return;
                }
                Backdrop = texture;
                _backdropTexture = texture;
            }
            catch (Exception e)
            {
                HudCore._log?.LogWarning($"MiniMap: backdrop load failed ({e.Message}); drawing a flat backdrop.");
                Backdrop = null;
                _backdropTexture = null;
            }
        }

        private static MethodInfo _loadImage;
        private static bool _loadImageResolved;

        /// Decodes a PNG into a texture.
        ///
        /// Texture2D.LoadImage lives in UnityEngine.ImageConversionModule,
        /// which is built against netstandard 2.1 — the same wall the HUD's
        /// alert sounds hit with the audio module — so it is reached by
        /// reflection rather than referenced.
        private static bool LoadPng(Texture2D texture, byte[] bytes)
        {
            if (!_loadImageResolved)
            {
                _loadImageResolved = true;
                var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                    .SelectMany(HudCore.GetTypesSafe)
                    .FirstOrDefault(t => t.FullName == "UnityEngine.ImageConversion");
                _loadImage = type?.GetMethod("LoadImage", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }, null);
                if (_loadImage == null)
                    HudCore._log?.LogWarning("MiniMap: ImageConversion.LoadImage not found; the backdrop stays flat.");
            }
            return _loadImage != null && _loadImage.Invoke(null, new object[] { texture, bytes, false }) is bool ok && ok;
        }

        /// Whether this map's preview covers only the centred half. The
        /// override wins, so a map the list doesn't know about (a custom one,
        /// or a new shipped one) is a settings edit rather than a rebuild:
        /// "Theta_Passage=half;My_Map=full".
        private static bool ResolveFraming(string dataName)
        {
            var overrides = FramingOverride;
            if (!string.IsNullOrEmpty(overrides))
            {
                foreach (var entry in overrides.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!entry.Substring(0, eq).Trim().Equals(dataName, StringComparison.OrdinalIgnoreCase)) continue;
                    return entry.Substring(eq + 1).Trim().StartsWith("half", StringComparison.OrdinalIgnoreCase);
                }
            }

            foreach (var name in HalfFramedMaps)
                if (name.Equals(dataName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// Where the preview image belongs inside the map area.
        ///
        /// The world transform always spans the whole map, on every map, so a
        /// unit is drawn in the right place regardless. On a half-framed map
        /// the picture simply doesn't reach the edges: it is drawn into the
        /// middle quarter it actually covers, and the outer ring is left as
        /// backdrop. Better a correct map with art in the middle than a
        /// complete picture with every unit in the wrong place.
        internal static Rect BackdropRect(Rect map)
        {
            if (!_halfFramed) return map;
            return new Rect(map.x + map.width * 0.25f, map.y + map.height * 0.25f,
                            map.width * 0.5f, map.height * 0.5f);
        }

        internal static void Clear()
        {
            if (_backdropTexture != null) UnityEngine.Object.Destroy(_backdropTexture);
            Backdrop = null;
            _backdropTexture = null;
            _loadedFrom = null;
            MapName = null;
            Width = 0f;
            Length = 0f;
            _halfFramed = false;
        }

        // ---- world <-> panel ------------------------------------------------
        //
        // Measured against the shipped maps rather than assumed, because a z
        // flip is the one error here that looks perfectly fine. Ambush Pass is
        // 256x256 with its Spawn markers at ARMY_1 (x 98, z 228) and ARMY_2
        // (x 164, z 40); on its 512x512 preview.png the matching numbered
        // circles sit at (0.385, 0.107) and (0.639, 0.842) as fractions from
        // the top-left. Two points, both axes: x runs left to right unflipped,
        // and world +z is *up* the image.
        //
        // Every shipped map's playable area is the whole map, so there is no
        // playable-area term; the transform keys off map size, so a custom map
        // whose playable area is smaller is still drawn whole.

        internal static Vector2 WorldToPanel(Vector3 world, Rect map)
        {
            return new Vector2(
                map.x + Mathf.Clamp01(world.x / Width) * map.width,
                map.y + (1f - Mathf.Clamp01(world.z / Length)) * map.height);
        }

        internal static Vector3 PanelToWorld(Vector2 point, Rect map)
        {
            var u = Mathf.Clamp01((point.x - map.x) / Mathf.Max(1f, map.width));
            var v = Mathf.Clamp01((point.y - map.y) / Mathf.Max(1f, map.height));
            // The y is irrelevant to the caller: the camera mover is handed a
            // square in the ground plane and samples the height itself.
            return new Vector3(u * Width, 0f, (1f - v) * Length);
        }
    }
}
