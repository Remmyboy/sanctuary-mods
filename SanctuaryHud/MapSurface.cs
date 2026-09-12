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

        // ---- the frame: the part of the world the panel shows -----------------
        //
        // The playable area, not the whole world. On most maps they are the
        // same, but six shipped maps (Seton's Clutch, The Forge, There Is Time,
        // Theta Passage, Two Step Shuffle, White Desert) have terrain twice the
        // size of the play space, with an area named "PlayableArea" fencing
        // play into the centred half — and the game's GetDefaultPlayableArea
        // looks exactly that name up. Each map's preview is rendered of that
        // area, so framing on it makes the picture fill the panel.
        //
        // Measuring every Spawn marker against the start circles baked into
        // the previews first singled those six out as "stale previews", and the
        // first release drew them into the middle of a world-sized panel with
        // a black border round three-quarters of it. They are not stale, they
        // are fenced; reading the area from the game makes that the rule
        // rather than a list of names.
        //
        // The whole world until the area has been read, which is the right
        // answer for almost every map anyway.

        internal static float FrameX { get; private set; }
        internal static float FrameZ { get; private set; }
        internal static float FrameW { get; private set; }
        internal static float FrameL { get; private set; }
        private static bool _frameFromArea;

        /// Frames the panel on an area given as its centre and full size, the
        /// shape the client's Area class uses. Ignored when it is degenerate
        /// or sticks out past the world, rather than trusted into a broken
        /// panel.
        internal static void SetFrame(float centreX, float centreZ, float sizeX, float sizeZ)
        {
            if (!Ready || !(sizeX >= 8f) || !(sizeZ >= 8f)) return;
            var x = centreX - sizeX * 0.5f;
            var z = centreZ - sizeZ * 0.5f;
            const float slack = 1f;
            if (x < -slack || z < -slack || x + sizeX > Width + slack || z + sizeZ > Length + slack) return;
            if (_frameFromArea && Mathf.Approximately(x, FrameX) && Mathf.Approximately(z, FrameZ) &&
                Mathf.Approximately(sizeX, FrameW) && Mathf.Approximately(sizeZ, FrameL)) return;

            FrameX = x;
            FrameZ = z;
            FrameW = sizeX;
            FrameL = sizeZ;
            _frameFromArea = true;
            HudCore._log?.LogInfo($"MiniMap: {MapName} framed on its playable area {sizeX:0}x{sizeZ:0} at ({x:0}, {z:0}) of {Width:0}x{Length:0}.");
        }

        private static void ResetFrame()
        {
            FrameX = 0f;
            FrameZ = 0f;
            FrameW = Width;
            FrameL = Length;
            _frameFromArea = false;
        }

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
            ResetFrame();
            LoadBackdrop(path);
            HudCore._log?.LogInfo(
                $"MiniMap: {MapName} ({dataName}) {Width}x{Length}, " +
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

        /// Where the preview image belongs inside the map area: all of it, now
        /// that the panel is framed on the same playable area the preview was
        /// rendered of.
        internal static Rect BackdropRect(Rect map) => map;

        internal static void Clear()
        {
            if (_backdropTexture != null) UnityEngine.Object.Destroy(_backdropTexture);
            Backdrop = null;
            _backdropTexture = null;
            _loadedFrom = null;
            MapName = null;
            Width = 0f;
            Length = 0f;
            FrameX = 0f;
            FrameZ = 0f;
            FrameW = 0f;
            FrameL = 0f;
            _frameFromArea = false;
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
        // Both directions go through the frame, so a unit, a click and the fog
        // all agree on where the playable area sits in the panel.

        internal static Vector2 WorldToPanel(Vector3 world, Rect map)
        {
            var w = Mathf.Max(1f, FrameW);
            var l = Mathf.Max(1f, FrameL);
            return new Vector2(
                map.x + Mathf.Clamp01((world.x - FrameX) / w) * map.width,
                map.y + (1f - Mathf.Clamp01((world.z - FrameZ) / l)) * map.height);
        }

        internal static Vector3 PanelToWorld(Vector2 point, Rect map)
        {
            var u = Mathf.Clamp01((point.x - map.x) / Mathf.Max(1f, map.width));
            var v = Mathf.Clamp01((point.y - map.y) / Mathf.Max(1f, map.height));
            // The y is irrelevant to the caller: the camera mover is handed a
            // square in the ground plane and samples the height itself.
            return new Vector3(FrameX + u * FrameW, 0f, FrameZ + (1f - v) * FrameL);
        }
    }
}
