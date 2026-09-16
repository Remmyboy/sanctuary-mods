using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
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
    // settings and the clicking. The panel stands on the game's HUD canvas
    // (HudCanvas): the map's picture, the fog and every icon are Images, a
    // press on the map is a pointer event that never reaches the battlefield,
    // and the frame is dragged and the corner pulled as uGUI drags.
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
        private static ConfigEntry<float> _cfgPosX;
        private static ConfigEntry<float> _cfgPosY;
        private static ConfigEntry<bool> _cfgLocked;

        /// The key toggles this within a session; the setting is what it
        /// starts as and what the Mods page shows.
        private static bool _shown = true;

        // 1080p-logical pixels, like every other panel in this repo; the
        // canvas converts (HudCanvas.UnitsPerLogical).
        private static Rect _rect = new Rect(16, 802, 250, 250);

        /// The border doubles as the handle that moves the panel, so the map
        /// itself is free to mean "go here".
        private const float Frame = 5f;
        private const float Grip = 14f;

        /// The live size, so a resize drag doesn't write the config file on
        /// every frame of the drag; it is stored when the mouse comes up.
        private static float _size;
        private static bool _resizing;

        private static float _lastJump;
        private static bool _dragging;
        private static bool _wasInMatch;

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
            _cfgPosX = config.Bind("MiniMap", "PanelX", 16f, "Panel X in 1080p-logical pixels.");
            _cfgPosY = config.Bind("MiniMap", "PanelY", 802f, "Panel Y in 1080p-logical pixels.");
            _cfgLocked = config.Bind("MiniMap", "Locked", false, "Keep the panel where it is: no dragging or resizing during a game.");

            _rect.x = _cfgPosX.Value;
            _rect.y = _cfgPosY.Value;
            _size = _cfgSize.Value;
            _shown = _cfgEnabled.Value;
        }

        /// Whether something in here has thrown. The mini-map shares the HUD's
        /// Update now, so an exception escaping it would take the economy
        /// strip and the alerts down with it — everything below is guarded,
        /// and a fault costs the mini-map and nothing else.
        private static bool _tickFailed;
        private static bool _syncLogged;

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

            // A release outside the window may never reach the handler, and
            // a drag stuck on would leave the full-screen shield up with
            // nothing under it — every click in the game swallowed. The real
            // button state is the backstop.
            if (!Input.GetMouseButton(0))
            {
                if (_dragging) _dragging = false;
                if (_resizing)
                {
                    _resizing = false;
                    _cfgSize.Value = _size;
                }
            }

            if (!InMatch)
            {
                ShowPanel(false);
                return;
            }

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

            try
            {
                SyncPanel(showing);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Mini-map could not be laid out (logged once): {e}");
                }
                ShowPanel(false);
            }

            // Persist the panel position once the drag is over.
            if (!Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _rect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _rect.y) > 0.5f))
            {
                _cfgPosX.Value = _rect.x;
                _cfgPosY.Value = _rect.y;
            }
        }

        // ---- the panel on the canvas ----------------------------------------------

        private static RectTransform _plate, _map;
        private static Image _plateImage, _mapBack, _dragShield;
        private static RawImage _backdrop, _fog;
        private static HudPanel.PanelDrag _drag;
        private static MapInput _input;
        private static ResizeGrip _grip;
        private static readonly Image[] _border = new Image[4];
        private static readonly List<Image> _gripMarks = new List<Image>();
        private static readonly List<RawImage> _icons = new List<RawImage>();
        private static readonly List<Image> _spots = new List<Image>();

        private static readonly Color FillColour = new Color(0.05f, 0.07f, 0.09f, 1f);
        private static readonly Color MapBackColour = new Color(0.09f, 0.11f, 0.14f, 1f);
        private static readonly Color BorderColour = new Color(0.35f, 0.55f, 0.8f, 0.7f);

        private static void ShowPanel(bool showing)
        {
            if (_plate != null && _plate.gameObject.activeSelf != showing) _plate.gameObject.SetActive(showing);
            if (!showing && _dragShield != null && _dragShield.gameObject.activeSelf) _dragShield.gameObject.SetActive(false);
        }

        private static void SyncPanel(bool showing)
        {
            if (!showing || !MapSurface.Ready)
            {
                ShowPanel(false);
                return;
            }
            var root = HudCanvas.Ensure();
            if (root == null) return;
            if (_plate == null) BuildPanel(root);
            ShowPanel(true);

            // The map keeps its own proportions inside a Size-by-Size box, so
            // a non-square map is letter-boxed rather than stretched.
            var k = HudCanvas.UnitsPerLogical;
            var side = Mathf.Clamp(_size, 120f, 640f) * k;
            var aspect = MapSurface.FrameL / Mathf.Max(1f, MapSurface.FrameW);
            var mapW = aspect <= 1f ? side : side / aspect;
            var mapH = aspect <= 1f ? side * aspect : side;
            var frame = Frame * k;
            var plateW = mapW + frame * 2f;
            var plateH = mapH + frame * 2f;
            _plate.sizeDelta = new Vector2(plateW, plateH);
            _map.anchoredPosition = new Vector2(frame, -frame);
            _map.sizeDelta = new Vector2(mapW, mapH);
            var area = new Rect(0f, 0f, mapW, mapH);
            _input.Area = area;

            // Where it sits: where the frame was dragged to, else the saved
            // place; kept wholly on screen whatever the size.
            var size = HudCanvas.Size;
            var dragged = _drag.Dragging || _drag.Moved;
            var x = dragged ? _plate.anchoredPosition.x : _rect.x * k;
            var y = dragged ? -_plate.anchoredPosition.y : _rect.y * k;
            _drag.Moved = false;
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, size.x - plateW));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, size.y - plateH));
            _plate.anchoredPosition = new Vector2(x, -y);
            _rect = new Rect(x / k, y / k, plateW / k, plateH / k);

            var opacity = _cfgOpacity.Value;
            var fill = FillColour;
            fill.a = opacity;
            _plateImage.color = fill;
            var back = MapBackColour;
            back.a = opacity;
            _mapBack.color = back;

            // The map area always spans the whole world; on a map whose
            // preview only covers the middle, the picture is drawn into just
            // that part and the rest stays backdrop.
            var backdrop = MapSurface.Backdrop;
            var haveBackdrop = backdrop != null;
            if (_backdrop.gameObject.activeSelf != haveBackdrop) _backdrop.gameObject.SetActive(haveBackdrop);
            if (haveBackdrop)
            {
                _backdrop.texture = backdrop;
                _backdrop.color = new Color(1f, 1f, 1f, opacity);
                var r = MapSurface.BackdropRect(area);
                _backdrop.rectTransform.anchoredPosition = new Vector2(r.x, -r.y);
                _backdrop.rectTransform.sizeDelta = new Vector2(r.width, r.height);
            }

            // Fog over the ground, under everything the player is being told
            // about — a contact is drawn because the game says it can be
            // seen, so it must not be shaded out by the fog it is standing in.
            var fog = _cfgFog.Value ? FogOverlay.Mask : null;
            var haveFog = fog != null;
            if (_fog.gameObject.activeSelf != haveFog) _fog.gameObject.SetActive(haveFog);
            if (haveFog)
            {
                _fog.texture = fog;
                _fog.color = new Color(1f, 1f, 1f, opacity);
            }

            SyncSpots(area, k);
            SyncContacts(area, k);

            // The border round the map, and the grip in the corner.
            var line = Mathf.Max(1f, k);
            Edge(_border[0], 0f, 0f, mapW, line);
            Edge(_border[1], 0f, mapH - line, mapW, line);
            Edge(_border[2], 0f, 0f, line, mapH);
            Edge(_border[3], mapW - line, 0f, line, mapH);
            var gripColour = new Color(0.55f, 0.7f, 0.9f, _resizing ? 0.95f : 0.5f);
            for (var i = 0; i < 3; i++)
            {
                var inset = (i + 1) * 4f * k;
                var mark = 2f * k;
                _gripMarks[i * 2].color = gripColour;
                _gripMarks[i * 2 + 1].color = gripColour;
                Edge(_gripMarks[i * 2], plateW - inset - mark / 2f, plateH - 2f * k - mark, mark, mark);
                Edge(_gripMarks[i * 2 + 1], plateW - 2f * k - mark, plateH - inset - mark / 2f, mark, mark);
            }
            ((RectTransform)_grip.transform).sizeDelta = new Vector2(Grip * k, Grip * k);

            // Keep a release off the battlefield while a drag is on: the
            // release is what the game acts on, and letting go outside the
            // panel with a build queued would plant a building wherever the
            // cursor ended up.
            var shield = _dragging || _resizing;
            if (_dragShield.gameObject.activeSelf != shield) _dragShield.gameObject.SetActive(shield);
        }

        private static void Edge(Image image, float x, float y, float w, float h)
        {
            var rt = image.rectTransform;
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void SyncSpots(Rect area, float k)
        {
            var spots = _cfgAlloySpots.Value ? Contacts.AlloySpots : null;
            var count = spots != null ? spots.Count : 0;
            var dot = 3f * k;
            for (var i = 0; i < count; i++)
            {
                if (i >= _spots.Count) _spots.Add(Dot(_map, "Alloy", AlloyColour));
                var image = _spots[i];
                if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
                var p = MapSurface.WorldToPanel(new Vector3(spots[i].x, 0f, spots[i].y), area);
                image.rectTransform.anchoredPosition = new Vector2(p.x, -p.y);
                image.rectTransform.sizeDelta = new Vector2(dot, dot);
            }
            for (var i = count; i < _spots.Count; i++)
                if (_spots[i].gameObject.activeSelf) _spots[i].gameObject.SetActive(false);
        }

        private static void SyncContacts(Rect area, float k)
        {
            var contacts = Contacts.Live;
            var count = contacts != null ? contacts.Count : 0;
            var size = _cfgIconSize.Value * k;
            var haveAtlas = _iconAtlas != null && _iconUvRects != null;
            for (var i = 0; i < count; i++)
            {
                if (i >= _icons.Count) _icons.Add(Icon(_map));
                var image = _icons[i];
                if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
                var c = contacts[i];
                var p = MapSurface.WorldToPanel(new Vector3(c.X, 0f, c.Z), area);
                image.rectTransform.anchoredPosition = new Vector2(p.x, -p.y);
                image.rectTransform.sizeDelta = new Vector2(size, size);
                // A radar-only contact is drawn white, as the game draws it:
                // it has told the player something is there and what size of
                // plate it is, but not whose it is.
                image.color = c.Seen ? Contacts.ColourFor(c.Army) : new Color(0.95f, 0.95f, 0.95f, 0.9f);
                // The icon is the whole point of drawing these as icons rather
                // than dots, but the registry fills in during match load, so
                // early contacts get a plain square until it does.
                if (haveAtlas && c.Icon >= 0 && c.Icon < _iconUvRects.Count)
                {
                    image.texture = _iconAtlas;
                    image.uvRect = _iconUvRects[c.Icon];
                }
                else
                {
                    image.texture = null;
                    image.uvRect = new Rect(0f, 0f, 1f, 1f);
                }
            }
            for (var i = count; i < _icons.Count; i++)
                if (_icons[i].gameObject.activeSelf) _icons[i].gameObject.SetActive(false);
        }

        private static void BuildPanel(RectTransform root)
        {
            _plateImage = HudCanvas.Fill(root, "Mini-map", FillColour);
            _plateImage.raycastTarget = true;
            _plate = _plateImage.rectTransform;
            _plate.anchorMin = _plate.anchorMax = new Vector2(0f, 1f);
            _plate.pivot = new Vector2(0f, 1f);
            _drag = _plate.gameObject.AddComponent<HudPanel.PanelDrag>();
            _drag.Locked = () => _cfgLocked.Value;

            _mapBack = HudCanvas.Fill(_plate, "Map", MapBackColour);
            _mapBack.raycastTarget = true;
            _map = _mapBack.rectTransform;
            _map.anchorMin = _map.anchorMax = new Vector2(0f, 1f);
            _map.pivot = new Vector2(0f, 1f);
            _input = _map.gameObject.AddComponent<MapInput>();

            _backdrop = Raw(_map, "Backdrop");
            _fog = Raw(_map, "Fog");
            var frt = _fog.rectTransform;
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero;
            frt.offsetMax = Vector2.zero;

            for (var i = 0; i < _border.Length; i++) _border[i] = Dot(_map, "Border", BorderColour);
            // Lines hang from their top-left corner, as Edge places them.
            foreach (var line in _border) line.rectTransform.pivot = new Vector2(0f, 1f);
            _gripMarks.Clear();
            for (var i = 0; i < 6; i++) _gripMarks.Add(Dot(_plate, "Grip", Color.white));
            foreach (var mark in _gripMarks) mark.rectTransform.pivot = new Vector2(0f, 1f);

            var grip = HudCanvas.Fill(_plate, "Resize", Color.clear);
            grip.raycastTarget = true;
            grip.canvasRenderer.cullTransparentMesh = false;
            var grt = grip.rectTransform;
            grt.anchorMin = grt.anchorMax = new Vector2(1f, 0f);
            grt.pivot = new Vector2(1f, 0f);
            grt.anchoredPosition = Vector2.zero;
            _grip = grip.gameObject.AddComponent<ResizeGrip>();

            // The whole-screen shield for a drag, on the canvas root so it
            // covers everything; off until a drag starts.
            _dragShield = HudCanvas.Fill(root, "Mini-map drag shield", Color.clear);
            _dragShield.raycastTarget = true;
            _dragShield.canvasRenderer.cullTransparentMesh = false;
            var srt = _dragShield.rectTransform;
            srt.anchorMin = Vector2.zero;
            srt.anchorMax = Vector2.one;
            srt.offsetMin = Vector2.zero;
            srt.offsetMax = Vector2.zero;
            _dragShield.gameObject.SetActive(false);
        }

        private static Image Dot(Transform parent, string name, Color colour)
        {
            var image = HudCanvas.Fill(parent, name, colour);
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            return image;
        }

        private static RawImage Raw(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<RawImage>();
            image.raycastTarget = false;
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            return image;
        }

        private static RawImage Icon(Transform parent)
        {
            var image = Raw(parent, "Contact");
            image.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            return image;
        }

        // ---- clicking ---------------------------------------------------------

        /// The map's pointer events: a press or a drag on the map moves the
        /// camera there; the release lands exactly where the button came up.
        private sealed class MapInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            internal Rect Area;

            private bool Point(PointerEventData eventData, out Vector2 point)
            {
                point = default;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, eventData.position, eventData.pressEventCamera, out var local)) return false;
                point = new Vector2(local.x, -local.y);
                return true;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || !Point(eventData, out var point)) return;
                _dragging = true;
                _lastJump = Time.realtimeSinceStartup;
                JumpTo(MapSurface.PanelToWorld(point, Area));
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || !_dragging) return;
                // Dragging across the map is a continuous pan, but each move
                // is a chunk through the Lua bridge, so it is held to a rate
                // a person cannot tell from every frame.
                var now = Time.realtimeSinceStartup;
                if (now - _lastJump < 0.06f) return;
                if (!Point(eventData, out var point)) return;
                _lastJump = now;
                JumpTo(MapSurface.PanelToWorld(point, Area));
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || !_dragging) return;
                _dragging = false;
                if (Point(eventData, out var point)) JumpTo(MapSurface.PanelToWorld(point, Area));
            }
        }

        /// The corner grip: pulling it out and down is the size going up.
        /// Both axes count, so a diagonal pull does what it looks like.
        private sealed class ResizeGrip : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private float _startSize;
            private Vector2 _startMouse;

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (_cfgLocked.Value || eventData.button != PointerEventData.InputButton.Left) return;
                _resizing = true;
                _startSize = _size;
                _startMouse = eventData.position;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (!_resizing) return;
                var pxPerLogical = Screen.height / 1080f;
                var delta = (eventData.position - _startMouse) / pxPerLogical;
                // Screen y runs up; the corner goes down as the panel grows.
                _size = Mathf.Clamp(_startSize + (delta.x - delta.y) * 0.5f, 120f, 640f);
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (!_resizing) return;
                _resizing = false;
                _cfgSize.Value = _size;
            }
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

        // ---- lifecycle ----------------------------------------------------------

        /// For unload: a hot reload leaves this assembly in memory for the rest
        /// of the session, so anything left standing here is left for good.
        internal static void Shutdown()
        {
            FogOverlay.Release();
            MapSurface.Clear();
            Contacts.Clear();
            if (_plate != null) UnityEngine.Object.Destroy(_plate.gameObject);
            if (_dragShield != null) UnityEngine.Object.Destroy(_dragShield.gameObject);
            _plate = null;
            _dragShield = null;
            _icons.Clear();
            _spots.Clear();
            _gripMarks.Clear();
        }
    }
}
