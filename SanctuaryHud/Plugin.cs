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
    // widget top-right, reclaim values and build countdowns drawn over the
    // map (WorldOverlays.cs), and the commander alerts (Alerts.cs).
    // Presentation-only: reads state the game already sends to the render
    // side and draws an IMGUI overlay. Never touches the lobby-hashed Lua
    // tree or the simulation.
    //
    // The idle-engineers panel, the alloy panel and the map-local file
    // fallback are their own mods in this monorepo; the plumbing they share
    // with this one (economy stream, ECS poll, Lua bridge) lives in
    // shared\HudCore.cs and is compiled into each mod that needs it.
    [BepInPlugin("com.sanctuarydb.hud", "SanctuaryDB HUD", "0.8.0")]
    public class SanctuaryHudPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        // ---- config ----
        private ConfigEntry<bool> _cfgVisible;
        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<bool> _cfgHideBuiltIn;
        private ConfigEntry<bool> _cfgReclaim;
        private ConfigEntry<KeyCode> _cfgReclaimHoldKey;
        private ConfigEntry<float> _cfgReclaimMinValue;
        private ConfigEntry<float> _cfgReclaimCluster;
        private ConfigEntry<bool> _cfgBuildEta;
        private ConfigEntry<int> _cfgBuildEtaMax;
        private ConfigEntry<bool> _cfgAlertAttacked;
        private ConfigEntry<bool> _cfgAlertCritical;
        private ConfigEntry<float> _cfgAlertCriticalAt;
        private ConfigEntry<bool> _cfgAlertBuildComplete;
        private ConfigEntry<bool> _cfgCompleteTier4;
        private readonly Dictionary<string, ConfigEntry<bool>> _cfgCompleteRules = new Dictionary<string, ConfigEntry<bool>>();
        private ConfigEntry<bool> _cfgAlertSound;
        private ConfigEntry<int> _cfgAlertVolume;
        private ConfigEntry<string> _cfgVoicePack;

        private bool _visible = true;

        private void Awake()
        {
            _log ??= Logger;

            _cfgVisible = Config.Bind("Overlay", "Visible", true, "Show the overlay.");
            _cfgToggleKey = Config.Bind("Overlay", "ToggleKey", KeyCode.F10, "Key that shows/hides the overlay.");
            _cfgHideBuiltIn = Config.Bind("Overlay", "HideGameEconomyBars", false,
                "Hide the game's own alloy and energy readouts at the top of the screen, so the strip is the only economy display. " +
                "The menu and pause buttons stay. They come back whenever the overlay is hidden or the mod is unloaded.");
            _cfgCommanderZoom = Config.Bind("Commander", "JumpZoomFactor", 0.5f,
                "How wide the camera sits after jumping to the commander, as a fraction of the current camera height. " +
                "Higher = further out. 0.5 keeps roughly your current zoom.");

            _cfgReclaim = Config.Bind("Reclaim", "Enabled", true,
                "Draw the alloy (and energy) value of wrecks and harvestable props over the map. Zoomed out, nearby values are summed into one figure.");
            _cfgReclaimHoldKey = Config.Bind("Reclaim", "HoldKey", KeyCode.LeftAlt,
                "Only show reclaim values while this key is held. None = always shown.");
            _cfgReclaimMinValue = Config.Bind("Reclaim", "MinValue", 5f,
                "Hide figures below this many alloys (energy counts a tenth).");
            _cfgReclaimCluster = Config.Bind("Reclaim", "ClusterPixels", 110f,
                "How close two values can sit on screen before they are summed into one figure, in pixels at 1080p. Smaller = more, finer numbers.");

            _cfgBuildEta = Config.Bind("BuildEta", "Enabled", true,
                "Show a time-to-finish under each of your structures under construction (upgrades included). Turns red when nothing is building it.");
            _cfgBuildEtaMax = Config.Bind("BuildEta", "MaxLabels", 12,
                "At most this many countdowns at once, soonest first; ones that would overlap another are skipped.");

            _cfgAlertAttacked = Config.Bind("Alerts", "CommanderUnderAttack", true,
                "Toast and tone when the commander loses health. Click the toast to jump to it.");
            _cfgAlertCritical = Config.Bind("Alerts", "CommanderCritical", true,
                "Toast and tone once when the commander drops below the critical fraction; re-arms after it is repaired.");
            _cfgAlertCriticalAt = Config.Bind("Alerts", "CriticalFraction", 0.35f,
                "Health fraction that counts as critical.");
            _cfgAlertBuildComplete = Config.Bind("Alerts", "StructureComplete", true,
                "Toast when one of your structures finishes building. Which ones is set under CompleteToasts.");

            // One switch per kind of completion, so the Mod Manager lists
            // them as rows. Upgrades (a tier-1 factory becoming tier 2, a
            // radar becoming the next tier) are the ones worth interrupting
            // for; a fresh extractor or generator is not, by default.
            _cfgCompleteTier4 = Config.Bind("CompleteToasts", "AnyTier4", true,
                "Any tier-4 structure finishing, whatever it is.");
            void Rule(string role, string label, bool newDefault, bool upgradeDefault)
            {
                _cfgCompleteRules[role + ".new"] = Config.Bind("CompleteToasts", label + "Built", newDefault, $"A new {label.ToLowerInvariant()} finishes building.");
                _cfgCompleteRules[role + ".upgrade"] = Config.Bind("CompleteToasts", label + "Upgraded", upgradeDefault, $"A {label.ToLowerInvariant()} finishes upgrading to its next tier.");
            }
            Rule("factory", "Factory", false, true);
            Rule("intel", "Radar", false, true);
            Rule("extractor", "Extractor", false, false);
            Rule("energy", "Energy", false, false);
            Rule("defence", "Defence", false, false);
            Rule("tech", "TechCentre", true, true);
            Rule("strategic", "Strategic", true, true);
            Rule("other", "Other", false, false);
            _cfgAlertSound = Config.Bind("Alerts", "Sound", false,
                "Play a sound with each alert: a voice line from the mod's sounds folder where one is shipped, else a short tone. Off by default; the toasts show either way.");
            _cfgAlertVolume = Config.Bind("Alerts", "Volume", 50,
                new ConfigDescription("Alert volume, 0 to 100, like the game's own audio sliders.", new AcceptableValueRange<int>(0, 100)));
            // The packs on disk plus the built-in tones, as a fixed list so
            // the Mod Manager offers them as a chooser rather than a text box.
            // Read once at load; a pack added later shows after a reload.
            var packs = new List<string>(Alerts.AvailablePacks()) { "tones" };
            var defaultPack = packs.Contains("caretaker") ? "caretaker" : packs[0];
            _cfgVoicePack = Config.Bind("Alerts", "VoicePack", defaultPack,
                new ConfigDescription(
                    "Which voice speaks the alerts: a subfolder of SanctuaryMods\\SanctuaryHud\\sounds, or the built-in tones.",
                    new AcceptableValueList<string>(packs.ToArray())));

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
        // drop our patches so the reloaded copy doesn't stack a second postfix,
        // and give the game its readouts back.
        private void OnDestroy()
        {
            GamePanel.Restore();
            Alerts.Shutdown();
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
            StepSmoothing();

            // Config is read every frame so the Mod Manager's settings page
            // takes effect at once; the entries are cheap to read.
            WorldOverlays.ReclaimEnabled = _cfgReclaim.Value;
            WorldOverlays.BuildEtaEnabled = _cfgBuildEta.Value || _cfgAlertBuildComplete.Value;
            Alerts.AttackedEnabled = _cfgAlertAttacked.Value;
            Alerts.CriticalEnabled = _cfgAlertCritical.Value;
            Alerts.CriticalFraction = Mathf.Clamp(_cfgAlertCriticalAt.Value, 0.05f, 0.9f);
            Alerts.BuildCompleteEnabled = _cfgAlertBuildComplete.Value;
            Alerts.CompleteAnyTier4 = _cfgCompleteTier4.Value;
            foreach (var kv in _cfgCompleteRules) Alerts.CompleteRules[kv.Key] = kv.Value.Value;
            Alerts.SoundEnabled = _cfgAlertSound.Value;
            Alerts.Volume = _cfgAlertVolume.Value / 100f;
            Alerts.VoicePack = _cfgVoicePack.Value;
            WorldOverlays.Tick();
            Alerts.Tick();

            // The built-in readouts only go while the strip is standing in for
            // them: overlay on, in a match, option set. Anything else restores.
            GamePanel.SetBuiltInBarsHidden(_ecoPanel, _visible && InMatch && _cfgHideBuiltIn.Value, _log);
        }

        // ---- economy smoothing --------------------------------------------

        /// Smoothed [income, demand, spend] per resource. Filtered once per
        /// frame on real elapsed time towards the latest update, with a fixed
        /// time constant, so the readout is the same at 30 fps and 240 fps
        /// and both halves of the strip trail the game's own numbers by the
        /// same small amount. (It used to step on every IMGUI event, which
        /// is two or more per frame and varies with input, so the lag
        /// depended on the frame rate.)
        ///
        /// It must step every frame, not only when an update changes: the
        /// spend jumps when a factory starts and then holds exactly steady,
        /// and a filter that only moved on changes froze a third of the way
        /// there (50 showed as 16) until something else in the stream moved.
        private static readonly Dictionary<string, float[]> _smooth = new Dictionary<string, float[]>();
        private static int _seenSequence = -1;

        /// Time constant of the filter, in seconds. Short: the aim is to take
        /// the edge off bursty reclaim, not to trail the game's numbers.
        private const float SmoothTau = 0.25f;

        private static void StepSmoothing()
        {
            Dictionary<string, float> eco;
            lock (_ecoLock) eco = _eco;
            if (eco == null)
            {
                // Between matches: forget the last game's rates so the next
                // one doesn't open on them.
                if (_smooth.Count > 0) _smooth.Clear();
                _seenSequence = -1;
                return;
            }

            // Snap on the first update of a match, where sliding in from
            // zero would itself be a visible disagreement with the game.
            var sequence = _ecoSequence;
            var first = _seenSequence < 0;
            _seenSequence = sequence;
            var dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 1f);
            var alpha = first ? 1f : 1f - Mathf.Exp(-dt / SmoothTau);

            foreach (var key in new[] { "alloy", "energy" })
            {
                float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;
                // GeneratedIncome already includes harvest: economy.lua sets
                // res.income = generation + harvest, and that is what Lua ships
                // as GeneratedIncome. Adding HarvestIncome on top would
                // double-count reclaim — it only looks harmless today because
                // economyPanel.lua assigns alloyHarvestIncome twice in one
                // table constructor (real value, then 0 beside a TODO), so the
                // zero wins and it always arrives empty.
                var income = V("GeneratedIncome");
                // Lua sends these negated (economyPanel.lua): RequestedTotal is
                // "how much we wanted to spend", RequestedStalled "how much we
                // actually spent".
                var demand = -V("RequestedTotal");
                var spend = -V("RequestedStalled");

                if (!_smooth.TryGetValue(key, out var s)) _smooth[key] = s = new float[3];
                s[0] += (income - s[0]) * alpha;
                s[1] += (demand - s[1]) * alpha;
                s[2] += (spend - s[2]) * alpha;
            }
        }

        // ---- drawing ------------------------------------------------------

        private void OnGUI()
        {
            if (!_visible || !InMatch) return;
            EnsureStyles();
            EnsureGameStyle();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;
            var logicalHeight = Screen.height / scale;

            // Map-anchored labels go first so the strip and widgets sit over
            // them rather than the other way round.
            var holdKey = _cfgReclaimHoldKey.Value;
            if (_cfgReclaim.Value && (holdKey == KeyCode.None || Input.GetKey(holdKey)))
            {
                WorldOverlays.DrawReclaim(scale, logicalWidth, logicalHeight, _cfgReclaimCluster.Value, _cfgReclaimMinValue.Value);
            }
            if (_cfgBuildEta.Value) WorldOverlays.DrawBuildEtas(scale, logicalWidth, logicalHeight, _cfgBuildEtaMax.Value);

            DrawEconomyStrip(logicalWidth);
            DrawCommanderWidget(logicalWidth);
            Alerts.Draw(logicalWidth, StripHeight + 12f, _texStrip);

            GUI.matrix = previousMatrix;
        }

        private const float StripHeight = 48f;

        // The game's UI palette (Beam UI, as the front menu uses it): near-
        // black blue panels with a hairline of accent blue.
        private static readonly Color GamePanelColour = new Color(0.098f, 0.137f, 0.176f, 0.80f);   // #19232D
        private static readonly Color GameAccent = new Color(0.239f, 0.686f, 1f);                    // #3DAFFF
        private static readonly Color MutedText = new Color(0.62f, 0.70f, 0.80f, 0.75f);

        private static Texture2D _texStrip;
        private static bool _gameStyleReady;
        private static Color _alloyTint = AlloyColour;
        private static Color _energyTint = EnergyColour;

        /// Once, in a match: put the game's typeface on the strip and take
        /// its resource tints off the game's own panel, so the two read as
        /// one UI. Falls back to the built-in styles piece by piece.
        private void EnsureGameStyle()
        {
            if (_gameStyleReady) return;
            _gameStyleReady = true;

            _texStrip = MakeTexture(GamePanelColour);

            var font = GamePanel.ResolveFont(_log);
            if (font != null)
            {
                foreach (var style in new[] { _stStripLabel, _stStripValue, _stStripMax, _stStripIn, _stStripOut, _stStripNet, _stStripChip, _stCmdLabel })
                {
                    style.font = font;
                }
                // Rajdhani/Bahnschrift run narrower and lighter than the
                // default face; a size up keeps the strip legible.
                _stStripLabel.fontSize = 14;
                _stStripValue.fontSize = 22;
                _stStripMax.fontSize = 14;
                _stStripIn.fontSize = 15;
                _stStripOut.fontSize = 15;
                _stStripNet.fontSize = 19;
                _stStripChip.fontSize = 12;
                _stCmdLabel.fontSize = 12;
            }
            WorldOverlays.ApplyFont(font);
            Alerts.ApplyFont(font);
            _stStripMax.normal.textColor = MutedText;

            var alloy = AlloyColour;
            var energy = EnergyColour;
            GamePanel.SampleColours(_ecoPanel, ref alloy, ref energy);
            _alloyTint = alloy;
            _energyTint = energy;
        }

        private static Texture2D MakeTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// Number formatting, matching the game's own readouts (SignedTextElement:
        /// K above 999, M above 999,999) so the two never disagree on the
        /// same figure.
        private static string Fmt(float v)
        {
            var a = Mathf.Round(Mathf.Abs(v));
            if (a > 999_999_999f) return (a / 1_000_000_000f).ToString("0.###") + "B";
            if (a > 999_999f) return (a / 1_000_000f).ToString("0.##") + "M";
            if (a > 999f) return (a / 1_000f).ToString("0.#") + "K";
            return a.ToString("0");
        }

        private static void Fill(Rect rect, Color colour)
        {
            var previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _texWhite);
            GUI.color = previous;
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
            GUI.DrawTexture(new Rect(0, 0, width, StripHeight), _texStrip);

            DrawStripHalf(eco, "alloy", "ALLOY", _alloyTint, 0f, centre);
            DrawStripHalf(eco, "energy", "ENERGY", _energyTint, centre, centre);

            // The game's panels sit on a hairline of accent blue; give the
            // strip the same edge, with a short fade under it so it lifts
            // off the map rather than ending in a hard line.
            var accent = GameAccent;
            accent.a = 0.35f;
            Fill(new Rect(centre - 0.5f, 10f, 1f, StripHeight - 20f), accent);
            accent.a = 0.6f;
            Fill(new Rect(0, StripHeight - 1f, width, 1f), accent);
            for (var i = 0; i < 4; i++)
            {
                Fill(new Rect(0, StripHeight + i, width, 1f), new Color(0f, 0f, 0f, 0.28f - i * 0.07f));
            }
        }

        private void DrawStripHalf(Dictionary<string, float> eco, string key, string label, Color baseColour, float x, float w)
        {
            float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;

            var current = V("StorageCurrent");
            var limit = Mathf.Max(1f, V("StorageLimit"));
            var wantedRaw = -V("RequestedTotal");
            var spendRaw = -V("RequestedStalled");
            var stalling = wantedRaw - spendRaw > 0.5f;

            var s = _smooth.TryGetValue(key, out var smoothed) ? smoothed : new[] { V("GeneratedIncome"), wantedRaw, spendRaw };
            var income = s[0];
            // The spend figure shows demand, not what the economy managed to
            // pay: while stalling those differ, and the useful number is what
            // your queue is asking for. Actual spend is capped by income, so
            // showing it just mirrors the income back at you (+12 −12) and
            // hides the shortfall. Off a stall the two are equal anyway.
            var demand = s[1];
            // Net stays on actual spend: it describes the store's real
            // movement, which is what the bar and the "empty in" chip need.
            // Derived from the same filtered figures as the income, so the
            // two never trail the game by different amounts.
            var net = s[0] - s[2];

            const float pad = 16f;
            var inner = w - pad * 2f;

            // --- row 1: label + storage on the left, flows on the right ---
            _stStripLabel.normal.textColor = baseColour;
            GUI.Label(new Rect(x + pad, 7f, 70f, 20f), label, _stStripLabel);

            var storageText = Fmt(current);
            var storageWidth = _stStripValue.CalcSize(new GUIContent(storageText)).x;
            GUI.Label(new Rect(x + pad + 66f, 2f, storageWidth + 8f, 26f), storageText, _stStripValue);
            GUI.Label(new Rect(x + pad + 66f + storageWidth + 8f, 8f, 90f, 18f), "/ " + Fmt(limit), _stStripMax);

            // Right cluster: +in  −out  net.
            var netText = (net >= 0f ? "+" : "−") + Fmt(net) + "/s";
            _stStripNet.normal.textColor = stalling ? DangerColour : net >= 0f ? GainColour : LossColour;
            GUI.Label(new Rect(x + w - pad - 108f, 4f, 108f, 22f), netText, _stStripNet);

            GUI.Label(new Rect(x + w - pad - 108f - 150f, 7f, 70f, 18f), "+" + Fmt(income), _stStripIn);
            // Flag the spend figure while stalling, since it is then demand
            // you are not actually meeting rather than resources leaving the
            // store — the STALL chip below carries the size of the shortfall.
            _stStripOut.normal.textColor = stalling ? DangerColour : LossColour;
            GUI.Label(new Rect(x + w - pad - 108f - 76f, 7f, 70f, 18f), "−" + Fmt(demand), _stStripOut);

            // --- row 2: capacity bar ---
            // A thin line in the resource colour on an accent-tinted track,
            // the way the game draws its own gauges, rather than a block.
            var lengthFactor = Mathf.Clamp(0.45f + 0.15f * Mathf.Log10(limit / 400f), 0.45f, 1f);
            var barRect = new Rect(x + pad, 36f, inner * lengthFactor, 4f);
            var track = GameAccent;
            track.a = 0.14f;
            Fill(barRect, track);

            var colour = FillColour(baseColour, current, net, stalling);
            var fillWidth = barRect.width * Mathf.Clamp01(current / limit);
            Fill(new Rect(barRect.x, barRect.y, fillWidth, barRect.height), colour);
            if (fillWidth > 2f) Fill(new Rect(barRect.x + fillWidth - 1f, barRect.y - 1f, 1f, barRect.height + 2f), new Color(1f, 1f, 1f, 0.75f));

            // Warning chip rides at the end of the bar row.
            string chip = null;
            if (stalling) chip = "STALL −" + Fmt(wantedRaw - spendRaw) + "/s";
            else if (net < -0.5f)
            {
                var tte = current / -net;
                if (tte < 120f) chip = "EMPTY IN " + tte.ToString("0") + "s";
            }
            if (chip != null)
            {
                var chipWidth = _stStripChip.CalcSize(new GUIContent(chip)).x + 14f;
                var chipRect = new Rect(x + w - pad - chipWidth, 29f, chipWidth, 15f);
                Fill(chipRect, stalling ? DangerColour : new Color(0.75f, 0.45f, 0.15f, 0.9f));
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

            GUI.DrawTexture(rect, _texStrip);
            if (hover) GUI.DrawTexture(rect, _texRowHover);
            var edge = GameAccent;
            edge.a = hover ? 0.8f : 0.45f;
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), edge);

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
            var barRect = new Rect(rect.x + 10f, rect.yMax - 12f, rect.width - 20f, 4f);
            var track = GameAccent;
            track.a = 0.14f;
            Fill(barRect, track);
            Fill(new Rect(barRect.x, barRect.y, barRect.width * frac, barRect.height),
                frac > 0.6f ? new Color(0.42f, 0.85f, 0.5f) : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour);

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
