using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SanctuaryHud.CameraUtils
{
    // Switches off the bits of the game the camera draws over the world —
    // strategic icons, intel and attack range rings, health bars, the UI HUD —
    // for recording cinematics, or just for a cleaner picture.
    //
    // Every switch is in two places: the mod's settings on the front menu's
    // Mods page (bound config entries, so they show up there for free), and a
    // small in-match panel on a hotkey, because during a shot you want them
    // without leaving the game. The config entries are the single source of
    // truth — the panel writes to them, so a change either way persists and
    // both views agree.
    //
    // The work itself is in RenderState; this is the config, the hotkey and
    // the panel.
    [BepInPlugin("com.sanctuarydb.camerautilities", "Camera Utilities", "0.1.0")]
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
        private ConfigEntry<bool> _cfgHealthBars;
        private ConfigEntry<bool> _cfgGameUi;

        private bool _open;
        private Rect _rect = new Rect(12, 420, 0, 0);
        private int _lastRowCount = -1;

        private const float PanelW = 268f;

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

            _cfgHealthBars = Config.Bind("Cinematic", "HideHealthBars", false,
                "Hide every health and progress bar.");
            _cfgGameUi = Config.Bind("Cinematic", "HideGameUI", false,
                "Hide the game's whole UI HUD. This mod's own panel stays up, so the hotkey still gets it back.");

            _rect.x = _cfgPosX.Value;
            _rect.y = _cfgPosY.Value;

            Logger.LogInfo($"Camera Utilities loaded: {_cfgToggleKey.Value} opens the panel in a match, " +
                           "and the same switches are on the Mods page.");
        }

        private void OnDestroy()
        {
            // Unloading the mod has to leave the client as the game had it:
            // the wrapper comes off RenderUpdate and every flag goes back.
            RenderState.Uninstall();
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
            RenderState.HideHealthBars = _cfgHealthBars.Value;
            RenderState.HideGameUi = _cfgGameUi.Value;

            RenderState.Poll(Time.unscaledDeltaTime, Logger);

            if (Input.GetKeyDown(_cfgToggleKey.Value) && RenderState.Active) _open = !_open;

            // Persist the panel position once the drag is over.
            if (_open && !Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _rect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _rect.y) > 0.5f))
            {
                _cfgPosX.Value = _rect.x;
                _cfgPosY.Value = _rect.y;
            }
        }

        // ---- the panel -----------------------------------------------------

        private void OnGUI()
        {
            if (!_open || !RenderState.Active) return;
            EnsureUi();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            try
            {
                var logicalWidth = Screen.width / scale;
                var logicalHeight = Screen.height / scale;

                // A layout window grows to its content but never shrinks, so
                // zero the height when the row count changes — the threshold
                // row only exists in one of the three icon modes.
                var rows = _cfgIcons.Value == IconMode.HideWhenClose ? 1 : 0;
                if (rows != _lastRowCount)
                {
                    _lastRowCount = rows;
                    _rect.height = 0;
                }

                _rect.x = Mathf.Clamp(_rect.x, -PanelW + 80, logicalWidth - 80);
                _rect.y = Mathf.Clamp(_rect.y, 0, logicalHeight - 40);
                _rect = GUILayout.Window(0x43414D55, _rect, DrawPanel, GUIContent.none, _stPanel, GUILayout.Width(PanelW));
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }

        private void DrawPanel(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("CAMERA UTILITIES", _stTitle);
            GUILayout.FlexibleSpace();
            var height = RenderState.CameraHeight;
            GUILayout.Label(height < 0 ? "" : $"cam {height:0}", _stDim);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("SHOW STRATEGIC ICONS", _stHead);
            GUILayout.BeginHorizontal();
            var mode = _cfgIcons.Value;
            if (ModeButton("ALWAYS", mode, IconMode.Show)) _cfgIcons.Value = IconMode.Show;
            if (ModeButton("WHEN FAR", mode, IconMode.HideWhenClose)) _cfgIcons.Value = IconMode.HideWhenClose;
            if (ModeButton("NEVER", mode, IconMode.Hide)) _cfgIcons.Value = IconMode.Hide;
            GUILayout.EndHorizontal();

            if (mode == IconMode.HideWhenClose)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("far is above", _stBody, GUILayout.Width(78));
                if (GUILayout.Button("-", _stButton, GUILayout.Width(28))) StepThreshold(-10f);
                GUILayout.Label($"{_cfgIconHeight.Value:0}", _stValue, GUILayout.Width(46));
                if (GUILayout.Button("+", _stButton, GUILayout.Width(28))) StepThreshold(10f);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            GUILayout.Label("HIDE", _stHead);
            GUILayout.BeginHorizontal();
            _cfgIntel.Value = Toggle(_cfgIntel.Value, "INTEL", GUILayout.Width(76));
            _cfgAttack.Value = Toggle(_cfgAttack.Value, "ATTACK", GUILayout.Width(76));
            _cfgBuild.Value = Toggle(_cfgBuild.Value, "BUILD", GUILayout.Width(76));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _cfgHealthBars.Value = Toggle(_cfgHealthBars.Value, "HEALTH BARS", GUILayout.Width(118));
            _cfgGameUi.Value = Toggle(_cfgGameUi.Value, "GAME UI", GUILayout.Width(118));
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            if (GUILayout.Button("SHOW EVERYTHING", _stButton)) ShowEverything();

            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void StepThreshold(float delta)
        {
            _cfgIconHeight.Value = Mathf.Clamp(Mathf.Round((_cfgIconHeight.Value + delta) / 10f) * 10f, 0f, 1000f);
        }

        private void ShowEverything()
        {
            _cfgIcons.Value = IconMode.Show;
            _cfgIntel.Value = false;
            _cfgAttack.Value = false;
            _cfgBuild.Value = false;
            _cfgHealthBars.Value = false;
            _cfgGameUi.Value = false;
        }

        /// One of a set of mutually exclusive modes. Returns true on the frame
        /// it is picked, so the caller can commit the new mode.
        private static bool ModeButton(string label, IconMode current, IconMode value)
        {
            return GUILayout.Toggle(current == value, label, _stToggle, GUILayout.Width(76)) && current != value;
        }

        private static bool Toggle(bool on, string label, params GUILayoutOption[] options)
        {
            return GUILayout.Toggle(on, label, _stToggle, options);
        }

        // ---- look ----------------------------------------------------------

        private static readonly Color Accent = new Color(0.95f, 0.55f, 0.2f, 0.95f);
        private static readonly Color TextDim = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color TextMid = new Color(1f, 1f, 1f, 0.78f);

        private static bool _uiReady;
        private static GUIStyle _stPanel, _stTitle, _stHead, _stBody, _stDim, _stValue, _stButton, _stToggle;

        private static void EnsureUi()
        {
            if (_uiReady) return;
            _uiReady = true;

            _stPanel = new GUIStyle
            {
                normal = { background = Tex(new Color(0.04f, 0.06f, 0.08f, 0.88f)) },
                padding = new RectOffset(12, 12, 10, 12),
            };
            _stTitle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _stHead = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft, normal = { textColor = TextDim }, margin = new RectOffset(2, 2, 2, 2) };
            _stBody = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextMid }, margin = new RectOffset(2, 2, 4, 2) };
            _stDim = new GUIStyle(_stBody) { normal = { textColor = TextDim }, alignment = TextAnchor.MiddleRight };
            _stValue = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, margin = new RectOffset(2, 2, 4, 2) };
            _stButton = new GUIStyle
            {
                fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { background = Tex(new Color(1f, 1f, 1f, 0.09f)), textColor = TextMid },
                hover = { background = Tex(new Color(1f, 1f, 1f, 0.17f)), textColor = Color.white },
                active = { background = Tex(new Color(1f, 1f, 1f, 0.17f)), textColor = Color.white },
                padding = new RectOffset(6, 6, 3, 3),
                margin = new RectOffset(2, 2, 2, 2),
                fixedHeight = 22,
                clipping = TextClipping.Clip,
            };
            // Lit means hidden, so the on-state is the loud one.
            _stToggle = new GUIStyle(_stButton)
            {
                onNormal = { background = Tex(Accent), textColor = new Color(0.08f, 0.06f, 0.04f) },
                onHover = { background = Tex(new Color(1f, 0.65f, 0.3f, 1f)), textColor = new Color(0.08f, 0.06f, 0.04f) },
                onActive = { background = Tex(new Color(1f, 0.65f, 0.3f, 1f)), textColor = new Color(0.08f, 0.06f, 0.04f) },
            };
        }

        private static Texture2D Tex(Color colour)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, colour);
            tex.Apply();
            return tex;
        }
    }
}
