using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The mini-map the game doesn't have.
    //
    // Sanctuary ships no map panel at all — there is no "minimap" anywhere in
    // its Lua tree and UIPanelType has no entry for one — so this is a new
    // element rather than a replacement for something. It draws the map's own
    // preview image as a backdrop, shaded where you cannot see, with every
    // contact you are allowed to see as its strategic icon and the alloy
    // deposits nobody has taken. Clicking or dragging moves the camera there.
    //
    // The work is split four ways: MapSurface knows about the map (all from
    // the game's own C#), Contacts knows what is on it (all from the client's
    // Lua), FogOverlay knows what can be seen, and this is the panel, the
    // settings and the clicking.
    internal static class MiniMap
    {
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<KeyCode> _cfgToggleKey;
        private static ConfigEntry<float> _cfgSize;
        private static ConfigEntry<float> _cfgOpacity;
        private static ConfigEntry<float> _cfgIconSize;
        private static ConfigEntry<float> _cfgRefreshHz;
        private static ConfigEntry<bool> _cfgAlloySpots;
        private static ConfigEntry<bool> _cfgFog;
        private static ConfigEntry<float> _cfgFogDarkness;
        private static ConfigEntry<string> _cfgFraming;
        private static ConfigEntry<float> _cfgPosX;
        private static ConfigEntry<float> _cfgPosY;

        /// The key toggles this within a session; the setting is what it
        /// starts as and what the Mods page shows.
        private static bool _shown = true;

        // 1080p-logical pixels, like every other panel in this repo, rescaled
        // to the real resolution by the caller's GUI matrix.
        private static Rect _rect = new Rect(16, 802, 250, 250);

        /// The map inside the window, in window coordinates, for drawing and
        /// hit-testing.
        private static Rect _mapArea;

        /// The border doubles as the handle that moves the panel, so the map
        /// itself is free to mean "go here".
        private const float Frame = 5f;
        private const float Grip = 14f;

        /// Its own window id, distinct from the other mods' (0x5DC, 0x5DD,
        /// 0x43414D55): "MMAP".
        private const int WindowId = 0x4D4D4150;

        /// The live size, so a resize drag doesn't write the config file on
        /// every frame of the drag; it is stored when the mouse comes up.
        private static float _size;
        private static bool _resizing;
        private static float _resizeStartSize;
        private static Vector2 _resizeStartMouse;

        private static float _lastJump;
        private static bool _dragging;
        private static bool _wasInMatch;

        private static Texture2D _texFill;
        private static Texture2D _texLine;

        internal static void Bind(ConfigFile config)
        {
            _cfgEnabled = config.Bind("MiniMap", "Enabled", true,
                "Show the mini-map during a match or a replay.");
            _cfgToggleKey = config.Bind("MiniMap", "ToggleKey", KeyCode.F2,
                "Key that shows/hides the mini-map. Hotkey.");
            _cfgSize = config.Bind("MiniMap", "Size", 240f,
                new ConfigDescription("How big the map is drawn, in 1080p-logical pixels along its longest edge. Dragging the bottom-right corner of the panel sets this too.",
                    new AcceptableValueRange<float>(120f, 640f)));
            _cfgOpacity = config.Bind("MiniMap", "Opacity", 0.88f,
                new ConfigDescription("How solid the panel is, from barely there to fully opaque.",
                    new AcceptableValueRange<float>(0.25f, 1f)));
            _cfgIconSize = config.Bind("MiniMap", "IconSize", 9f,
                new ConfigDescription("How big each contact's strategic icon is drawn, in 1080p-logical pixels.",
                    new AcceptableValueRange<float>(4f, 20f)));
            _cfgRefreshHz = config.Bind("MiniMap", "RefreshHz", 8f,
                new ConfigDescription("How many times a second the contacts are re-read from the game. Lower costs less on a big late game.",
                    new AcceptableValueRange<float>(2f, 20f)));
            _cfgAlloySpots = config.Bind("MiniMap", "ShowAlloySpots", true,
                "Mark alloy deposits that have no extractor on them yet — the same ones the game marks on the ground.");
            _cfgFog = config.Bind("MiniMap", "ShowFog", true,
                "Shade the ground you cannot currently see, so an empty patch of map reads as nothing there rather than nothing known.");
            _cfgFogDarkness = config.Bind("MiniMap", "FogDarkness", 0.6f,
                new ConfigDescription("How heavily the unseen ground is shaded.",
                    new AcceptableValueRange<float>(0.1f, 0.95f)));
            _cfgFraming = config.Bind("MiniMap", "FramingOverride", "",
                "Force how a map's preview image is framed, for a map whose picture is offset or scaled wrongly — \"My_Map=half\" if the image covers only the middle of the map, \"My_Map=full\" if it covers all of it. Semicolon-separated; the map name is its folder name. Six shipped maps are already known and need no entry.");
            _cfgPosX = config.Bind("MiniMap", "PanelX", 16f, "Panel X in 1080p-logical pixels.");
            _cfgPosY = config.Bind("MiniMap", "PanelY", 802f, "Panel Y in 1080p-logical pixels.");

            _rect.x = _cfgPosX.Value;
            _rect.y = _cfgPosY.Value;
            _size = _cfgSize.Value;
            _shown = _cfgEnabled.Value;
        }

        /// Whether something in here has thrown. The mini-map shares the HUD's
        /// Update and OnGUI now, so an exception escaping it would take the
        /// economy strip and the alerts down with it — everything below is
        /// guarded, and a fault costs the mini-map and nothing else.
        private static bool _tickFailed;
        private static bool _drawFailed;

        /// From the HUD's Update. `hudShowing` is the overlay's own state, so
        /// the mini-map goes away with the rest of the HUD on F10 and under
        /// the game's menus.
        internal static void Tick(float deltaTime, bool hudShowing)
        {
            if (_tickFailed) return;
            try { TickCore(deltaTime, hudShowing); }
            catch (Exception e)
            {
                _tickFailed = true;
                _log?.LogError($"Mini-map update failed; the rest of the HUD carries on without it: {e}");
            }
        }

        /// From the HUD's OnGUI, inside its 1080p-logical GUI matrix.
        internal static void Draw(float logicalWidth, float logicalHeight, float scale)
        {
            if (_drawFailed) return;
            try { DrawCore(logicalWidth, logicalHeight, scale); }
            catch (Exception e)
            {
                _drawFailed = true;
                _log?.LogError($"Mini-map draw failed; the rest of the HUD carries on without it: {e}");
            }
        }

        private static void TickCore(float deltaTime, bool hudShowing)
        {
            if (_cfgEnabled == null) return;

            // Nothing from the last match may survive into the next one: the
            // icon registry and the map itself are both rebuilt per match.
            if (InMatch != _wasInMatch)
            {
                _wasInMatch = InMatch;
                if (!InMatch)
                {
                    MapSurface.Clear();
                    Contacts.Clear();
                    FogOverlay.Release();
                }
            }

            // A release outside the window may never reach the window's own
            // event handler, and a drag stuck on would leave the full-screen
            // shield up with nothing drawing under it — every click in the
            // game swallowed. The real button state is the backstop.
            if (!Input.GetMouseButton(0))
            {
                if (_dragging) _dragging = false;
                if (_resizing)
                {
                    _resizing = false;
                    _cfgSize.Value = _size;
                }
            }

            if (!InMatch) return;

            MapSurface.FramingOverride = _cfgFraming.Value;
            MapSurface.Refresh();

            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                _shown = !_shown;
                _cfgEnabled.Value = _shown;
            }
            // The Mods page can flip it too.
            if (_cfgEnabled.Value != _shown) _shown = _cfgEnabled.Value;

            var showing = _shown && hudShowing;
            // A paused game is still worth looking at, but nothing is moving,
            // so there is no point re-reading it.
            if (showing && !Paused)
                Contacts.Poll(deltaTime, _cfgRefreshHz.Value, _cfgAlloySpots.Value);

            // Built from the contacts that have just been read, and pure CPU
            // work, so it belongs here rather than in the middle of drawing.
            FogOverlay.Rebuild(showing && _cfgFog.Value, _cfgFogDarkness.Value);

            // The settings page can change the size too, so follow it whenever
            // the corner isn't being dragged.
            if (!_resizing && Math.Abs(_cfgSize.Value - _size) > 0.5f) _size = _cfgSize.Value;

            // Persist the panel position once the drag is over.
            if (!Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _rect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _rect.y) > 0.5f))
            {
                _cfgPosX.Value = _rect.x;
                _cfgPosY.Value = _rect.y;
            }
        }

        private static void DrawCore(float logicalWidth, float logicalHeight, float scale)
        {
            if (_cfgEnabled == null || !_shown || !MapSurface.Ready) return;
            EnsureUi();

            // The map keeps its own proportions inside a Size-by-Size box, so
            // a non-square map is letter-boxed rather than stretched.
            var side = Mathf.Clamp(_size, 120f, 640f);
            var aspect = MapSurface.Length / Mathf.Max(1f, MapSurface.Width);
            var mapW = aspect <= 1f ? side : side / aspect;
            var mapH = aspect <= 1f ? side * aspect : side;

            _rect.width = mapW + Frame * 2f;
            _rect.height = mapH + Frame * 2f;
            _rect.x = Mathf.Clamp(_rect.x, -_rect.width + 60f, logicalWidth - 60f);
            _rect.y = Mathf.Clamp(_rect.y, 0f, logicalHeight - 40f);

            _mapArea = new Rect(Frame, Frame, mapW, mapH);

            _rect = GUI.Window(WindowId, _rect, DrawPanel, GUIContent.none, GUIStyle.none);

            // Keep the click off the battlefield underneath. Without this the
            // same press would also clear the selection or start a box drag,
            // because the game's input only knows about uGUI.
            //
            // A drag covers the whole screen, because the release is what the
            // game acts on: letting go outside the panel while a build is
            // queued would otherwise plant a building wherever the cursor
            // happened to end up.
            Shield(_dragging || _resizing ? new Rect(0f, 0f, logicalWidth, logicalHeight) : _rect, scale);
        }

        private static void DrawPanel(int id)
        {
            var opacity = _cfgOpacity.Value;
            var previousColour = GUI.color;

            GUI.color = new Color(1f, 1f, 1f, opacity);
            GUI.DrawTexture(new Rect(0f, 0f, _rect.width, _rect.height), _texFill);

            // The map area always spans the whole world; on a map whose
            // preview only covers the middle, the picture is drawn into just
            // that part and the rest stays backdrop.
            GUI.color = new Color(0.09f, 0.11f, 0.14f, opacity);
            GUI.DrawTexture(_mapArea, _texFill);
            if (MapSurface.Backdrop != null)
            {
                GUI.color = new Color(1f, 1f, 1f, opacity);
                GUI.DrawTexture(MapSurface.BackdropRect(_mapArea), MapSurface.Backdrop);
            }

            // Fog over the ground, under everything the player is being told
            // about — a contact is drawn because the game says it can be seen,
            // so it must not be shaded out by the fog it is standing in.
            if (_cfgFog.Value && FogOverlay.Mask != null)
            {
                GUI.color = new Color(1f, 1f, 1f, opacity);
                GUI.DrawTexture(_mapArea, FogOverlay.Mask);
            }

            GUI.color = Color.white;
            DrawAlloySpots();
            DrawContacts();
            DrawBorder(_mapArea, new Color(0.35f, 0.55f, 0.8f, 0.7f), 1f);
            DrawGrip();

            GUI.color = previousColour;

            HandleMapInput();

            // Whatever the map and the grip didn't claim is the frame, and the
            // frame is the handle: a press on the map itself is always a camera
            // move, never a nudge of the window.
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, _rect.height));
        }

        private static void DrawContacts()
        {
            var contacts = Contacts.Live;
            if (contacts == null) return;

            var size = _cfgIconSize.Value;
            var half = size * 0.5f;
            for (var i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                var p = MapSurface.WorldToPanel(new Vector3(c.X, 0f, c.Z), _mapArea);
                var rect = new Rect(p.x - half, p.y - half, size, size);
                // A radar-only contact is drawn white, as the game draws it:
                // it has told the player something is there and what size of
                // plate it is, but not whose it is.
                GUI.color = c.Seen ? Contacts.ColourFor(c.Army) : new Color(0.95f, 0.95f, 0.95f, 0.9f);
                // The icon is the whole point of drawing these as icons rather
                // than dots, but the registry fills in during match load, so
                // early contacts get a plain square until it does.
                if (!DrawStrategicIcon(rect, c.Icon)) GUI.DrawTexture(rect, _texLine);
            }
            GUI.color = Color.white;
        }

        private static void DrawAlloySpots()
        {
            if (!_cfgAlloySpots.Value) return;
            var spots = Contacts.AlloySpots;
            if (spots == null) return;

            GUI.color = AlloyColour;
            for (var i = 0; i < spots.Count; i++)
            {
                var p = MapSurface.WorldToPanel(new Vector3(spots[i].x, 0f, spots[i].y), _mapArea);
                GUI.DrawTexture(new Rect(p.x - 1.5f, p.y - 1.5f, 3f, 3f), _texLine);
            }
            GUI.color = Color.white;
        }

        /// Three small steps in the bottom-right corner, so the corner reads as
        /// something to pull rather than just more border.
        private static void DrawGrip()
        {
            GUI.color = new Color(0.55f, 0.7f, 0.9f, _resizing ? 0.95f : 0.5f);
            for (var i = 1; i <= 3; i++)
            {
                var inset = i * 4f;
                GUI.DrawTexture(new Rect(_rect.width - inset - 1f, _rect.height - 4f, 2f, 2f), _texLine);
                GUI.DrawTexture(new Rect(_rect.width - 4f, _rect.height - inset - 1f, 2f, 2f), _texLine);
            }
            GUI.color = Color.white;
        }

        // ---- clicking ---------------------------------------------------------

        private static void HandleMapInput()
        {
            var ev = Event.current;
            if (ev == null || ev.button != 0) return;

            var gripRect = new Rect(_rect.width - Grip, _rect.height - Grip, Grip, Grip);

            if (ev.type == EventType.MouseUp)
            {
                if (_resizing) { _resizing = false; _cfgSize.Value = _size; ev.Use(); return; }
                if (!_dragging) return;
                _dragging = false;
                // Land exactly where the button came up, whatever the rate
                // limit was doing.
                JumpTo(MapSurface.PanelToWorld(ev.mousePosition, _mapArea));
                ev.Use();
                return;
            }

            if (ev.type == EventType.MouseDown)
            {
                if (gripRect.Contains(ev.mousePosition))
                {
                    _resizing = true;
                    _resizeStartSize = _size;
                    _resizeStartMouse = GUIUtility.GUIToScreenPoint(ev.mousePosition);
                    ev.Use();
                    return;
                }
                if (!_mapArea.Contains(ev.mousePosition)) return;
                _dragging = true;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                if (_resizing)
                {
                    // The panel's top-left stays put, so the corner moving out
                    // and down is the size going up. Both axes count, so a
                    // diagonal pull does what it looks like.
                    var delta = GUIUtility.GUIToScreenPoint(ev.mousePosition) - _resizeStartMouse;
                    _size = Mathf.Clamp(_resizeStartSize + (delta.x + delta.y) * 0.5f, 120f, 640f);
                    ev.Use();
                    return;
                }
                if (!_dragging) return;
            }
            else return;

            // Dragging across the map is a continuous pan, but each move is a
            // chunk through the Lua bridge, so it is held to a rate a person
            // cannot tell from every frame.
            var now = Time.realtimeSinceStartup;
            if (ev.type == EventType.MouseDrag && now - _lastJump < 0.06f)
            {
                ev.Use();
                return;
            }
            _lastJump = now;

            JumpTo(MapSurface.PanelToWorld(ev.mousePosition, _mapArea));
            ev.Use();
        }

        /// Moves the camera over a world position without changing the zoom.
        ///
        /// FitCameraToPositions is the client's own camera mover — the one the
        /// game uses to focus a control group — and it fits the bounding box
        /// it is handed, but only ever outwards: it takes the greater of the
        /// fitted height and the height the camera is already at. So handing
        /// it a small square around the target leaves the height exactly as it
        /// was and moves the camera and nothing else, which is what a click on
        /// a mini-map should do.
        private static void JumpTo(Vector3 world)
        {
            if (!LuaReady) return;
            var inv = CultureInfo.InvariantCulture;
            var chunk =
                "local ok, err = pcall(function() " +
                "local cam = Import('client/input/cameraController.lua') " +
                $"local x, z, r = {world.x.ToString("0.##", inv)}, {world.z.ToString("0.##", inv)}, 8 " +
                "cam.FitCameraToPositions({ " +
                "  EngineClasses.float3(x - r, 0, z - r), " +
                "  EngineClasses.float3(x + r, 0, z + r) }) " +
                "end) " +
                "if not ok then Warn('MiniMap camera: ' .. tostring(err)) end";
            RunLua(chunk);
        }

        // ---- lifecycle and drawing helpers ------------------------------------

        /// For unload: a hot reload leaves this assembly in memory for the rest
        /// of the session, so anything left standing here is left for good.
        internal static void Shutdown()
        {
            FogOverlay.Release();
            MapSurface.Clear();
            Contacts.Clear();
        }

        private static void DrawBorder(Rect rect, Color colour, float width)
        {
            var previousColour = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, width), _texLine);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - width, rect.width, width), _texLine);
            GUI.DrawTexture(new Rect(rect.x, rect.y, width, rect.height), _texLine);
            GUI.DrawTexture(new Rect(rect.xMax - width, rect.y, width, rect.height), _texLine);
            GUI.color = previousColour;
        }

        private static void EnsureUi()
        {
            if (_texLine != null) return;
            _texFill = Solid(new Color(0.05f, 0.07f, 0.09f, 1f));
            _texLine = Solid(Color.white);
        }

        private static Texture2D Solid(Color colour)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, colour);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
