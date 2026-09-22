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
    // map (WorldOverlays.cs), the commander alerts (Alerts.cs), the mini-map
    // (MiniMap.cs), and stand-ins for the game's own panels along the bottom:
    // a compact orders row (OrdersBar.cs), a plainer unit card (InfoCard.cs),
    // the selection list as a row (SelectionRow.cs), the build options,
    // tabs and queue as rows (BuildStrip.cs; the rows are on the HUD's own
    // uGUI canvas, HudCanvas.cs, as clones of the game's buttons), and the tier
    // tabs put away when there is only one (TierTabs.cs).
    // Presentation-only: reads state the game already sends to the render
    // side and draws an IMGUI overlay. Never touches the lobby-hashed Lua
    // tree or the simulation.
    //
    // The idle-engineers panel, the alloy panel and the map-local file
    // fallback are their own mods in this monorepo; the plumbing they share
    // with this one (economy stream, ECS poll, Lua bridge) lives in
    // shared\HudCore.cs and is compiled into each mod that needs it.
    [BepInPlugin("com.sanctuarydb.hud", "SanctuaryDB HUD", "0.12.2")]
    public class SanctuaryHudPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        // ---- config ----
        private ConfigEntry<bool> _cfgVisible;
        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<bool> _cfgHideBuiltIn;
        private static ConfigEntry<float> _cfgScale;
        private ConfigEntry<bool> _cfgSanctuaryUi;
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
        private ConfigEntry<bool> _cfgAlertDisconnect;

        private bool _visible = true;
        private bool _menuOpen;

        private void Awake()
        {
            _log ??= Logger;

            _cfgVisible = Config.Bind("Overlay", "Visible", true, "Show the overlay.");
            _cfgToggleKey = Config.Bind("Overlay", "ToggleKey", KeyCode.F10, "Key that shows/hides the overlay.");
            _cfgHideBuiltIn = Config.Bind("Overlay", "HideGameEconomyBars", false,
                "Hide the game's own alloy and energy readouts at the top of the screen, so the strip is the only economy display. " +
                "The menu and pause buttons that share that panel move into the middle of the strip. " +
                "It all comes back whenever the overlay is hidden or the mod is unloaded.");
            _cfgScale = Config.Bind("Overlay", "Scale", 1f,
                new ConfigDescription("Size of the whole HUD — the economy strip, the commander widget, the orders row, the unit card, " +
                    "the selection row and the build strip — as a multiple of the standard size, on top of the game's own UI Scale.",
                    new AcceptableValueRange<float>(0.6f, 1.5f)));
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
                "Show a time-to-finish under each of your structures under construction (upgrades included): normal while it builds, " +
                "dark orange while your economy is stalling, red once nothing is building it. A paused upgrade that nothing is building is left out.");
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
            _cfgAlertDisconnect = Config.Bind("Alerts", "PlayerDisconnected", true,
                "White toast naming a player who drops out of the match (the game's own notice is small text top right), " +
                "and one when your connection to the host is lost. A player quitting reads the same as a disconnect.");

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
            // Shipped packs first, in preference order, then anything a
            // player added, then the tones; the first present is the default.
            var preferred = new[] { "machine", "announcer", "caretaker" };
            var onDisk = new List<string>(Alerts.AvailablePacks());
            var packs = new List<string>();
            foreach (var p in preferred) if (onDisk.Remove(p)) packs.Add(p);
            packs.AddRange(onDisk);
            packs.Add("tones");
            var defaultPack = packs[0];
            _cfgVoicePack = Config.Bind("Alerts", "VoicePack", defaultPack,
                new ConfigDescription(
                    "Which voice speaks the alerts: a subfolder of SanctuaryMods\\SanctuaryHud\\sounds, or the built-in tones.",
                    new AcceptableValueList<string>(packs.ToArray())));

            MiniMap.Bind(Config);
            // The stand-ins for the game's own bottom panels, under one switch:
            // with it off the game's panels stay as they are and only the
            // strip, the mini-map, the alerts and the map labels remain.
            _cfgSanctuaryUi = Config.Bind("SanctuaryUI", "Enabled", true,
                "The HUD's own versions of the game's bottom panels — the orders row, the unit card, the selection row and the build " +
                "strip with its tabs and queue — in place of the game's. Off leaves the game's panels untouched; the settings below then do nothing.");
            OrdersBar.Bind(Config);
            InfoCard.Bind(Config);
            SelectionRow.Bind(Config);
            TierTabs.Bind(Config);
            BuildStrip.Bind(Config);
            BottomDock.Bind(Config);

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
            try
            {
                Alerts.ApplyLogPatch(_harmony);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Disconnect toasts unavailable (log panel hook failed): {e.Message}");
            }
            // The stand-ins for the game's panels: one hook keeps a concealed
            // panel concealed through Lua's own visibility calls, the other
            // catches the unit card's values. Without them the game's own
            // panels stay as they are.
            try
            {
                PanelConceal.ApplyPatch(_harmony);
                InfoCard.ApplyPatch(_harmony);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Panel stand-ins unavailable (hook failed): {e.Message}");
                PanelConceal.Unavailable = true;
            }
            _log.LogInfo($"Hotkeys: {_cfgToggleKey.Value} = toggle overlay, F9 = dump UI hierarchy to log.");
        }

        // Hot reload (or the mod manager) destroys and recreates the plugin;
        // drop our patches so the reloaded copy doesn't stack a second postfix,
        // and give the game its readouts back.
        private void OnDestroy()
        {
            // Unpatch whatever else throws, or the reloaded copy's postfixes
            // would run beside these.
            try
            {
                GamePanel.Shutdown();
                Alerts.Shutdown();
                MiniMap.Shutdown();
                OrdersBar.Shutdown();
                InfoCard.Shutdown();
                SelectionRow.Shutdown();
                TierTabs.Shutdown();
                BuildStrip.Shutdown();
                BottomDock.Shutdown();
                EcoStrip.Shutdown();
                HudCanvas.Destroy();
            }
            finally
            {
                _harmony?.UnpatchSelf();
            }
        }

        // ---- input --------------------------------------------------------

        // ---- cost meter ------------------------------------------------------
        // How long the HUD's own Update and OnGUI take per frame, logged every
        // ten seconds while in a match, so a slow game can be blamed or cleared
        // from the log alone.
        private static readonly System.Diagnostics.Stopwatch _swUpdate = new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _swGui = new System.Diagnostics.Stopwatch();
        private static int _meterFrames;
        private static float _meterNext;

        private void LateUpdate()
        {
            _meterFrames++;
            if (Time.realtimeSinceStartup < _meterNext) return;
            _meterNext = Time.realtimeSinceStartup + 10f;
            if (_meterFrames > 0 && InMatch)
            {
                _log.LogInfo($"HUD cost: update {_swUpdate.Elapsed.TotalMilliseconds / _meterFrames:0.00} ms/frame, " +
                             $"gui {_swGui.Elapsed.TotalMilliseconds / _meterFrames:0.00} ms/frame over {_meterFrames} frames " +
                             $"({_meterFrames / 10f:0} fps).");
            }
            _swUpdate.Reset();
            _swGui.Reset();
            _meterFrames = 0;
        }

        private void Update()
        {
            _swUpdate.Start();
            try { UpdateInner(); }
            finally { _swUpdate.Stop(); }
        }

        private void UpdateInner()
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
            Alerts.DisconnectEnabled = _cfgAlertDisconnect.Value;
            WorldOverlays.Tick();
            Alerts.Tick();

            // The built-in readouts only go while the strip is standing in for
            // them: overlay on, in a match, option set. Anything else restores.
            // ...and while the strip is standing aside, the game's own
            // readouts have to come back, or the all-armies view would have no
            // economy display at all.
            GamePanel.SetBuiltInBarsHidden(_ecoPanel, _visible && InMatch && OwnArmyFocused && _cfgHideBuiltIn.Value, _log);
            _menuOpen = _visible && InMatch && GamePanel.GameMenuOpen();
            // The strip and the commander widget are one player's own numbers,
            // so they step aside in a replay's all-armies view: there is no
            // single economy to report there, and what was on screen was the
            // last seat's figures going stale. The mini-map stays — it is the
            // one thing here that reads just as well watching everybody.
            EcoStrip.Tick(_visible && InMatch && !_menuOpen && OwnArmyFocused, SliderScale);
            Alerts.SyncCanvas(_visible && InMatch && !_menuOpen, SliderScale);
            // The HUD canvas (the uGUI stand-ins) hides with the rest of the
            // HUD, and under the game's menus, as everything else here does.
            HudCanvas.SetShowing(_visible && InMatch && !_menuOpen);

            // The mini-map hides with the rest of the HUD, and under the
            // game's own menus, as everything else here does.
            MiniMap.Tick(Time.unscaledDeltaTime, _visible && !_menuOpen);

            // The game's own orders panel and unit card stay concealed under
            // its menus too (nothing of the HUD shows there anyway); their
            // stand-ins just don't draw. Only hiding the overlay, or leaving
            // the match, gives them back.
            var ui = _visible && _cfgSanctuaryUi.Value;
            // Docked in order: the orders row and the card make the left
            // column, the rows go against it, and the dock outlines the whole.
            BottomDock.Begin();
            OrdersBar.Tick(ui);
            InfoCard.Tick(ui);
            OrdersBar.FitColumn();
            SelectionRow.Tick(ui);
            // The build strip takes the tier tabs with it; TierTabs only
            // minds them while the strip is the game's own.
            BuildStrip.Tick(ui);
            TierTabs.Tick(ui && !BuildStrip.Active);
            BottomDock.End();
            // The domain map is per match: the sprite registry reloads with
            // each one, so it is dropped between matches and rebuilt.
            if (InMatch) UnitDomains.Tick();
            else UnitDomains.Reset();
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

        /// The smoothed [income, demand, spend] for a resource, or null before
        /// the first update.
        internal static float[] Smoothed(string key) => _smooth.TryGetValue(key, out var s) ? s : null;
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
            _swGui.Start();
            try { OnGuiInner(); }
            finally { _swGui.Stop(); }
        }

        private void OnGuiInner()
        {
            // Under the game's pause menu or a settings screen nothing of the
            // game's own shows through, so nothing of ours should either.
            if (!_visible || !InMatch || _menuOpen) return;
            // Only labels here, nothing that takes input: laying them out
            // for Layout and every mouse and key event too is wasted work.
            if (Event.current.type != EventType.Repaint) return;
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

            // The stand-ins for the game's own bottom panels are on the HUD
            // canvas (HudCanvas), not drawn here.

            // The strip, the commander widget, the alerts and the mini-map are
            // on the HUD canvas too (EcoStrip, Alerts, MiniMap).

            GUI.matrix = previousMatrix;
        }

        /// The one size setting, for everything the HUD draws in its own
        /// shape. The strip and the commander widget take it as it is; the
        /// rows and card on the canvas take it a fifth up, since at the
        /// strip's size they read small.
        private static float SliderScale => _cfgScale != null ? Mathf.Clamp(_cfgScale.Value, 0.6f, 1.5f) : 1f;
        internal static float HudScale => SliderScale * 1.2f;

        // The game's UI palette (Beam UI, as the front menu uses it): near-
        // black blue panels with a hairline of accent blue.
        internal static Color GamePanelColour => PanelColour;
        internal static Color GameAccent => AccentColour;
        internal static readonly Color MutedText = new Color(0.62f, 0.70f, 0.80f, 0.75f);

        private static bool _gameStyleReady;
        private static Color _alloyTint = AlloyColour;
        private static Color _energyTint = EnergyColour;

        /// The resource tints as the strip draws them: the game's own where
        /// they could be read off its panel, the fallbacks otherwise.
        internal static Color AlloyTint => _alloyTint;
        internal static Color EnergyTint => _energyTint;

        /// Once, in a match: put the game's typeface on the map labels and
        /// take its resource tints off the game's own panel, so the two read
        /// as one UI. Falls back to the built-in styles piece by piece.
        private void EnsureGameStyle()
        {
            if (_gameStyleReady) return;
            _gameStyleReady = true;

            WorldOverlays.ApplyFont(GamePanel.ResolveFont(_log));

            var alloy = AlloyColour;
            var energy = EnergyColour;
            GamePanel.SampleColours(_ecoPanel, ref alloy, ref energy);
            _alloyTint = alloy;
            _energyTint = energy;
        }

        /// Number formatting, matching the game's own readouts (SignedTextElement:
        /// K above 999, M above 999,999) so the two never disagree on the
        /// same figure.
        internal static string Fmt(float v)
        {
            var a = Mathf.Round(Mathf.Abs(v));
            if (a > 999_999_999f) return (a / 1_000_000_000f).ToString("0.###") + "B";
            if (a > 999_999f) return (a / 1_000_000f).ToString("0.##") + "M";
            if (a > 999f) return (a / 1_000f).ToString("0.#") + "K";
            return a.ToString("0");
        }

        /// The host's economy ticks ten times a second; the stream's rates are
        /// per second, its storage a plain amount (economyPanel.lua).
        private const float TicksPerSecond = 10f;

        /// Whether this resource is itself stalling: its spending was cut
        /// (actual under demand) and its own store can't cover a tick of
        /// demand, the test economy.lua throttles on (satisfaction is
        /// (current + income) / request, per tick). The spending cut alone
        /// isn't enough: construction draws alloy and energy together and is
        /// throttled by whichever is short, so an energy stall cuts alloy
        /// spending too, and read that way a full alloy store showed STALL.
        internal static bool IsStalling(float stored, float income, float wanted, float spent) =>
            wanted - spent > 0.5f && stored * TicksPerSecond + income < wanted - 0.5f;

        private static bool IsStalling(Dictionary<string, float> eco, string key)
        {
            float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;
            return IsStalling(V("StorageCurrent"), V("GeneratedIncome"), -V("RequestedTotal"), -V("RequestedStalled"));
        }

        /// Whether alloy or energy is stalling on the latest update, for the
        /// build countdowns: construction draws on both, so a stall in either
        /// slows every build.
        internal static bool EconomyStalling()
        {
            Dictionary<string, float> eco;
            lock (_ecoLock) eco = _eco;
            return eco != null && (IsStalling(eco, "alloy") || IsStalling(eco, "energy"));
        }

        internal static Color FillColour(Color baseColour, float stored, float net, bool stalling)
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
