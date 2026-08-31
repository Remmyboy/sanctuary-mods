using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Client-side HUD: the economy strip across the top and the commander
    // widget top-right. Presentation-only: reads state the game already sends
    // to the render side and draws an IMGUI overlay. Never touches the
    // lobby-hashed Lua tree or the simulation.
    //
    // The idle-engineers panel, the alloy panel and the map-local file
    // fallback are their own mods in this monorepo; the plumbing they share
    // with this one (economy stream, ECS poll, Lua bridge) lives in
    // shared\HudCore.cs and is compiled into each mod that needs it.
    [BepInPlugin("com.sanctuarydb.hud", "SanctuaryDB HUD", "0.6.0")]
    public class SanctuaryHudPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        // ---- config ----
        private ConfigEntry<bool> _cfgVisible;
        private ConfigEntry<KeyCode> _cfgToggleKey;

        private bool _visible = true;

        private void Awake()
        {
            _log ??= Logger;

            _cfgVisible = Config.Bind("Overlay", "Visible", true, "Show the overlay.");
            _cfgToggleKey = Config.Bind("Overlay", "ToggleKey", KeyCode.F10, "Key that shows/hides the overlay.");
            _cfgCommanderZoom = Config.Bind("Commander", "JumpZoomFactor", 0.5f,
                "How wide the camera sits after jumping to the commander, as a fraction of the current camera height. " +
                "Higher = further out. 0.5 keeps roughly your current zoom.");

            _visible = _cfgVisible.Value;

            _log.LogInfo($"SanctuaryDB HUD loaded (assembly {typeof(SanctuaryHudPlugin).Assembly.GetName().Version}). Unity {Application.unityVersion}.");
            try
            {
                _harmony = new Harmony("com.sanctuarydb.hud." + Guid.NewGuid().ToString("N").Substring(0, 8));
                ApplyEconomyPatch(_harmony);
            }
            catch (Exception e)
            {
                _log.LogError($"Economy patch failed (strip will stay empty): {e}");
            }
            _log.LogInfo($"Hotkeys: {_cfgToggleKey.Value} = toggle overlay, F9 = dump UI hierarchy to log.");
        }

        // Hot reload (or the mod manager) destroys and recreates the plugin;
        // drop our patches so the reloaded copy doesn't stack a second postfix.
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // ---- input --------------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                _visible = !_visible;
                _cfgVisible.Value = _visible;
            }
            if (Input.GetKeyDown(KeyCode.F9)) DumpHierarchy();

            SharedTick();
        }

        // ---- drawing ------------------------------------------------------

        private void OnGUI()
        {
            if (!_visible || !InMatch) return;
            EnsureStyles();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;

            DrawEconomyStrip(logicalWidth);
            DrawCommanderWidget(logicalWidth);

            GUI.matrix = previousMatrix;
        }

        private const float StripHeight = 48f;

        /// Smoothed [income, spend, net] per resource, so the readout doesn't
        /// jitter with per-tick noise.
        private static readonly Dictionary<string, float[]> _smooth = new Dictionary<string, float[]>();

        /// Compact number formatting — these values run to millions.
        private static string Fmt(float v)
        {
            var a = Mathf.Abs(v);
            if (a >= 1_000_000f) return (v / 1_000_000f).ToString("0.##") + "M";
            if (a >= 10_000f) return (v / 1_000f).ToString("0.#") + "K";
            return v.ToString("#,0");
        }

        // Full-width strip: alloy on the left, energy on the right. Each half
        // shows storage on top with gross in / gross out / net beside it, and a
        // capacity bar underneath whose length scales gently with storage size
        // and whose colour warns as the store heads for empty.
        private void DrawEconomyStrip(float width)
        {
            Dictionary<string, float> eco;
            lock (_ecoLock) eco = _eco;
            if (eco == null) return;

            var centre = width / 2f;
            GUI.DrawTexture(new Rect(0, 0, width, StripHeight), _texPanel);

            DrawStripHalf(eco, "alloy", "ALLOY", AlloyColour, 0f, centre);
            DrawStripHalf(eco, "energy", "ENERGY", EnergyColour, centre, centre);

            // Hairline separators.
            GUI.DrawTexture(new Rect(centre - 1, 6, 2, StripHeight - 12), _texBarBack);
            GUI.DrawTexture(new Rect(0, StripHeight - 1, width, 1), _texBarBack);
        }

        private void DrawStripHalf(Dictionary<string, float> eco, string key, string label, Color baseColour, float x, float w)
        {
            float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;

            var current = V("StorageCurrent");
            var limit = Mathf.Max(1f, V("StorageLimit"));
            var incomeRaw = V("GeneratedIncome") + V("HarvestIncome");
            // Lua sends these negated: Total = what builds asked for,
            // Stalled = what the economy actually paid out.
            var wantedRaw = -V("RequestedTotal");
            var spendRaw = -V("RequestedStalled");
            var netRaw = incomeRaw - spendRaw;
            var stalling = wantedRaw - spendRaw > 0.5f;

            if (!_smooth.TryGetValue(key, out var s)) _smooth[key] = s = new float[3];
            s[0] += (incomeRaw - s[0]) * 0.2f;
            s[1] += (spendRaw - s[1]) * 0.2f;
            s[2] += (netRaw - s[2]) * 0.15f;
            var income = s[0];
            var spend = s[1];
            var net = s[2];

            const float pad = 16f;
            var inner = w - pad * 2f;

            // --- row 1: label + storage on the left, flows on the right ---
            _stStripLabel.normal.textColor = baseColour;
            GUI.Label(new Rect(x + pad, 7f, 70f, 20f), label, _stStripLabel);

            var storageText = Fmt(current);
            var storageWidth = _stStripValue.CalcSize(new GUIContent(storageText)).x;
            GUI.Label(new Rect(x + pad + 58f, 3f, storageWidth + 8f, 24f), storageText, _stStripValue);
            GUI.Label(new Rect(x + pad + 58f + storageWidth + 8f, 8f, 90f, 18f), "/ " + Fmt(limit), _stStripMax);

            // Right cluster: +in  −out  net.
            var netText = (net >= 0f ? "+" : "−") + Fmt(Mathf.Abs(net)) + "/s";
            _stStripNet.normal.textColor = stalling ? DangerColour : net >= 0f ? GainColour : LossColour;
            GUI.Label(new Rect(x + w - pad - 108f, 4f, 108f, 22f), netText, _stStripNet);

            GUI.Label(new Rect(x + w - pad - 108f - 150f, 7f, 70f, 18f), "+" + Fmt(income), _stStripIn);
            GUI.Label(new Rect(x + w - pad - 108f - 76f, 7f, 70f, 18f), "−" + Fmt(spend), _stStripOut);

            // --- row 2: capacity bar ---
            var lengthFactor = Mathf.Clamp(0.45f + 0.15f * Mathf.Log10(limit / 400f), 0.45f, 1f);
            var barRect = new Rect(x + pad, 32f, inner * lengthFactor, 9f);
            GUI.DrawTexture(barRect, _texBarBack);

            var colour = FillColour(baseColour, current, net, stalling);
            var previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(current / limit), barRect.height), _texWhite);
            GUI.color = previous;

            // Warning chip rides at the end of the bar row.
            string chip = null;
            if (stalling) chip = "STALL −" + Fmt(wantedRaw - spendRaw) + "/s";
            else if (net < -0.5f)
            {
                var tte = current / -net;
                if (tte < 120f) chip = "empty in " + tte.ToString("0") + "s";
            }
            if (chip != null)
            {
                var chipWidth = _stStripChip.CalcSize(new GUIContent(chip)).x + 14f;
                var chipRect = new Rect(x + w - pad - chipWidth, 30f, chipWidth, 14f);
                GUI.color = stalling ? DangerColour : new Color(0.75f, 0.45f, 0.15f, 0.9f);
                GUI.DrawTexture(chipRect, _texWhite);
                GUI.color = previous;
                GUI.Label(chipRect, chip, _stStripChip);
            }
        }

        private static Color FillColour(Color baseColour, float stored, float net, bool stalling)
        {
            if (stalling) return DangerColour;
            if (net >= -0.5f) return baseColour;

            var timeToEmpty = stored / -net;
            var urgency = Mathf.Clamp01(1f - timeToEmpty / 45f);
            var colour = Color.Lerp(baseColour, DangerColour, urgency * 0.9f);
            if (timeToEmpty < 10f)
            {
                colour = Color.Lerp(colour, DangerColour, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f));
            }
            return colour;
        }

        // Commander button, top-right under the economy strip: click to select
        // the commander and fly the camera to it; the bar underneath is its
        // health, always visible so a commander under fire is obvious.
        private void DrawCommanderWidget(float width)
        {
            if (_commanderLocalIndex < 0) return;

            const float w = 108f;
            const float h = 66f;
            var rect = new Rect(width - w - 14f, StripHeight + 10f, w, h);
            var hover = rect.Contains(Event.current.mousePosition);

            GUI.DrawTexture(rect, _texPanel);
            if (hover) GUI.DrawTexture(rect, _texRowHover);

            var frac = _commanderMaxHealth > 0f ? Mathf.Clamp01(_commanderHealth / _commanderMaxHealth) : 1f;
            var hurt = frac < 0.999f;

            _stCmdLabel.normal.textColor = hurt && frac < 0.35f ? DangerColour : new Color(0.85f, 0.9f, 0.97f);
            GUI.Label(new Rect(rect.x, rect.y + 6f, rect.width, 20f), "COMMANDER", _stCmdLabel);

            // The game's own strategic icon, sampled out of its atlas.
            var previous = GUI.color;
            var iconRect = new Rect(rect.center.x - 13f, rect.y + 20f, 26f, 26f);
            if (_iconAtlas != null && _iconUvRects != null &&
                _commanderIconIndex >= 0 && _commanderIconIndex < _iconUvRects.Count)
            {
                GUI.color = _ownArmyColourUi ?? Color.white;
                GUI.DrawTextureWithTexCoords(iconRect, _iconAtlas, _iconUvRects[_commanderIconIndex]);
                GUI.color = previous;
            }
            else
            {
                GUI.color = hurt && frac < 0.35f ? DangerColour : new Color(0.55f, 0.78f, 1f, 0.95f);
                GUI.Label(new Rect(rect.x, rect.y + 20f, rect.width, 18f), "◆", _stCmdGlyph);
                GUI.color = previous;
            }

            // Health bar.
            var barRect = new Rect(rect.x + 10f, rect.yMax - 12f, rect.width - 20f, 6f);
            GUI.DrawTexture(barRect, _texBarBack);
            GUI.color = frac > 0.6f ? new Color(0.42f, 0.85f, 0.5f) : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour;
            GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * frac, barRect.height), _texWhite);
            GUI.color = previous;

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover)
            {
                _pendingCommander = true;
                _applyOnFrame = -1;
                Event.current.Use();
            }
        }

        // ---- diagnostics (F9) ---------------------------------------------

        private static void DumpHierarchy()
        {
            _log.LogInfo("=== UI hierarchy dump ===");
            var lines = 0;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                _log.LogInfo($"--- scene '{scene.name}' ---");
                foreach (var root in scene.GetRootGameObjects())
                {
                    DumpNode(root.transform, 0, ref lines);
                }
            }
            _log.LogInfo($"=== dump complete ({lines} nodes) ===");
        }

        private static void DumpNode(Transform node, int depth, ref int lines)
        {
            if (depth > 12 || lines > 6000) return;

            var rectInfo = "";
            if (node is RectTransform rect)
            {
                rectInfo = $" [rect {rect.rect.width:F0}x{rect.rect.height:F0} @ {rect.anchoredPosition.x:F0},{rect.anchoredPosition.y:F0}]";
            }
            var components = string.Join(",", node.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .Where(n => n != "Transform" && n != "RectTransform" && n != "CanvasRenderer"));

            _log.LogInfo($"{new string(' ', depth * 2)}{node.name}{(node.gameObject.activeInHierarchy ? "" : " (inactive)")}{rectInfo}{(components.Length > 0 ? " {" + components + "}" : "")}");
            lines++;

            for (var i = 0; i < node.childCount; i++)
            {
                DumpNode(node.GetChild(i), depth + 1, ref lines);
            }
        }
    }
}
