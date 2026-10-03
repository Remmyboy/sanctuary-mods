using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Role-based build hotkeys, with tier cycling.
    //
    // The game's own construction hotkeys are nine fixed letters resolved by
    // tag category, first displayed match wins (constructionPanelHotkeys.lua).
    // That means you cannot bind a specific thing — T1 and T3 tanks are both
    // Tags.TANK, so only one is reachable — and whole categories (shields,
    // artillery, air and naval factories, tech centres, walls, storage) have
    // no key at all; their buttons render with '?' on them.
    //
    // This replaces them with one key per *role*. A role is a tag expression,
    // so it resolves per faction on its own: PointDefence is
    // DEFENCE * ANTI_SURFACE * STRUCTURE, which is ues1001/ucs1001/ugs1001 at
    // T1 and ucs3001 only for Chosen at T3. Pressing the key gives the highest
    // tier the selection can actually build; pressing it again cycles down.
    //
    // Nothing here edits a Lua file, so ComputeLuaHash is untouched and a
    // modded client still joins unmodded lobbies. The binding is a runtime
    // insert into inputSystem.lua's LoadedActionMap, which CallAction reads
    // live on every event, and the build itself goes through the construction
    // panel's own click handler — so it takes the same observer check, the
    // same local prediction and the same host-validated command that clicking
    // the button does.
    [BepInPlugin("com.sanctuarydb.buildhotkeys", "Build Hotkeys", "0.5.0")]
    public class BuildHotkeysPlugin : BaseUnityPlugin
    {
        private readonly Dictionary<string, ConfigEntry<string>> _cfgKeys =
            new Dictionary<string, ConfigEntry<string>>();

        // The game's own hotkeys, keyed "Group.Action", in the order bound:
        // the catalogue first, then anything the live table adds to it.
        private readonly Dictionary<string, ConfigEntry<string>> _cfgRemaps =
            new Dictionary<string, ConfigEntry<string>>(StringComparer.Ordinal);
        private readonly List<GameAction> _remapActions = new List<GameAction>();

        private ConfigEntry<string> _cfgCancelKey;
        private ConfigEntry<string> _cfgRepeatKey;
        private ConfigEntry<string> _cfgMenuKey;
        private ConfigEntry<float> _cfgCycleSeconds;
        private ConfigEntry<float> _cfgSnapDistance;
        private ConfigEntry<float> _cfgSnapPixels;

        // The snap distance as pushed into the hook: a screen-pixel radius
        // turned into world units from the camera's height and field of
        // view, so it holds its size on screen as you zoom; the world-unit
        // setting is its floor. Re-sent only when it moves by a twentieth.
        private float _snapPoll;
        private float _snapPushed = -1f;
        private ConfigEntry<bool> _cfgOverlay;
        private ConfigEntry<float> _cfgOverlaySeconds;
        private ConfigEntry<float> _cfgOverlayY;
        private ConfigEntry<float> _cfgOverlayIcon;
        private ConfigEntry<int> _cfgOverlayMax;
        private ConfigEntry<bool> _cfgOverlayNames;

        private bool _installed;
        private float _accum;
        private float _cyclePoll;
        private string _installedSignature;
        private int _builds;
        // Whether a live action table has been read for hotkeys the catalogue
        // lacks. Once a session is enough: the table is the game's own file.
        private bool _discovered;

        // The cycle the last press landed in, for the overlay.
        private int _cycleSeq = -1;
        private int _cycleIndex;
        private string[] _cycleNames;
        private uint[] _cycleIcons;
        private uint[] _cycleBgs;
        private int[] _cycleTiers;
        private float _cycleAt = -999f;

        /// Base key names the game's `Key` enum accepts (enums.lua). Modifier
        /// keys themselves are excluded — binding a build to bare Ctrl would
        /// fire on every modified keypress.
        private static readonly HashSet<string> ValidKeys = BuildValidKeys();

        private static HashSet<string> BuildValidKeys()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var c = 'A'; c <= 'Z'; c++) set.Add(c.ToString());
            for (var i = 0; i <= 9; i++) { set.Add("Digit" + i); set.Add("Numpad" + i); }
            for (var i = 1; i <= 24; i++) set.Add("F" + i);
            foreach (var k in new[]
            {
                "Space", "Enter", "Tab", "Backquote", "Quote", "Semicolon", "Comma", "Period",
                "Slash", "Backslash", "LeftBracket", "RightBracket", "Minus", "Equals",
                "ContextMenu", "Escape", "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
                "Backspace", "PageDown", "PageUp", "Home", "End", "Insert", "Delete",
                "CapsLock", "NumLock", "PrintScreen", "ScrollLock", "Pause",
                "NumpadEnter", "NumpadDivide", "NumpadMultiply", "NumpadPlus", "NumpadMinus",
                "NumpadPeriod", "NumpadEquals",
                "OEM1", "OEM2", "OEM3", "OEM4", "OEM5",
            }) set.Add(k);
            return set;
        }

        /// Mouse buttons the input system also indexes as keys. Only the
        /// game-hotkey remaps take them: the camera's drag is on the middle
        /// button, and moving it means naming one.
        private static readonly HashSet<string> MouseKeys =
            new HashSet<string>(StringComparer.Ordinal) { "LeftButton", "RightButton", "MiddleButton" };

        /// inputSystem.lua's modifierKeyCombos: what AnyModifier stands for.
        private static readonly string[] ModifierCombos =
            { "", "Ctrl-", "Shift-", "Alt-", "Ctrl-Alt-", "Ctrl-Shift-", "Shift-Alt-", "Ctrl-Shift-Alt-" };

        private void Awake()
        {
            _log ??= Logger;

            // The mod manager lists sections in the order they are first bound
            // and settings as bound, so this order is the page's: every key
            // first — the build roles, the other keys for builders, then the
            // game's own — and what tunes them after.
            foreach (var role in Roles.All)
            {
                var section = role.Mode == RoleMode.Structure ? "Structures" : "Units";
                _cfgKeys[role.Name] = Config.Bind(section, role.Name, role.DefaultKey,
                    role.Description + " Hotkey in the game's own format, e.g. G, Ctrl-G, Ctrl-Alt-G. " +
                    "Holding Shift queues five. Blank to unbind.");
            }

            // Sections and names from before 0.4.0 are carried over by Rebind,
            // so a key someone already moved stays moved.
            // Pause is the game's own key, in the slot the mod's own pause key
            // had until 0.4.0; the other three are the mod's.
            BindRemap(GameActions.All.First(a => a.Id == GameActions.PauseId));
            _cfgRepeatKey = Rebind("Toggles", "RepeatBuildKey", "BuilderKeys", "RepeatBuildKey", "Z",
                "Switches repeat build on the selected factories, and again off: the orders panel's Repeat toggle. " +
                "A build role on the same key fires first; the toggle only when that had nothing to build. " +
                "Hotkey in the game's own format; blank to unbind.");
            _cfgCancelKey = Rebind("Cancel", "ClearFactoryQueue", "BuilderKeys", "StopFactoriesKey", "Escape",
                "Stops every selected factory, as escape does in FAF: the same order as the Stop button, so its " +
                "build queue is cleared and an assist on another factory is dropped rather than left to refill it. " +
                "With no selected factory queued or assisting -- or the pause menu already open -- the key falls " +
                "through to whatever it normally does, so escape still opens the menu unless the pause menu key has " +
                "moved it. Hotkey in the game's own format; blank to unbind.");
            BindRemap(GameActions.All.First(a => a.Id == GameActions.UpgradeId));

            foreach (var action in GameActions.All)
            {
                // The pause menu's key goes with chat, the other keys for the
                // game's interface, ahead of them.
                if (_cfgMenuKey == null && action.Section == "GameInterface")
                    _cfgMenuKey = Rebind("Menu", "PauseMenuKey", "GameInterface", "PauseMenuKey", "Escape",
                        "Key that opens the pause menu, e.g. F11 or Ctrl-M (not F1, which opens the game's debug menu). " +
                        "Moving it off escape leaves escape to stopping factories; escape still closes the menu once " +
                        "it is open. Hotkey in the game's own format; escape or blank keeps the game's own binding.");
                BindRemap(action);
            }

            _cfgCycleSeconds = Rebind("Cycle", "Seconds", "Building", "CycleSeconds", 0f,
                "How long a key keeps cycling after a press, the way FAF hotbuild's cycle reset time does " +
                "(theirs is 1.1). Off by default: a structure already cycles for as long as its template is " +
                "on the cursor, and this only adds anything for factories, where it would turn a second press " +
                "into \"cycle\" instead of \"queue another\". Set 1.1 to match FAF.");
            _cfgSnapPixels = Rebind("Placement", "ExtractorSnapPixels", "Building", "ExtractorSnapPixels", 40f,
                new ConfigDescription("How close to a deposit, in screen pixels, the cursor has to be for an extractor being placed to snap onto it, " +
                    "whatever the zoom. 0 turns this off and leaves the world-unit distance below.",
                    new AcceptableValueRange<float>(0f, 200f)));
            _cfgSnapDistance = Rebind("Placement", "ExtractorSnapDistance", "Building", "ExtractorSnapDistance", 8f,
                new ConfigDescription("The least snap distance in world units, however far in you zoom. The game's own is 8.",
                    new AcceptableValueRange<float>(4f, 80f)));
            _cfgOverlay = Config.Bind("Overlay", "Show", true,
                "After a build hotkey, show what it picked and the rest of that key's cycle. A factory only " +
                "cycles with Cycle seconds set, so without it the overlay shows just the pick.");
            _cfgOverlaySeconds = Config.Bind("Overlay", "Seconds", 2.5f,
                "How long the overlay stays up after the last press.");
            _cfgOverlayIcon = Config.Bind("Overlay", "IconSize", 40f,
                "Size of each icon in the overlay, in 1080p-logical pixels.");
            _cfgOverlayMax = Config.Bind("Overlay", "MaxShown", 3,
                "Most icons to show at once. The overlay shows one tech tier of the cycle at a time — " +
                "the T3 options, then the T2 ones as you cycle past them — and this caps a single tier.");
            _cfgOverlayNames = Config.Bind("Overlay", "ShowNames", false,
                "Caption the overlay with the name of the entry you are on, e.g. \"Tier 1: Land Factory\". " +
                "Off by default — the icons carry it, and the name is only needed to tell two tiers apart.");
            _cfgOverlayY = Config.Bind("Overlay", "PosY", 860f,
                "Overlay's distance from the top of the screen, in 1080p-logical pixels, sitting just clear of " +
                "the build panel. It is always centred horizontally.");

            Logger.LogInfo($"Build Hotkeys loaded with {Roles.All.Count} roles (configure them from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            Remove();
            // A hot reload leaves the old assembly loaded; its strip and canvas
            // root go with it.
            _strip.Destroy();
            HudCanvas.Destroy();
        }

        /// One setting per game hotkey, defaulting to the keys the game ships
        /// it on, so the box shows what the key is today.
        private void BindRemap(GameAction action)
        {
            var id = action.Id;
            if (_cfgRemaps.ContainsKey(id)) return;
            var queued = action.Group == "Orders" && action.Defaults.Contains("Shift-")
                ? " Keep a Shift- form so the order can be queued." : "";
            var description = action.Description + " The game's own hotkey, in its own format, comma-separated, e.g. Q, Shift-Q; " +
                "AnyModifier-Q takes Q whatever Ctrl, Shift or Alt is held." + queued + " Blank to unbind.";
            _cfgRemaps[id] = action.OldSection != null
                ? Rebind(action.OldSection, action.OldKey, action.Section, action.Key, action.Defaults, description)
                : Config.Bind(action.Section, action.Key, action.Defaults, description);
            _remapActions.Add(action);
        }

        private ConfigEntry<T> Rebind<T>(string oldSection, string oldKey, string section, string key, T defaultValue, string description) =>
            Rebind(oldSection, oldKey, section, key, defaultValue, new ConfigDescription(description));

        /// Binds a setting that used to live under another section or name,
        /// carrying across a value saved there. BepInEx keeps a saved value
        /// that nothing binds as an orphan, which a Bind under the new name
        /// never looks at; taking it out of the orphans and saving drops the
        /// old line from the file, so this happens once.
        private ConfigEntry<T> Rebind<T>(string oldSection, string oldKey, string section, string key, T defaultValue, ConfigDescription description)
        {
            var entry = Config.Bind(section, key, defaultValue, description);
            try
            {
                var orphans = HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(Config, null)
                    as Dictionary<ConfigDefinition, string>;
                var old = new ConfigDefinition(oldSection, oldKey);
                if (orphans != null && orphans.TryGetValue(old, out var saved))
                {
                    entry.Value = (T)TomlTypeConverter.ConvertToValue(saved, typeof(T));
                    orphans.Remove(old);
                    Config.Save();
                    Logger.LogInfo($"Build hotkeys: moved setting {oldSection}.{oldKey} = '{saved}' to {section}.{key}.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Build hotkeys: could not carry {oldSection}.{oldKey} over to {section}.{key} ({e.Message}); it is back at its default.");
            }
            return entry;
        }

        /// The keys an action should move to, or false when it stays where the
        /// game put it: left at its default, or set to something that does not
        /// parse (which is warned about). An empty list means unbound.
        private bool TryRemap(GameAction action, bool quiet, out List<string> hotkeys)
        {
            var id = action.Id;
            var value = (_cfgRemaps[id].Value ?? "").Trim();
            hotkeys = null;
            if (value == action.Defaults) return false;
            if (!TryHotkeyList(value, id, quiet, out hotkeys)) return false;
            // A default that is written differently but means the same keys —
            // "Shift-A, A" — is still the default, and left to the game.
            return !(TryHotkeyList(action.Defaults, id, true, out var defaults) && Canon(hotkeys) == Canon(defaults));
        }

        /// A remap's value as the exact keys LoadedActionMap is indexed by:
        /// each comma-separated key canonicalised as a role key is, and
        /// AnyModifier- expanded into all eight combinations the way
        /// inputSystem.lua expands the defaults. Blank is a valid empty list.
        private bool TryHotkeyList(string raw, string name, bool quiet, out List<string> hotkeys)
        {
            hotkeys = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return true;
            const string any = "AnyModifier-";
            foreach (var item in raw.Split(','))
            {
                var key = item.Trim();
                if (key.Length == 0) continue;
                var anyModifier = key.StartsWith(any, StringComparison.OrdinalIgnoreCase);
                if (anyModifier) key = key.Substring(any.Length);
                if (!TryBindings(key, name, out var canonical, out _, mouse: true, quiet: quiet)) return false;
                // Base key names have no dash, so one means a modifier, which
                // AnyModifier already covers.
                if (anyModifier && canonical.Contains("-"))
                {
                    if (!quiet) Logger.LogWarning($"Build hotkeys: {name} = '{item.Trim()}' — AnyModifier takes a bare key. Ignored.");
                    return false;
                }
                foreach (var hk in anyModifier ? ModifierCombos.Select(m => m + canonical) : new[] { canonical })
                    if (!hotkeys.Contains(hk)) hotkeys.Add(hk);
            }
            return true;
        }

        private static string Canon(IEnumerable<string> hotkeys) =>
            string.Join(",", hotkeys.OrderBy(h => h, StringComparer.Ordinal).ToArray());

        /// The inverse of the AnyModifier expansion, for showing a list of
        /// keys the way inputActions.lua writes it.
        private static string Compress(IList<string> hotkeys)
        {
            var shown = new List<string>();
            foreach (var hk in hotkeys)
            {
                var b = hk.Substring(hk.LastIndexOf('-') + 1);
                var item = ModifierCombos.All(m => hotkeys.Contains(m + b)) ? "AnyModifier-" + b : hk;
                if (!shown.Contains(item)) shown.Add(item);
            }
            return string.Join(", ", shown.ToArray());
        }

        /// Binds a setting for every game hotkey the live table has and the
        /// catalogue lacks, so an action a game update adds is remappable
        /// from the next match on. Returns whether any of them already had a
        /// saved remap (BepInEx keeps a value for a key nobody has bound yet),
        /// which needs an install to take effect.
        private bool DiscoverActions()
        {
            var raw = GetLuaGlobal("__SdbBuildHotkeysActions");
            if (string.IsNullOrEmpty(raw)) return false;

            var added = new List<string>();
            var pending = false;
            foreach (var line in raw.Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 4) continue;
                var group = f[0];
                var name = f[1];
                if (_cfgRemaps.ContainsKey(group + "." + name)) continue;
                if (GameActions.SkippedGroups.Contains(group) || GameActions.SkippedActions.Contains(group + "." + name)) continue;

                var desc = f[2].Trim();
                var action = new GameAction(group, name,
                    Compress(f[3].Split(',').Where(k => k.Length > 0).ToList()),
                    desc.Length > 0 ? desc.TrimEnd('.') + "." : name + ".");
                BindRemap(action);
                added.Add(group + "." + name);
                if (TryRemap(action, true, out _)) pending = true;
            }
            if (added.Count > 0)
                Logger.LogInfo($"Build hotkeys: the game has {added.Count} hotkey(s) this version doesn't list, now in the settings too: " +
                               string.Join(", ", added.ToArray()));
            return pending;
        }

        /// What the last press picked and the rest of that key's cycle, on
        /// the HUD canvas (CycleStrip.cs).
        private readonly CycleStrip _strip = new CycleStrip();

        private void Update()
        {
            _strip.Sync(_cfgOverlay.Value, _cycleNames, _cycleIcons, _cycleBgs, _cycleTiers, _cycleIndex,
                Time.unscaledTime - _cycleAt, Mathf.Max(0.2f, _cfgOverlaySeconds.Value), _cfgOverlayIcon.Value,
                _cfgOverlayMax.Value, _cfgOverlayNames.Value, _cfgOverlayY.Value);
            PushSnap();
            PushMenuOpen();
            ReleaseStuckModifiers();

            // The overlay has to keep up with keypresses, so it polls far more
            // often than the once-a-second install upkeep below. Both are a
            // lua_getglobal, which is cheap; parsing only happens on a change.
            if (_installed && _cfgOverlay.Value)
            {
                _cyclePoll += Time.unscaledDeltaTime;
                if (_cyclePoll >= 0.05f)
                {
                    _cyclePoll = 0f;
                    PollCycle();
                }
            }

            _accum += Time.unscaledDeltaTime;
            if (_accum < 1f) return;
            _accum = 0f;

            // The client VM exists exactly while a match or replay is running,
            // so it is the whole gate: no VM means nothing to bind into, and a
            // new match builds a fresh one that needs reinstalling.
            //
            // Deliberately not InMatch — that rides on the economy Harmony
            // patch, and HudCore is compiled into each assembly, so its statics
            // are per-mod: InMatch would be permanently false here unless this
            // mod applied a patch it has no other reason to want.
            EnsureLuaBridge();
            if (!LuaReady)
            {
                _installed = false;
                _installedSignature = null;
                return;
            }

            var signature = Signature();
            if (_installed)
            {
                // Rebound from the mod manager mid-match: swap the layout over.
                if (signature != _installedSignature) Remove();
                else if (StillInstalled()) { LogUnstuck(); return; }
                else _installed = false;   // VM swapped under us; rebind below.
            }

            Install(signature);
        }

        private int _menuPushed = -1;
        private static readonly HarmonyLib.AccessTools.FieldRef<EM.UI.InterfaceManager, EM.UI.InterfaceManager.Window> CurrentWindow =
            HarmonyLib.AccessTools.FieldRefAccess<EM.UI.InterfaceManager, EM.UI.InterfaceManager.Window>("currentWindow");

        /// Mirrors "a menu screen is over the match" into BH.menuOpen, every
        /// frame but only running Lua on a change. A match runs under the
        /// None screen; the pause menu, and Settings opened from it, are any
        /// other — the same test the game's own escape toggle makes.
        private void PushMenuOpen()
        {
            if (!_installed) return;
            try
            {
                var im = EM.UI.InterfaceManager.Instance;
                var open = im != null && CurrentWindow(im) != EM.UI.InterfaceManager.Window.None ? 1 : 0;
                if (open == _menuPushed || !LuaReady) return;
                if (RunLua("if __SdbBuildHotkeys then __SdbBuildHotkeys.menuOpen = " + (open == 1 ? "true" : "false") + " end"))
                    _menuPushed = open;
            }
            catch (Exception e)
            {
                if (_menuPushed != -2) Logger.LogWarning($"Build hotkeys: can't read the pause menu ({e.Message}); escape keeps the game's binding.");
                _menuPushed = -2;
            }
        }

        private float _modPoll;
        private int _unstuck;
        private bool _unityDisagreed;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private static bool _noAsyncKeys;

        /// Whether Windows says a modifier is down right now (either side).
        /// This is the keyboard itself, not a record built from window
        /// messages, so it can't be stranded by Alt-Tab. Null where user32
        /// isn't there to ask.
        private static bool? OsKeyDown(int vk)
        {
            if (_noAsyncKeys) return null;
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch (Exception) { _noAsyncKeys = true; return null; }
        }

        // Back in focus: check straight away rather than on the next tick.
        private void OnApplicationFocus(bool focused)
        {
            if (focused) _modPoll = 1f;
        }

        /// Ten times a second while focused: which of Ctrl, Shift and Alt are
        /// physically up, into the hook's ReleaseMods, which clears any the
        /// input system still thinks are down (see the chunk for how Alt-Tab
        /// strands one). Safe against a key-down still waiting in the game's
        /// queue: the physical state is read after that event, so a key that
        /// reads up has its key-up queued behind it. Unfocused, the game's own
        /// focus reset owns the state, so this keeps out.
        ///
        /// "Physically" means Windows' GetAsyncKeyState. Until 0.4.1 this read
        /// Unity's Input.GetKey, which learns key state from the same window
        /// messages the game does: the Alt-up that Alt-Tab sends to the other
        /// window never reaches either, so Unity said Alt was still held too
        /// and nothing was let go.
        private void ReleaseStuckModifiers()
        {
            if (!_installed || !Application.isFocused) return;
            _modPoll += Time.unscaledDeltaTime;
            if (_modPoll < 0.1f) return;
            _modPoll = 0f;

            var unityCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var unityShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var unityAlt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
            var ctrl = OsKeyDown(VK_CONTROL) ?? unityCtrl;
            var shift = OsKeyDown(VK_SHIFT) ?? unityShift;
            var alt = OsKeyDown(VK_MENU) ?? unityAlt;

            // Once a match, say so if Unity thought a modifier was held that
            // the keyboard says isn't: that's the Alt-Tab case this is for.
            if (!_unityDisagreed && ((unityCtrl && !ctrl) || (unityShift && !shift) || (unityAlt && !alt)))
            {
                _unityDisagreed = true;
                Logger.LogInfo("Build hotkeys: Unity still had " +
                               (unityAlt && !alt ? "Alt" : unityCtrl && !ctrl ? "Ctrl" : "Shift") +
                               " held after it was let go (Alt-Tab?); going by the keyboard instead.");
            }
            if (ctrl && shift && alt) return;

            string Up(bool held) => held ? "false" : "true";
            try
            {
                if (LuaReady)
                    RunLua("if __SdbBuildHotkeys and __SdbBuildHotkeys.ReleaseMods then __SdbBuildHotkeys.ReleaseMods(" +
                           Up(ctrl) + "," + Up(shift) + "," + Up(alt) + ") end");
            }
            catch { /* next poll tries again */ }
        }

        /// Says so in the log when a stuck modifier had to be cleared, so a
        /// report of keys acting modified can be matched against it.
        private void LogUnstuck()
        {
            var raw = GetLuaGlobal("__SdbBuildHotkeysUnstuck");
            if (raw == null || !int.TryParse(raw, out var n) || n <= _unstuck) return;
            _unstuck = n;
            Logger.LogInfo($"Build hotkeys: released a modifier the game still had held after the key was let go ({n} this match).");
        }

        private string Signature() =>
            string.Join("|", Roles.All.Select(r => r.Name + "=" + _cfgKeys[r.Name].Value).ToArray()) + "|cycle=" + _cfgCycleSeconds.Value + "|cancel=" + _cfgCancelKey.Value + "|menu=" + _cfgMenuKey.Value + "|snap=" + _cfgSnapDistance.Value + "|repeat=" + _cfgRepeatKey.Value +
            "|" + string.Join("|", _cfgRemaps.Select(p => p.Key + "=" + p.Value.Value).ToArray());

        /// Reads the cycle the last press landed in: press counter, key, live
        /// index, then every option in order. The counter leads so two presses
        /// that resolve to the same entry still register as separate events —
        /// otherwise an identical string would look like nothing had happened
        /// and the overlay would not come back.
        private void PollCycle()
        {
            var raw = GetLuaGlobal("__SdbBuildHotkeysCycle");
            if (raw == null) return;

            var parts = raw.Split('|');
            if (parts.Length < 4) return;
            if (!int.TryParse(parts[0], out var seq) || seq == _cycleSeq) return;

            _cycleSeq = seq;
            int.TryParse(parts[2], out _cycleIndex);

            // Each entry is name~icon~tier~background. The three numbers are
            // taken from the right so a name containing a tilde cannot shift
            // them; whatever is left is the name.
            var n = parts.Length - 3;
            _cycleNames = new string[n];
            _cycleIcons = new uint[n];
            _cycleTiers = new int[n];
            _cycleBgs = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var fields = parts[i + 3].Split('~');
                _cycleNames[i] = parts[i + 3];
                if (fields.Length < 4) continue;
                var cut = fields.Length - 3;
                _cycleNames[i] = string.Join("~", fields, 0, cut);
                uint.TryParse(fields[cut], out _cycleIcons[i]);
                int.TryParse(fields[cut + 1], out _cycleTiers[i]);
                uint.TryParse(fields[cut + 2], out _cycleBgs[i]);
            }
            _cycleAt = Time.unscaledTime;
        }


        /// True while our binding is still live in the VM the game is running.
        /// Polling a global rather than trusting the flag means a match that
        /// starts and ends between two ticks — swapping the VM without
        /// LuaReady ever reading false — still gets rebound rather than
        /// silently leaving the hotkeys dead for the rest of the session.
        /// Doubles as the build counter.
        private bool StillInstalled()
        {
            // A flat global, not a field on the state table: the read-back
            // bridge is lua_getglobal, so it can only resolve a bare name.
            var raw = GetLuaGlobal("__SdbBuildHotkeysCount");
            if (raw == null) return false;
            if (int.TryParse(raw, out var count) && count > _builds)
            {
                _builds = count;
                Logger.LogInfo($"Build hotkeys: {count} build(s) issued this match.");
            }
            return true;
        }

        /// A configured key, split into the form the input system indexes by.
        private sealed class Binding
        {
            internal string Hotkey;   // what LoadedActionMap is keyed on
            internal string RoleKey;  // the canonical key roles are grouped under
            internal bool Shift;      // queue five, as the stock hotkeys do
            internal bool Reverse;    // walk the cycle backwards, as Alt does in FAF
        }

        /// Canonicalises a configured key into the exact strings the input
        /// system builds at press time. It assembles modifiers in the fixed
        /// order Ctrl, Shift, Alt (inputSystem.lua's modifierInputCodes), so
        /// "Shift-Ctrl-S" typed by a user has to become "Ctrl-Shift-S" or the
        /// lookup would never match.
        private bool TryBindings(string raw, string roleName, out string roleKey, out List<Binding> bindings,
            bool mouse = false, bool quiet = false)
        {
            roleKey = null;
            bindings = null;
            if (string.IsNullOrEmpty(raw)) return false;

            var parts = raw.Trim().Split('-').Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) return false;

            bool ctrl = false, shift = false, alt = false;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": ctrl = true; break;
                    case "shift": shift = true; break;
                    case "alt": alt = true; break;
                    default:
                        if (!quiet) Logger.LogWarning($"Build hotkeys: {roleName} = '{raw}' — '{parts[i]}' is not a modifier " +
                                          "(use Ctrl, Shift or Alt). Ignored.");
                        return false;
                }
            }

            // Case-correct the base key so 'ctrl-g' works as well as 'Ctrl-G'.
            var baseKey = parts[parts.Length - 1];
            var match = ValidKeys.Concat(mouse ? MouseKeys : Enumerable.Empty<string>())
                .FirstOrDefault(k => string.Equals(k, baseKey, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                if (!quiet) Logger.LogWarning($"Build hotkeys: {roleName} = '{raw}' — '{baseKey}' is not a key name. Ignored.");
                return false;
            }

            string Compose(bool withShift, bool withAlt) =>
                (ctrl ? "Ctrl-" : "") + (withShift ? "Shift-" : "") + (withAlt ? "Alt-" : "") + match;

            roleKey = Compose(shift, alt);
            bindings = new List<Binding>
            {
                new Binding { Hotkey = roleKey, RoleKey = roleKey, Shift = shift, Reverse = false },
            };

            // Shift means "queue five", as the stock hotkeys do; Alt walks the
            // cycle backwards, as it does in FAF hotbuild. Each is only added
            // when the configured key has not already claimed that modifier.
            if (!shift) bindings.Add(new Binding { Hotkey = Compose(true, alt), RoleKey = roleKey, Shift = true });
            if (!alt) bindings.Add(new Binding { Hotkey = Compose(shift, true), RoleKey = roleKey, Shift = shift, Reverse = true });
            if (!shift && !alt) bindings.Add(new Binding { Hotkey = Compose(true, true), RoleKey = roleKey, Shift = true, Reverse = true });
            return true;
        }

        private void Install(string signature)
        {
            var roleEntries = new List<string>();
            var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
            var layout = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var role in Roles.All)
            {
                if (!TryBindings(_cfgKeys[role.Name].Value, role.Name, out var roleKey, out var roleBindings)) continue;

                roleEntries.Add(
                    "{key=" + Quote(roleKey) +
                    ",mode=" + (role.Mode == RoleMode.Structure ? "'s'" : "'u'") +
                    ",name=" + Quote(role.Name) +
                    ",maxTier=" + role.MaxTier +
                    ",label=" + Quote(Label(roleKey)) +
                    ",expr=function() return " + role.Expression + " end}");

                foreach (var b in roleBindings)
                {
                    if (!bindings.ContainsKey(b.Hotkey)) bindings[b.Hotkey] = b;
                }

                if (!layout.TryGetValue(roleKey, out var names)) layout[roleKey] = names = new List<string>();
                names.Add(role.Name);
            }

            // Canonicalised the same way a role key is, but bound on its own:
            // no Shift or Alt variants, since it takes no count and has no
            // cycle to walk.
            var cancelKey = "";
            if (TryBindings(_cfgCancelKey.Value, "StopFactoriesKey", out var canonicalCancel, out _))
                cancelKey = canonicalCancel;
            var repeatKey = "";
            if (TryBindings(_cfgRepeatKey.Value, "RepeatBuildKey", out var canonicalRepeat, out _))
                repeatKey = canonicalRepeat;

            // Escape is where the game already binds the menu, so only another
            // key has anything to move.
            var menuKey = "";
            if (TryBindings(_cfgMenuKey.Value, "PauseMenuKey", out var canonicalMenu, out _) && canonicalMenu != "Escape")
                menuKey = canonicalMenu;

            // Only actions moved off their defaults go over; the rest are left
            // exactly as the game loaded them.
            var remapEntries = new List<string>();
            var remapLog = new List<string>();
            foreach (var action in _remapActions)
            {
                if (!TryRemap(action, false, out var hotkeys)) continue;
                remapEntries.Add("{g=" + Quote(action.Group) + ",a=" + Quote(action.Name) +
                                 ",hks={" + string.Join(",", hotkeys.Select(Quote).ToArray()) + "}}");
                remapLog.Add(action.Id + " -> " + (hotkeys.Count == 0 ? "unbound" : Compress(hotkeys)));
            }

            // Nothing of ours to bind still installs when it is the first
            // match of the session: the install is what lists the game's
            // hotkeys, so a remap of one this version doesn't know about
            // has to go through it to be found.
            if (roleEntries.Count == 0 && cancelKey.Length == 0 && menuKey.Length == 0 && repeatKey.Length == 0
                && remapEntries.Count == 0 && _discovered)
            {
                Logger.LogWarning("Build hotkeys: nothing bound — every role's key is blank or invalid.");
                _installed = true;
                _installedSignature = signature;
                return;
            }

            var bindingEntries = bindings.Values.Select(b =>
                "{hk=" + Quote(b.Hotkey) + ",key=" + Quote(b.RoleKey) + ",shift=" + (b.Shift ? "true" : "false") +
                ",rev=" + (b.Reverse ? "true" : "false") + "}");

            var chunk = InstallChunk
                .Replace("__ROLES__", string.Join(",", roleEntries.ToArray()))
                .Replace("__BINDINGS__", string.Join(",", bindingEntries.ToArray()))
                .Replace("__CYCLE__", Mathf.Max(0f, _cfgCycleSeconds.Value).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__SNAP__", Mathf.Clamp(_cfgSnapDistance.Value, 4f, 80f).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__CANCELKEY__", Quote(cancelKey))
                .Replace("__REPEATKEY__", Quote(repeatKey))
                .Replace("__MENUKEY__", Quote(menuKey))
                .Replace("__REMAPS__", string.Join(",", remapEntries.ToArray()));

            try
            {
                if (!RunLua(chunk)) return;
                _installed = true;
                _installedSignature = signature;
                _builds = 0;
                _unstuck = 0;
                _unityDisagreed = false;
                // The new install starts from the chunk's own snap and a fresh
                // press counter, so last install's readings no longer hold.
                _snapPushed = -1f;
                _cycleSeq = -1;
                _menuPushed = -1;
                // Each match reloads the sprites through Engine.LoadSprite, so
                // last match's AssetIDs are not safe to assume still valid.
                ClearSpriteCache();

                foreach (var pair in layout.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    Logger.LogInfo($"Build hotkeys: {pair.Key} -> {string.Join(", ", pair.Value.ToArray())}");
                }
                if (cancelKey.Length > 0)
                    Logger.LogInfo($"Build hotkeys: {cancelKey} -> stop selected factories");
                if (repeatKey.Length > 0)
                    Logger.LogInfo($"Build hotkeys: {repeatKey} -> repeat build on/off");
                if (menuKey.Length > 0)
                    Logger.LogInfo($"Build hotkeys: {menuKey} -> pause menu (escape only closes it)");
                foreach (var line in remapLog)
                    Logger.LogInfo($"Build hotkeys: {line}");
                // What the moves ran into: an action missing from this game
                // version, a key taken off another action, a key another group
                // also answers to.
                var report = GetLuaGlobal("__SdbBuildHotkeysRemapReport");
                if (!string.IsNullOrEmpty(report))
                    foreach (var line in report.Split('\n'))
                        Logger.LogWarning($"Build hotkeys: {line}");
                Logger.LogInfo($"Build hotkeys installed: {roleEntries.Count} roles on {layout.Count} keys, " +
                               $"{remapEntries.Count} game hotkey(s) moved.");

                if (!_discovered)
                {
                    _discovered = true;
                    // With nothing new already remapped, the new settings are
                    // at their defaults and the install stands; otherwise the
                    // changed signature reinstalls next tick to apply them.
                    if (!DiscoverActions()) _installedSignature = Signature();
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Build hotkeys could not be installed: {e.Message}");
            }
        }

        /// A few times a second while the hook is in: the snap distance for
        /// the current zoom, into the hook's BH.snap.
        private void PushSnap()
        {
            if (!_installed) return;
            _snapPoll += Time.unscaledDeltaTime;
            if (_snapPoll < 0.2f) return;
            _snapPoll = 0f;

            var floor = Mathf.Clamp(_cfgSnapDistance.Value, 4f, 80f);
            var snap = floor;
            var pixels = Mathf.Clamp(_cfgSnapPixels.Value, 0f, 200f);
            if (pixels > 0f)
            {
                var camera = Camera.main;
                if (camera == null)
                {
                    var all = Camera.allCameras;
                    camera = all != null && all.Length > 0 ? all[0] : null;
                }
                if (camera != null)
                {
                    // World units per screen pixel at the camera's height: the
                    // vertical field of view spans 2·h·tan(fov/2) of ground
                    // over the screen's height. Near enough for a tilted
                    // camera; the snap is a tolerance, not a measurement.
                    var height = Mathf.Abs(camera.transform.position.y);
                    var perPixel = 2f * height * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                    snap = Mathf.Max(floor, pixels * perPixel);
                }
            }

            if (_snapPushed >= 0f && Mathf.Abs(snap - _snapPushed) < Mathf.Max(0.5f, _snapPushed * 0.05f)) return;
            var chunk = "if __SdbBuildHotkeys then __SdbBuildHotkeys.snap = " +
                        snap.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " end";
            try
            {
                if (LuaReady && RunLua(chunk)) _snapPushed = snap;
            }
            catch { /* next poll tries again */ }
        }

        private void Remove()
        {
            if (!_installed) return;
            _installed = false;
            _snapPushed = -1f;
            _installedSignature = null;
            try
            {
                if (LuaReady) RunLua(RemoveChunk);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Build hotkeys could not be removed: {e.Message}");
            }
        }

        private static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        /// Compact form for the corner text on a construction button, which the
        /// stock panel fills with a single letter. Modifiers become
        /// one-character sigils so "Ctrl-S" still fits where "S" did.
        private static string Label(string roleKey)
        {
            var parts = roleKey.Split('-');
            var b = parts[parts.Length - 1];
            if (b.StartsWith("Digit")) b = b.Substring(5);
            else if (b.StartsWith("Numpad")) b = "N" + b.Substring(6);

            var prefix = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "Ctrl") prefix += "^";
                else if (parts[i] == "Shift") prefix += "+";
                else if (parts[i] == "Alt") prefix += "~";
            }
            return prefix + b;
        }

        // Installed once per match, guarded by a global inside the VM so a
        // retry is harmless. Every name it reaches for is a module-level global
        // of the file that owns it: Import hands back the file's environment
        // table (it discards the file's own `return`), so these are reachable
        // even where the file exports a narrower table — which is how
        // ConstructionClickFunction gets us to the file-local
        // ExecuteConstructionAction without reimplementing it.
        private const string InstallChunk = @"
if not __SdbBuildHotkeys then
  local BH = { saved = {}, state = {} }
  -- Lets the C# side see the hotkeys actually firing. Flat, because the
  -- read-back bridge is lua_getglobal and resolves bare names only.
  __SdbBuildHotkeysCount = 0
  local IS = Import('client/input/inputSystem.lua')
  local CP = Import('client/ui/constructionPanel.lua')
  local SS = Import('client/input/selectionSystem.lua')
  local BM = Import('client/input/buildmodeTemp.lua')
  local grp = IS.LoadedActionMap and IS.LoadedActionMap.Construction
  if not grp then error('BuildHotkeys: no Construction action group to bind into') end
  BH.grp = grp
  BH.NIL = {}
  BH.cycleSeconds = __CYCLE__
  BH.roles = { __ROLES__ }

  BH.byKey = {}
  for _, r in ipairs(BH.roles) do
    BH.byKey[r.key] = BH.byKey[r.key] or {}
    table.insert(BH.byKey[r.key], r)
  end

  -- The templates carry TECH1..TECH4 as tags; general.techNumber is derived
  -- from them but has no TECH5 branch, so read the tags directly.
  local function techOf(tpId)
    if Tags.TECH5[tpId] then return 5 end
    if Tags.TECH4[tpId] then return 4 end
    if Tags.TECH3[tpId] then return 3 end
    if Tags.TECH2[tpId] then return 2 end
    return 1
  end

  -- Relabel the construction buttons. Each one draws a hotkey in its corner,
  -- filled from constructionPanelHotkeys.GetHotkeyForTemplate; constructionPanel
  -- holds the module table rather than the function, and looks the field up per
  -- button, so replacing it here relabels every button with our own key the
  -- next time the panel is built. Templates no role claims keep the stock
  -- answer (usually '?').
  local CH = Import('client/input/constructionPanelHotkeys.lua')
  BH.CH = CH
  BH.origLabel = CH.GetHotkeyForTemplate
  local labelCache = {}
  CH.GetHotkeyForTemplate = function(tpId, isFactory)
    local want = isFactory and 'u' or 's'
    local ck = want .. tpId
    local hit = labelCache[ck]
    if hit == nil then
      hit = false
      for _, r in ipairs(BH.roles) do
        if r.mode == want and techOf(tpId) <= r.maxTier and r.expr()[tpId] then
          hit = r.label
          break
        end
      end
      labelCache[ck] = hit
    end
    if hit == false then return BH.origLabel(tpId, isFactory) end
    return hit
  end

  -- Every role on this key, in one cycle: ranked by tier first and by role
  -- order second. Where the roles cannot coexist (tank vs warship) that reads
  -- as 'first one that applies'; where they can (the three factories) it reads
  -- as a round-robin across them at the best tier, then again a tier down.
  local function candidates(roles, buildable, want)
    local out, ord = {}, {}
    for ri, role in ipairs(roles) do
      if role.mode == want then
        local expr = role.expr()
        for tpId in pairs(buildable) do
          -- DEMO_UI_ONLY templates are drawn as blank, unclickable buttons and
          -- left out of the panel's own hotkey list; they are not buildable.
          if expr[tpId] and ord[tpId] == nil and not Tags.DEMO_UI_ONLY[tpId]
             and techOf(tpId) <= role.maxTier then
            ord[tpId] = ri
            table.insert(out, tpId)
          end
        end
      end
    end
    table.sort(out, function(a, b)
      local ta, tb = techOf(a), techOf(b)
      if ta ~= tb then return ta > tb end
      if ord[a] ~= ord[b] then return ord[a] < ord[b] end
      return a < b
    end)
    return out
  end

  local function fire(key, shift, reverse)
    local units = SS.GetSelectedEntities()
    if not units then return false end

    -- Same split the construction panel makes: engineers place structures,
    -- everything else that can build queues units.
    local engineerTags = Tags.COMMAND + Tags.ENGINEER + Tags.ENGINEERING_STATION
    local isEngineer, isFactory, buildable = false, false, {}
    -- Count and lowest id identify the selection without depending on pairs
    -- order, which is not stable across the fresh table each call returns.
    local selCount, selMin = 0, 0
    for _, u in pairs(units) do
      selCount = selCount + 1
      local uid = u.id and u.id.index
      if uid and (selMin == 0 or uid < selMin) then selMin = uid end
      local t = buildQueueUtils.GetBuildableTags(u)
      if next(t) then
        if engineerTags[u.tp.general.tpId] then isEngineer = true else isFactory = true end
        for tpId in pairs(t) do buildable[tpId] = true end
      end
    end
    -- Nothing buildable, or a mixed selection: the game blanks the panel in
    -- both cases, so leave the key to whoever else wants it.
    if isEngineer == isFactory then return false end

    local list = BH.byKey[key]
    if not list then return false end
    local want = isFactory and 'u' or 's'

    local cands = candidates(list, buildable, want)
    if #cands == 0 then return false end

    -- Two ways a press continues the previous one rather than restarting it.
    -- A structure is still uncommitted while its template sits on the cursor,
    -- which has no time limit — you may be lining up a placement. A factory
    -- never enters build mode, so it gets FAF hotbuild's rule instead: repeat
    -- presses cycle while they keep coming, and once the window lapses the key
    -- queues another of whatever it last chose. Setting the window to 0 drops
    -- back to queue-on-repeat only.
    local now = (os and os.clock) and os.clock() or nil
    local sig = selCount .. ':' .. selMin
    local st = BH.state
    local sameTarget = st.key == key and st.mode == want and st.sig == sig
    local continuing = sameTarget and (
      (BM.GetBuildMode() and BM.GetBuildTpId() == st.tpId)
      or (now and st.t and BH.cycleSeconds > 0 and (now - st.t) < BH.cycleSeconds))

    local idx
    if continuing then
      if reverse then idx = ((st.index - 2) % #cands) + 1
      else idx = (st.index % #cands) + 1 end
    else
      -- A fresh reverse press opens at the far end, which makes Alt the direct
      -- way to the cheapest option — the T1 factory you mean to upgrade later,
      -- rather than the T3 one the forward cycle opens on.
      idx = reverse and #cands or 1
    end
    local tpId = cands[idx]
    BH.state = { key = key, mode = want, index = idx, tpId = tpId, sig = sig, t = now }

    -- Publish the whole cycle for the overlay: which key, which entry is live,
    -- and every option in order. Led by the press counter so two presses that
    -- land on the same entry still read as two distinct events.
    __SdbBuildHotkeysCount = __SdbBuildHotkeysCount + 1
    -- A factory with no cycle window never gets past its first pick — a repeat
    -- press queues another of the same — so the rest of the list would only
    -- show options that pressing again cannot reach. Show just the pick.
    local shown, shownIdx = cands, idx
    if want == 'u' and not (now and BH.cycleSeconds > 0) then
      shown, shownIdx = { tpId }, 1
    end
    local entries = {}
    for i = 1, #shown do
      local g = __Templates.Units[shown[i]]
      g = g and g.general
      -- foregroundIconID is the build-menu button art (UnitTemplateIDToIconID,
      -- one sprite per template); strategicIconID is the map symbol, which is
      -- shared across tiers and so cannot tell a T1 factory from a T3 one.
      -- tonumber because the FFI hands back a uint32 cdata, which would
      -- otherwise concatenate as '1234ULL'.
      local icon = 0
      if g and g.foregroundIconID and g.foregroundIconID.index then
        icon = tonumber(g.foregroundIconID.index) or 0
      end
      -- backgroundIconID is the domain plate the build buttons sit on, picked
      -- by iconUIType (land / air / water / amphibious). Same art, so the
      -- overlay reads like the panel rather than like floating cut-outs.
      local bg = 0
      if g and g.backgroundIconID and g.backgroundIconID.index then
        bg = tonumber(g.backgroundIconID.index) or 0
      end
      entries[i] = ((g and g.displayName) or shown[i]) .. '~' .. string.format('%d', icon)
        .. '~' .. string.format('%d', techOf(shown[i])) .. '~' .. string.format('%d', bg)
    end
    __SdbBuildHotkeysCycle = __SdbBuildHotkeysCount .. '|' .. key .. '|' .. shownIdx
      .. '|' .. table.concat(entries, '|')

    -- The panel's own click handler: it wraps the file-local
    -- ExecuteConstructionAction, so this is exactly a button click.
    CP.ConstructionClickFunction(
      { mouseClickType = UIMouseClickType.Left, isShiftHeld = shift },
      { tpId = tpId, isFactory = isFactory, selectedUnits = units })
    return true
  end

  -- The input system builds a key's modifier prefix from its own record of
  -- which keys are down, and only an Alt-up clears Alt from it. Alt-Tab out
  -- during a loading screen can lose that key-up — the focus-change reset
  -- lands ahead of the Alt-down it was meant to undo — so Alt stays 'held'
  -- and every W arrives as Alt-W: the reverse cycle, which opens on the air
  -- factory. Every other hotkey breaks the same way. The plugin reads the
  -- physical keys and passes in which modifiers are up; any the record still
  -- holds down are cleared. The record is fetched per call because
  -- ReleaseAllKeys swaps the table out.
  __SdbBuildHotkeysUnstuck = 0
  local modKeys = {
    Ctrl = { 'Ctrl', 'LeftCtrl', 'RightCtrl' },
    Shift = { 'Shift', 'LeftShift', 'RightShift' },
    Alt = { 'Alt', 'LeftAlt', 'RightAlt' },
  }
  BH.ReleaseMods = function(ctrlUp, shiftUp, altUp)
    local raw = IS.InputStatesRawKeys
    if not raw then return end
    for name, up in pairs({ Ctrl = ctrlUp, Shift = shiftUp, Alt = altUp }) do
      if up and raw[name] then
        for _, k in ipairs(modKeys[name]) do raw[k] = false end
        __SdbBuildHotkeysUnstuck = __SdbBuildHotkeysUnstuck + 1
      end
    end
  end

  -- Escape has to keep closing the pause menu, and Lua has no getter for
  -- it. Since 0.0.1.20 the menu is an InterfaceManager screen that Lua
  -- opens but the C# Resume button closes, so the plugin mirrors it here
  -- from the screen it reads (see PushMenuOpen).
  BH.menuOpen = false

  -- Placing an extractor snaps it onto a deposit within a few world units
  -- of the cursor. The game's FindClosestResourceSpot fixes that at 8, which
  -- zoomed out is a couple of pixels; this is the same search with the
  -- distance from the setting. The module's own callers reach the function
  -- through the module table, so replacing the field there catches them.
  BH.snap = __SNAP__
  local CPS = Import('client/input/constructionPreviewSystem.lua')
  local PU = Import('common/systems/placementUtils.lua')
  local RS = Import('common/resourceSpot.lua')
  if CPS and CPS.FindClosestResourceSpot and PU and RS then
    BH.CPS = CPS
    BH.origFindSpot = CPS.FindClosestResourceSpot
    CPS.FindClosestResourceSpot = function(position)
      local tp = __Templates.Units[BM.GetBuildTpId()]
      if not tp then return BH.origFindSpot(position) end
      local closest, best = nil, BH.snap
      local domain = PU.GetPlacementDomain(tp)
      for _, spot in pairs(RS.resourceSpots) do
        local p = spot:GetPosition()
        p.x = math.truncateToInt(p.x)
        p.z = math.truncateToInt(p.z)
        local d = math.sqrt((position.x - p.x) ^ 2 + (position.z - p.z) ^ 2)
        if d < best then
          local minX, minY, maxX, maxY = PU.GetPlacementBounds(p, tp.skirtSize)
          if CPS.CanPlaceAtArea(minX, minY, maxX, maxY, domain, true) then
            best = d
            closest = p
          end
        end
      end
      return closest
    end
  end

  -- Stop every selected factory, the way FAF's escape does. This is the Stop
  -- button's own order, not a queue edit: emptying the queue alone leaves a
  -- factory that assists another one still slaved to it, and it pulls the next
  -- item straight off that factory's queue. The host's ClearOrder drops the
  -- assist along with the queue and the item in hand.
  local function stopFactories()
    if BH.menuOpen or IsObserver() then return false end
    local units = SS.GetSelectedUnits()
    if not units then return false end

    -- Factories only, and only our own: a tank sharing the selection keeps its
    -- orders, where the Stop button would halt it too.
    local army = GetFocusArmy()
    local factories, busy = {}, false
    for _, u in pairs(units) do
      local g = u.tp and u.tp.general
      if u.id and g and Tags.FACTORY[g.tpId] and u.armyId == army then
        table.insert(factories, u)
        -- An assist on another factory is an order that stays active for as
        -- long as the factory is slaved, so it counts even with an empty queue.
        local q, ord = u.predictedBuildQueue, u.orderState
        if (q and next(q)) or (ord and (ord.activeOrder or next(ord.queuedOrdersArray or {}))) then
          busy = true
        end
      end
    end
    if not busy then return false end

    Import('client/managers/orders/clientOrderManager.lua').ClientClearOrder(factories)
    return true
  end

  BH.Cancel = function()
    local ok, res = pcall(stopFactories)
    if not ok then Warn('BuildHotkeys stop: ' .. tostring(res)) return false end
    return res
  end

  BH.Fire = function(key, shift, reverse)
    local ok, res = pcall(fire, key, shift, reverse)
    if not ok then Warn('BuildHotkeys: ' .. tostring(res)) return false end
    return res
  end

  -- Repeat build on the selection, as the orders panel's toggle does
  -- it: on if any selected unit has it off, else off. SetToggle sends
  -- the game's own command, and errors with nothing valid selected, hence
  -- the pcall. False with no unit that has the toggle, so the key falls
  -- through to whatever else it does.
  local function flipToggle(name)
    if BH.menuOpen or IsObserver() then return false end
    local units = SS.GetSelectedUnits()
    if not units then return false end
    local army = GetFocusArmy()
    local any, allOn = false, true
    for _, u in pairs(units) do
      if u.armyId == army and u.toggles and u.toggles[name] ~= nil then
        any = true
        if not u.toggles[name] then allOn = false end
      end
    end
    if not any then return false end
    Import('client/inputEventsFunctions.lua').SetToggle(name, not allOn)
    return true
  end
  BH.Toggle = function(name)
    local ok, res = pcall(flipToggle, name)
    if not ok then Warn('BuildHotkeys toggle: ' .. tostring(res)) return false end
    return res
  end

  -- The game's own hotkeys, moved. LoadedActionMap holds a shallow copy of
  -- an action's defaultActions under each of its keys, so every copy shares
  -- the action's functions: that is how its current keys are found and how
  -- a key's owner is named. Import is cached, so this is the very table
  -- inputSystem.lua loaded from. Every moved action comes off its keys
  -- before any goes on, so two can swap; and all of it happens before the
  -- build roles bind below, which then sit on top as they would anyway.
  local IA = Import('client/input/inputActions.lua').InputActions
  local LAM = IS.LoadedActionMap
  BH.LAM = LAM
  BH.remapSaved = {}
  local events = { 'press', 'release', 'doublePress', 'valueChange' }
  local function isAction(entry, da)
    if type(entry) ~= 'table' or type(da) ~= 'table' then return false end
    for _, ev in ipairs(events) do
      if da[ev] and entry[ev] == da[ev] then return true end
    end
    return false
  end
  local function ownerOf(g, entry)
    for name, data in pairs(IA[g] or {}) do
      if type(data) == 'table' and isAction(entry, data.defaultActions) then return g .. '.' .. name end
    end
    if g == 'Construction' then return 'a BuildHotkeys key' end
    return g
  end
  local function touch(g, hk)
    local id = g .. '|' .. hk
    if BH.remapSaved[id] == nil then BH.remapSaved[id] = { g = g, hk = hk, v = LAM[g][hk] or BH.NIL } end
  end
  local report = {}
  BH.remaps = { __REMAPS__ }
  for _, r in ipairs(BH.remaps) do
    local data = IA[r.g] and IA[r.g][r.a]
    if type(data) == 'table' and type(data.defaultActions) == 'table' and LAM[r.g] then
      r.da = data.defaultActions
      -- Clearing a field mid-traversal is allowed; adding one is not.
      for hk, entry in pairs(LAM[r.g]) do
        if isAction(entry, r.da) then touch(r.g, hk); LAM[r.g][hk] = nil end
      end
    else
      table.insert(report, r.g .. '.' .. r.a .. ' is not in this version of the game; nothing moved')
    end
  end
  for _, r in ipairs(BH.remaps) do
    if r.da then
      local map = LAM[r.g]
      for _, hk in ipairs(r.hks) do
        if map[hk] then table.insert(report, r.g .. '.' .. r.a .. ' takes ' .. hk .. ' from ' .. ownerOf(r.g, map[hk])) end
        touch(r.g, hk)
        -- A copy, as LoadInputActions makes one per key.
        local copy = {}
        for ev, fn in pairs(r.da) do copy[ev] = fn end
        map[hk] = copy
      end
    end
  end

  -- Construction has the highest group priority, so these run before the
  -- Orders group; returning false when nothing matched lets the event fall
  -- through to whatever the key normally does.
  for _, b in ipairs({ __BINDINGS__ }) do
    if BH.saved[b.hk] == nil then BH.saved[b.hk] = grp[b.hk] or BH.NIL end
    grp[b.hk] = { press = function() return BH.Fire(b.key, b.shift, b.rev) end }
  end

  -- Returning false when there was nothing to stop lets the press carry on
  -- to the GameMenu group, which has no priority set and so sits below this
  -- one — so with the menu left on escape, escape still opens it.
  BH.cancelKey = __CANCELKEY__
  if BH.cancelKey ~= '' then
    if BH.saved[BH.cancelKey] == nil then BH.saved[BH.cancelKey] = grp[BH.cancelKey] or BH.NIL end
    grp[BH.cancelKey] = { press = function() return BH.Cancel() end }
  end

  -- The repeat key sits behind whatever the key already does here: a build
  -- role on the same key fires first, and the toggle only when that had
  -- nothing to build for the selection. (Pause is the game's own key since
  -- 0.4.0, moved like any other; its Orders group is below this one anyway.)
  BH.repeatKey = __REPEATKEY__
  for name, hk in pairs({ RepeatBuild = BH.repeatKey }) do
    if hk ~= '' then
      if BH.saved[hk] == nil then BH.saved[hk] = grp[hk] or BH.NIL end
      local before = grp[hk]
      grp[hk] = { press = function()
        if before and before.press and before.press() then return true end
        return BH.Toggle(name)
      end }
    end
  end

  -- Moving the pause menu off escape. The game's own toggle goes to the new
  -- key as it is, and escape keeps only its closing half: an open menu still
  -- shuts on escape, but a closed one no longer opens behind a stop that had
  -- nothing to do. The toggle only flips a file-local flag, and it is the
  -- sole way the menu opens, so that flag is always set while the menu is up.
  BH.menuKey = __MENUKEY__
  local gm = IS.LoadedActionMap.GameMenu
  if BH.menuKey ~= '' and gm and gm.Escape and gm.Escape.press then
    local toggle = gm.Escape
    BH.gm = gm
    BH.gmSaved = { Escape = toggle, [BH.menuKey] = gm[BH.menuKey] or BH.NIL }
    gm[BH.menuKey] = toggle
    gm.Escape = { press = function()
      if BH.menuOpen then return toggle.press() end
      return false
    end }
  end

  -- A moved key that another group also binds: the input system runs every
  -- group in priority order until one says it consumed the press, which
  -- most actions never do, so both usually fire. Construction is the
  -- exception, and left out: its keys consume a press only when they act
  -- on it, so a key shared with a build key goes to it only when the
  -- selection can build and falls through otherwise, which is the point.
  for _, r in ipairs(BH.remaps) do
    if r.da then
      local shared = {}
      for _, hk in ipairs(r.hks) do
        for g2, map2 in pairs(LAM) do
          if g2 ~= r.g and g2 ~= 'Construction' and map2[hk] then
            local o = ownerOf(g2, map2[hk])
            if not shared[o] then
              shared[o] = true
              table.insert(report, r.g .. '.' .. r.a .. ' shares ' .. hk .. ' with ' .. o)
            end
          end
        end
      end
    end
  end
  __SdbBuildHotkeysRemapReport = table.concat(report, '\n')

  -- Every action in the game's table, for the plugin to find any its own
  -- list lacks: group, name, description, keys (already AnyModifier-expanded).
  local catalogue = {}
  for g, acts in pairs(IA) do
    if type(acts) == 'table' then
      for name, data in pairs(acts) do
        if type(data) == 'table' and type(data.defaultActions) == 'table' and type(data.defaultHotkeys) == 'table' then
          local da = data.defaultActions
          if da.press or da.release or da.doublePress then
            local desc = (string.gsub(tostring(data.uiDescription or ''), '%s', ' '))
            table.insert(catalogue, g .. '\t' .. name .. '\t' .. desc .. '\t' .. table.concat(data.defaultHotkeys, ','))
          end
        end
      end
    end
  end
  table.sort(catalogue)
  __SdbBuildHotkeysActions = table.concat(catalogue, '\n')

  __SdbBuildHotkeys = BH
end";

        // Puts the stock construction hotkeys back. Without this, unloading the
        // mod or switching it off would leave the bindings live in the VM with
        // nothing running to explain them.
        private const string RemoveChunk = @"
if __SdbBuildHotkeys then
  local BH = __SdbBuildHotkeys
  if BH.grp then
    for hk, saved in pairs(BH.saved) do
      if saved == BH.NIL then BH.grp[hk] = nil else BH.grp[hk] = saved end
    end
  end
  if BH.CH and BH.origLabel then BH.CH.GetHotkeyForTemplate = BH.origLabel end
  if BH.CPS and BH.origFindSpot then BH.CPS.FindClosestResourceSpot = BH.origFindSpot end
  if BH.gm and BH.gmSaved then
    for hk, saved in pairs(BH.gmSaved) do
      if saved == BH.NIL then BH.gm[hk] = nil else BH.gm[hk] = saved end
    end
  end
  -- Last: the build keys above saved what the moves had left, so undoing
  -- them first and the moves after lands back on the game's own map.
  if BH.LAM and BH.remapSaved then
    for _, s in pairs(BH.remapSaved) do
      if s.v == BH.NIL then BH.LAM[s.g][s.hk] = nil else BH.LAM[s.g][s.hk] = s.v end
    end
  end
  __SdbBuildHotkeys = nil
  __SdbBuildHotkeysRemapReport = nil
  __SdbBuildHotkeysActions = nil
  __SdbBuildHotkeysCount = nil
  __SdbBuildHotkeysCycle = nil
  __SdbBuildHotkeysUnstuck = nil
end";
    }
}
