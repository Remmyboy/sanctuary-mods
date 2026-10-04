using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.CameraUtils
{
    // Switches off the bits of the game the camera draws over the world —
    // strategic icons, range rings, order lines, planned buildings, health
    // bars, the UI HUD — for recording cinematics, or just for a cleaner
    // picture.
    //
    // Every switch is in two places: the mod's settings on the front menu's
    // Mods page (bound config entries, so they show up there for free), and a
    // small in-match panel on a hotkey, because during a shot you want them
    // without leaving the game. The config entries are the single source of
    // truth — the panel writes to them, so a change either way persists and
    // both views agree.
    //
    // It also raises how far out units keep being drawn, which is the one
    // setting here that isn't live — see DrawDistance.
    //
    // The work itself is in RenderState and DrawDistance; this is the config,
    // the hotkey and the panel. The panel is on a canvas of the mod's own,
    // not the game's HUD canvas: hiding the game's UI switches that canvas
    // off, and the panel is how it comes back.
    [BepInPlugin("com.sanctuarydb.camerautilities", "Camera Utilities", "0.2.1")]
    public class CameraUtilitiesPlugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;
        private ConfigEntry<IconMode> _cfgIcons;
        private ConfigEntry<float> _cfgIconHeight;
        private ConfigEntry<bool> _cfgIntel;
        private ConfigEntry<bool> _cfgAttack;
        private ConfigEntry<bool> _cfgBuild;
        private ConfigEntry<bool> _cfgOrderLines;
        private ConfigEntry<bool> _cfgPlannedBuilds;
        private ConfigEntry<bool> _cfgAlloySpots;
        private ConfigEntry<bool> _cfgHealthBars;
        private ConfigEntry<bool> _cfgGameUi;
        private ConfigEntry<float> _cfgDrawDistance;

        private Harmony _harmony;

        private bool _open;

        private void Awake()
        {
            HudCore._log ??= Logger;

            _cfgToggleKey = Config.Bind("UI", "ToggleKey", KeyCode.F4,
                "Shows/hides the camera utilities panel during a match or a replay.");
            _cfgPosX = Config.Bind("UI", "PanelX", 12f, "Panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("UI", "PanelY", 420f, "Panel Y in 1080p-logical pixels.");

            _cfgIcons = Config.Bind("Icons", "StrategicIcons", IconMode.Show,
                "Show: leave strategic icons to the game. HideWhenClose: only draw them while the camera is at or above HideIconsBelowHeight. Hide: never draw them.");
            _cfgIconHeight = Config.Bind("Icons", "HideIconsBelowHeight", 100f,
                "Camera height below which strategic icons are hidden, in world units, when StrategicIcons is HideWhenClose. The game zooms from about 2 on the deck to a few hundred at full stretch.");

            _cfgIntel = Config.Bind("Ranges", "HideIntelRanges", false,
                "Hide the vision, fog, radar, sonar, omni and counter-intel range rings.");
            _cfgAttack = Config.Bind("Ranges", "HideAttackRanges", false,
                "Hide the direct, indirect, anti-air, anti-naval and counter-attack range rings.");
            _cfgBuild = Config.Bind("Ranges", "HideBuildRanges", false,
                "Hide the build and assist range rings.");

            _cfgOrderLines = Config.Bind("Orders", "HideOrderLines", false,
                "Hide the order lines and markers drawn for the selected units — move, build, attack, assist, reclaim — and the whole-army view of them the append key brings up.");
            _cfgPlannedBuilds = Config.Bind("Orders", "HidePlannedBuildings", false,
                "Hide the outlines of buildings that are queued but not started yet. They come back on their own the moment construction begins.");

            _cfgAlloySpots = Config.Bind("Markers", "HideAlloySpotMarkers", false,
                "Hide the marker drawn on an alloy deposit that has no extractor on it yet. The game already hides one once an extractor covers it, and that stays true when this is switched back off.");

            _cfgHealthBars = Config.Bind("Cinematic", "HideHealthBars", false,
                "Hide every health and progress bar.");
            _cfgGameUi = Config.Bind("Cinematic", "HideGameUI", false,
                "Hide the game's whole UI HUD. This mod's own panel stays up, so the hotkey still gets it back.");

            _cfgDrawDistance = Config.Bind("Rendering", "UnitDrawDistance", 0f,
                "Smallest distance from the camera at which a unit or structure may stop being drawn, in world units. 0 leaves the game alone, which stops drawing mobile units past 100 and structures past 160. Takes effect when a match or replay starts, and only units and structures are affected — props keep their own level-of-detail chain.");

            try
            {
                _harmony = new Harmony("com.sanctuarydb.camerautilities." + Guid.NewGuid().ToString("N").Substring(0, 8));
                DrawDistance.ApplyPatch(_harmony, Logger);
            }
            catch (Exception e)
            {
                Logger.LogError($"Camera Utilities: the draw-distance patch failed, so units will fade as usual. Everything else still works: {e}");
                _harmony?.UnpatchSelf();
                _harmony = null;
            }

            Logger.LogInfo($"Camera Utilities loaded: {_cfgToggleKey.Value} opens the panel in a match, " +
                           "and the same switches are on the Mods page.");
        }

        private void OnDestroy()
        {
            // Unloading the mod has to leave the client as the game had it:
            // the wrapper comes off RenderUpdate and every flag goes back.
            RenderState.Uninstall();
            _harmony?.UnpatchSelf();
            // A hot reload leaves the old assembly loaded; its panel goes.
            HudCanvas.Destroy();
            _panel = null;
        }

        private void Update()
        {
            // The config entries are the state; the panel and the Mods page
            // both write to them, and this is the one place they are read.
            RenderState.Icons = _cfgIcons.Value;
            RenderState.HideIconsBelow = _cfgIconHeight.Value;
            RenderState.HideIntel = _cfgIntel.Value;
            RenderState.HideAttack = _cfgAttack.Value;
            RenderState.HideBuild = _cfgBuild.Value;
            RenderState.HideOrderLines = _cfgOrderLines.Value;
            RenderState.HidePlannedBuildings = _cfgPlannedBuilds.Value;
            RenderState.HideAlloySpots = _cfgAlloySpots.Value;
            RenderState.HideHealthBars = _cfgHealthBars.Value;
            RenderState.HideGameUi = _cfgGameUi.Value;
            DrawDistance.Wanted = _cfgDrawDistance.Value;

            RenderState.Poll(Time.unscaledDeltaTime, Logger);

            if (Input.GetKeyDown(_cfgToggleKey.Value) && RenderState.Active) _open = !_open;

            UpdatePanel();
        }

        // ---- the panel -----------------------------------------------------

        // Canvas units: twice the 1080-logical pixels. The panel is at least
        // Inner wide inside its padding; rows of switches share it out.
        private const float Inner = 488f, RowH = 44f, Text = 22f, Head = 20f, Step = 56f;

        private HudPanel _panel;
        private TMP_Text _camHeight, _threshold, _distance, _nextMatch;
        private RectTransform _thresholdRow;
        // What the three number labels last showed; NaN until first drawn.
        private float _shownHeight = float.NaN, _shownThreshold = float.NaN, _shownDistance = float.NaN;
        private HudButton _show, _far, _never;
        private HudButton _intel, _attack, _build, _orderLines, _plannedBuilds, _alloySpots, _healthBars, _gameUi;

        private void UpdatePanel()
        {
            var showing = _open && RenderState.Active && !MenuOpen();
            if (showing && (_panel == null || !_panel.Alive))
            {
                var root = HudCanvas.EnsureOwn();
                if (root != null) Build(root);
            }
            if (_panel == null || !_panel.Alive) return;
            if (showing) HudCanvas.EnsureOwn();
            _panel.Show(showing);
            if (!showing) return;

            // The three numbers are formatted only when they change, not on
            // every frame the panel is up.
            var height = RenderState.CameraHeight;
            if (height != _shownHeight)
            {
                _shownHeight = height;
                HudCanvas.SetText(_camHeight, height < 0 ? "" : "cam " + height.ToString("0", CultureInfo.InvariantCulture));
            }

            var mode = _cfgIcons.Value;
            _show.SetOn(mode == IconMode.Show);
            _far.SetOn(mode == IconMode.HideWhenClose);
            _never.SetOn(mode == IconMode.Hide);
            var far = mode == IconMode.HideWhenClose;
            if (_thresholdRow.gameObject.activeSelf != far) _thresholdRow.gameObject.SetActive(far);
            if (_cfgIconHeight.Value != _shownThreshold)
            {
                _shownThreshold = _cfgIconHeight.Value;
                HudCanvas.SetText(_threshold, _shownThreshold.ToString("0", CultureInfo.InvariantCulture));
            }

            _intel.SetOn(_cfgIntel.Value);
            _attack.SetOn(_cfgAttack.Value);
            _build.SetOn(_cfgBuild.Value);
            _orderLines.SetOn(_cfgOrderLines.Value);
            _plannedBuilds.SetOn(_cfgPlannedBuilds.Value);
            _alloySpots.SetOn(_cfgAlloySpots.Value);
            _healthBars.SetOn(_cfgHealthBars.Value);
            _gameUi.SetOn(_cfgGameUi.Value);

            var distance = _cfgDrawDistance.Value;
            if (distance != _shownDistance)
            {
                _shownDistance = distance;
                HudCanvas.SetText(_distance, distance <= 0f ? "game default" : distance.ToString("0", CultureInfo.InvariantCulture));
            }
            // The distances are baked into the render prefabs as a match's
            // templates load, so a change here is not live — say so rather
            // than letting the button look broken.
            var pending = DrawDistance.Applied != distance;
            if (_nextMatch.gameObject.activeSelf != pending) _nextMatch.gameObject.SetActive(pending);

            var at = _panel.Place(new Vector2(_cfgPosX.Value, _cfgPosY.Value));
            if (_panel.TakeDragged())
            {
                _cfgPosX.Value = at.x;
                _cfgPosY.Value = at.y;
            }
        }

        private void Build(RectTransform root)
        {
            _panel = HudPanel.Create(root, "Camera utilities", () => false);
            var rect = _panel.Rect;
            _shownHeight = _shownThreshold = _shownDistance = float.NaN;

            var title = HudControls.Row(rect, "Title", 8f);
            HudControls.Size(title.gameObject, Inner, -1f);
            HudControls.Label(title, "Title", "CAMERA UTILITIES", Text, Color.white, TextAlignmentOptions.MidlineLeft);
            HudControls.Flexible(title);
            _camHeight = HudControls.Label(title, "Height", "", Text, HudControls.TextDim, TextAlignmentOptions.MidlineRight);

            Heading(rect, "SHOW STRATEGIC ICONS");
            var modes = HudControls.Row(rect, "Modes", 4f);
            _show = Switch(modes, "ALWAYS", () => _cfgIcons.Value = IconMode.Show);
            _far = Switch(modes, "WHEN FAR", () => _cfgIcons.Value = IconMode.HideWhenClose);
            _never = Switch(modes, "NEVER", () => _cfgIcons.Value = IconMode.Hide);

            _thresholdRow = HudControls.Row(rect, "Threshold", 4f);
            HudControls.Label(_thresholdRow, "Label", "far is above", Text, HudControls.TextMid, TextAlignmentOptions.MidlineLeft, 156f);
            HudButton.Create(_thresholdRow, "Less", "-", Step, RowH).OnClick = () => StepThreshold(-10f);
            _threshold = HudControls.Label(_thresholdRow, "Value", "", 24f, Color.white, TextAlignmentOptions.Center, 92f);
            HudButton.Create(_thresholdRow, "More", "+", Step, RowH).OnClick = () => StepThreshold(10f);

            Heading(rect, "HIDE");
            var ranges = HudControls.Row(rect, "Ranges", 4f);
            _intel = Toggle(ranges, "INTEL", _cfgIntel);
            _attack = Toggle(ranges, "ATTACK", _cfgAttack);
            _build = Toggle(ranges, "BUILD", _cfgBuild);
            var orders = HudControls.Row(rect, "Orders", 4f);
            _orderLines = Toggle(orders, "ORDER LINES", _cfgOrderLines);
            _plannedBuilds = Toggle(orders, "PLANNED BUILDS", _cfgPlannedBuilds);
            var markers = HudControls.Row(rect, "Markers", 4f);
            _alloySpots = Toggle(markers, "ALLOY SPOTS", _cfgAlloySpots);
            _healthBars = Toggle(markers, "HEALTH BARS", _cfgHealthBars);
            var ui = HudControls.Row(rect, "Game UI", 4f);
            _gameUi = Toggle(ui, "GAME UI", _cfgGameUi);

            HudControls.Cell(rect, "Gap", 0f, 2f);
            var drawHead = HudControls.Row(rect, "Draw distance heading", 8f);
            HudControls.Label(drawHead, "Heading", "UNIT DRAW DISTANCE", Head, HudControls.TextDim, TextAlignmentOptions.BottomLeft);
            HudControls.Flexible(drawHead);
            _nextMatch = HudControls.Label(drawHead, "Next match", "next match", Text, HudControls.TextDim, TextAlignmentOptions.BottomRight);
            var draw = HudControls.Row(rect, "Draw distance", 4f);
            HudButton.Create(draw, "Less", "-", Step, RowH).OnClick = () => StepDrawDistance(-250f);
            _distance = HudControls.Label(draw, "Value", "", 24f, Color.white, TextAlignmentOptions.Center, 192f);
            HudButton.Create(draw, "More", "+", Step, RowH).OnClick = () => StepDrawDistance(250f);
            HudControls.Flexible(draw);
            HudButton.Create(draw, "Off", "OFF", 88f, RowH).OnClick = () => _cfgDrawDistance.Value = 0f;

            var reset = HudControls.Row(rect, "Reset", 4f);
            HudButton.Create(reset, "Show everything", "SHOW EVERYTHING", 0f, RowH).Flexible().OnClick = ShowEverything;
        }

        private static void Heading(RectTransform parent, string text)
        {
            // A little air above each group, as the IMGUI panel had.
            HudControls.Cell(parent, "Gap", 0f, 2f);
            HudControls.Label(parent, "Heading", text, Head, HudControls.TextDim, TextAlignmentOptions.BottomLeft);
        }

        // Rows of switches share the panel's width evenly.
        private static HudButton Switch(Transform row, string label, Action onClick)
        {
            var button = HudButton.Create(row, label, label, 0f, RowH).Flexible();
            button.OnClick = onClick;
            return button;
        }

        // Lit means hidden, so the on-state is the loud one.
        private static HudButton Toggle(Transform row, string label, ConfigEntry<bool> entry) =>
            Switch(row, label, () => entry.Value = !entry.Value);

        private void StepThreshold(float delta)
        {
            _cfgIconHeight.Value = Mathf.Clamp(Mathf.Round((_cfgIconHeight.Value + delta) / 10f) * 10f, 0f, 1000f);
        }

        private void StepDrawDistance(float delta)
        {
            _cfgDrawDistance.Value = Mathf.Clamp(Mathf.Round((_cfgDrawDistance.Value + delta) / 250f) * 250f, 0f, 10000f);
        }

        private void ShowEverything()
        {
            _cfgIcons.Value = IconMode.Show;
            _cfgIntel.Value = false;
            _cfgAttack.Value = false;
            _cfgBuild.Value = false;
            _cfgOrderLines.Value = false;
            _cfgPlannedBuilds.Value = false;
            _cfgAlloySpots.Value = false;
            _cfgHealthBars.Value = false;
            _cfgGameUi.Value = false;
        }
    }
}
