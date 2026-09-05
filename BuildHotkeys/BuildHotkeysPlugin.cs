using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    [BepInPlugin("com.sanctuarydb.buildhotkeys", "Build Hotkeys", "0.1.0")]
    public class BuildHotkeysPlugin : BaseUnityPlugin
    {
        private readonly Dictionary<string, ConfigEntry<string>> _cfgKeys =
            new Dictionary<string, ConfigEntry<string>>();
        private ConfigEntry<bool> _cfgEnabled;

        private ConfigEntry<float> _cfgCycleSeconds;
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

        // The cycle the last press landed in, for the overlay.
        private int _cycleSeq = -1;
        private string _cycleKey;
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

        private void Awake()
        {
            _log ??= Logger;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch. Off restores the game's own construction hotkeys.");
            _cfgCycleSeconds = Config.Bind("Cycle", "Seconds", 1.1f,
                "How long a key keeps cycling after a press, matching FAF hotbuild's cycle reset time. " +
                "A structure ignores this while its template is still on the cursor. A factory has no such " +
                "state, so this is what lets repeat presses there walk the cycle; once the window lapses the " +
                "key queues another of whatever it last chose. Set 0 to only ever queue on repeat.");
            _cfgOverlay = Config.Bind("Overlay", "Show", true,
                "After a build hotkey, show what it picked and the rest of that key's cycle.");
            _cfgOverlaySeconds = Config.Bind("Overlay", "Seconds", 2.5f,
                "How long the overlay stays up after the last press.");
            _cfgOverlayIcon = Config.Bind("Overlay", "IconSize", 48f,
                "Size of each icon in the overlay, in 1080p-logical pixels.");
            _cfgOverlayMax = Config.Bind("Overlay", "MaxShown", 3,
                "Most icons to show at once. The overlay shows one tech tier of the cycle at a time — " +
                "the T3 options, then the T2 ones as you cycle past them — and this caps a single tier.");
            _cfgOverlayNames = Config.Bind("Overlay", "ShowNames", false,
                "Caption the overlay with the name of the entry you are on, e.g. \"Tier 1: Land Factory\". " +
                "Off by default — the icons carry it, and the name is only needed to tell two tiers apart.");
            _cfgOverlayY = Config.Bind("Overlay", "PosY", 700f,
                "Overlay's distance from the top of the screen, in 1080p-logical pixels. " +
                "It is always centred horizontally.");

            foreach (var role in Roles.All)
            {
                var section = role.Mode == RoleMode.Structure ? "Structures" : "Units";
                _cfgKeys[role.Name] = Config.Bind(section, role.Name, role.DefaultKey,
                    role.Description + " Hotkey in the game's own format, e.g. G, Ctrl-G, Ctrl-Alt-G. " +
                    "Holding Shift queues five. Blank to unbind.");
            }

            Logger.LogInfo($"Build Hotkeys loaded with {Roles.All.Count} roles (configure them from the F8 mod manager).");
        }

        private void OnDestroy() => Remove();

        /// How much of the next entry leans into view past the band edge.
        private const float PeekFraction = 0.45f;

        private GUIStyle _stCycleKey, _stCycleTier, _stCycleCaption;

        /// Shows what the last press actually picked and the rest of that key's
        /// cycle, as a strip of the same art the build menu uses: the live one
        /// lit, the others faded, left to right in the order further presses
        /// reach them. Drawn, never interactive — no GUI.Window or Button, so
        /// it cannot swallow a click meant for the battlefield underneath.
        private void OnGUI()
        {
            if (!_cfgOverlay.Value || _cycleNames == null || _cycleNames.Length == 0) return;
            if (Time.unscaledTime - _cycleAt > Mathf.Max(0.2f, _cfgOverlaySeconds.Value)) return;

            EnsureStyles();
            if (_stCycleKey == null)
            {
                _stCycleKey = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.68f, 0.25f) } };
                _stCycleTier = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.45f) } };
                _stCycleCaption = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            }

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            var icon = Mathf.Clamp(_cfgOverlayIcon.Value, 16f, 256f);
            var captioned = _cfgOverlayNames.Value;
            const float cellPad = 6f, padX = 8f, padY = 8f, chipW = 40f, captionH = 22f;

            var total = _cycleNames.Length;
            var liveIndex = Mathf.Clamp(_cycleIndex - 1, 0, total - 1);

            // A short cycle is shown whole — point defence is one entry per
            // tier, and banding that would leave a single icon on screen.
            // Only once the cycle outgrows the cap does it show a tier at a
            // time: a T3 engineer's factory key is nine entries with naval in,
            // which at this icon size would span the screen. The candidate list
            // is sorted by tier, so a band is the contiguous run around the
            // live entry sharing its tier — and that, rather than a fixed block
            // of N, is the grouping that reads right, since a block would
            // straddle two tiers whenever a faction lacks a domain at one.
            var cap = Mathf.Max(1, _cfgOverlayMax.Value);
            int first = 0, last = total - 1;
            if (total > cap)
            {
                first = last = liveIndex;
                if (_cycleTiers != null && _cycleTiers.Length == total)
                {
                    var tier = _cycleTiers[liveIndex];
                    while (first > 0 && _cycleTiers[first - 1] == tier) first--;
                    while (last < total - 1 && _cycleTiers[last + 1] == tier) last++;
                }
                if (last - first + 1 > cap)
                {
                    first = Mathf.Clamp(liveIndex - cap / 2, first, last - cap + 1);
                    last = first + cap - 1;
                }
            }
            var shown = last - first + 1;
            var hidden = total - shown;

            // Rather than count what is left, let the next entry run off the
            // edge: half an icon says "there is more" without asking anyone to
            // read a number. Only forwards, and only when there really is a
            // next one — no peek on the final band is itself the signal that
            // the cycle ends there.
            var peek = last < total - 1;

            var cellW = icon + cellPad * 2f;
            var cellH = icon + cellPad * 2f;
            var width = padX * 2f + chipW + cellW * shown + (peek ? cellW * PeekFraction : 0f);
            var height = padY * 2f + cellH + (captioned ? captionH : 0f);

            var x = Mathf.Round((Screen.width / scale - width) * 0.5f);
            var y = _cfgOverlayY.Value;

            GUI.DrawTexture(new Rect(x, y, width, height), _texPanel);
            var previousColour = GUI.color;

            // The whole strip belongs to one key, so it is named once, off to
            // the left where it can never sit on top of the art. The tier under
            // it says which band you are looking at.
            var banded = hidden > 0 && _cycleTiers != null && _cycleTiers.Length == total;
            if (banded)
            {
                GUI.Label(new Rect(x + padX, y + padY + 2f, chipW, cellH * 0.55f), _cycleKey, _stCycleKey);
                GUI.Label(new Rect(x + padX, y + padY + cellH * 0.5f, chipW, cellH * 0.45f),
                    "T" + _cycleTiers[liveIndex], _stCycleTier);
            }
            else
            {
                // Whole cycle on screen, so there is no band to name — the tier
                // label would just look like it applied to the strip.
                GUI.Label(new Rect(x + padX, y + padY, chipW, cellH), _cycleKey, _stCycleKey);
            }

            for (var i = first; i <= last; i++)
            {
                var cellX = x + padX + chipW + (i - first) * cellW;
                var cellY = y + padY;
                var live = i == liveIndex;

                if (live)
                {
                    GUI.DrawTexture(new Rect(cellX, cellY, cellW, cellH), _texRowHover);
                    GUI.DrawTexture(new Rect(cellX, cellY + cellH - 3f, cellW, 3f), _texWhite);
                }

                // Options not landed on are faded, so the live one reads at a
                // glance without having to hunt for the highlight.
                GUI.color = live ? Color.white : new Color(1f, 1f, 1f, 0.3f);
                var art = new Rect(cellX + cellPad, cellY + cellPad, icon, icon);
                if (_cycleBgs != null && i < _cycleBgs.Length) DrawSprite(art, _cycleBgs[i]);
                if (_cycleIcons != null && i < _cycleIcons.Length) DrawSprite(art, _cycleIcons[i]);
                GUI.color = previousColour;
            }

            // The next entry, cut off mid-icon: the cycle carries on past this
            // band, so a key that looks like it has three options does not read
            // as the whole story.
            if (peek && _cycleIcons != null && last + 1 < _cycleIcons.Length)
            {
                var peekX = x + padX + chipW + shown * cellW;
                GUI.color = new Color(1f, 1f, 1f, 0.18f);
                var peekArt = new Rect(peekX + cellPad, y + padY + cellPad, icon * PeekFraction, icon);
                if (_cycleBgs != null && last + 1 < _cycleBgs.Length)
                    DrawSprite(peekArt, _cycleBgs[last + 1], PeekFraction);
                DrawSprite(peekArt, _cycleIcons[last + 1], PeekFraction);
                GUI.color = previousColour;
            }

            if (captioned)
                GUI.Label(new Rect(x, y + height - captionH - 3f, width, captionH),
                    _cycleNames[liveIndex], _stCycleCaption);

            GUI.matrix = previousMatrix;
        }

        private void Update()
        {
            if (!_cfgEnabled.Value)
            {
                if (_installed) Remove();
                return;
            }

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
                else if (StillInstalled()) return;
                else _installed = false;   // VM swapped under us; rebind below.
            }

            Install(signature);
        }

        private string Signature() =>
            string.Join("|", Roles.All.Select(r => r.Name + "=" + _cfgKeys[r.Name].Value).ToArray()) + "|cycle=" + _cfgCycleSeconds.Value;

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
            _cycleKey = parts[1];
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

        // ---- build-menu icons ---------------------------------------------

        private static MethodInfo _tryGetSprite;
        private static Type _assetIdType;
        private static bool _spriteBridgeTried;
        private readonly Dictionary<uint, Sprite> _spriteCache = new Dictionary<uint, Sprite>();

        /// The build buttons' art is not the strategic icon atlas HudCore
        /// samples for the commander widget — those are `.sansprite` assets
        /// loaded through the game's own pipeline into a registry keyed by
        /// AssetID. SanctuaryUI.Utils.TryGetLoadedSprite is the public way in,
        /// and EM.Core.AssetID is a struct wrapping the very uint the Lua side
        /// reports as `foregroundIconID.index`.
        private static void ResolveSpriteBridge()
        {
            if (_spriteBridgeTried) return;
            _spriteBridgeTried = true;
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic).SelectMany(GetTypesSafe).ToList();
                _assetIdType = types.FirstOrDefault(t => t.FullName == "EM.Core.AssetID");
                _tryGetSprite = types.FirstOrDefault(t => t.FullName == "SanctuaryUI.Utils")
                    ?.GetMethod("TryGetLoadedSprite", BindingFlags.Public | BindingFlags.Static);
                if (_tryGetSprite == null || _assetIdType == null)
                    _log?.LogWarning("Build hotkeys: sprite lookup unavailable; the overlay will list names only.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Build hotkeys: sprite lookup failed to resolve ({e.Message}); names only.");
            }
        }

        private Sprite ResolveSprite(uint index)
        {
            if (index == 0) return null;
            if (_spriteCache.TryGetValue(index, out var cached)) return cached;

            ResolveSpriteBridge();
            Sprite sprite = null;
            if (_tryGetSprite != null && _assetIdType != null)
            {
                try
                {
                    var args = new[] { Activator.CreateInstance(_assetIdType, index), null };
                    if (_tryGetSprite.Invoke(null, args) is bool ok && ok) sprite = args[1] as Sprite;
                }
                catch { /* one bad id must not take the overlay down */ }
            }
            _spriteCache[index] = sprite;
            return sprite;
        }

        /// Draws a Sprite in IMGUI: its pixels are a window into a packed
        /// atlas, so the draw has to be told which corner of the texture.
        private void DrawSprite(Rect rect, uint index, float fraction = 1f)
        {
            var sprite = ResolveSprite(index);
            var tex = sprite == null ? null : sprite.texture;
            if (tex == null) return;
            var tr = sprite.textureRect;
            GUI.DrawTextureWithTexCoords(rect, tex,
                new Rect(tr.x / tex.width, tr.y / tex.height, tr.width * fraction / tex.width, tr.height / tex.height));
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
        private bool TryBindings(string raw, string roleName, out string roleKey, out List<Binding> bindings)
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
                        Logger.LogWarning($"Build hotkeys: {roleName} = '{raw}' — '{parts[i]}' is not a modifier " +
                                          "(use Ctrl, Shift or Alt). Ignored.");
                        return false;
                }
            }

            // Case-correct the base key so 'ctrl-g' works as well as 'Ctrl-G'.
            var baseKey = parts[parts.Length - 1];
            var match = ValidKeys.FirstOrDefault(k => string.Equals(k, baseKey, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Logger.LogWarning($"Build hotkeys: {roleName} = '{raw}' — '{baseKey}' is not a key name. Ignored.");
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

            if (roleEntries.Count == 0)
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
                .Replace("__CYCLE__", Mathf.Max(0f, _cfgCycleSeconds.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                if (!RunLua(chunk)) return;
                _installed = true;
                _installedSignature = signature;
                _builds = 0;
                // Each match reloads the sprites through Engine.LoadSprite, so
                // last match's AssetIDs are not safe to assume still valid.
                _spriteCache.Clear();

                foreach (var pair in layout.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    Logger.LogInfo($"Build hotkeys: {pair.Key} -> {string.Join(", ", pair.Value.ToArray())}");
                }
                Logger.LogInfo($"Build hotkeys installed: {roleEntries.Count} roles on {layout.Count} keys.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Build hotkeys could not be installed: {e.Message}");
            }
        }

        private void Remove()
        {
            if (!_installed) return;
            _installed = false;
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
    local entries = {}
    for i = 1, #cands do
      local g = __Templates.Units[cands[i]]
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
      entries[i] = ((g and g.displayName) or cands[i]) .. '~' .. string.format('%d', icon)
        .. '~' .. string.format('%d', techOf(cands[i])) .. '~' .. string.format('%d', bg)
    end
    __SdbBuildHotkeysCycle = __SdbBuildHotkeysCount .. '|' .. key .. '|' .. idx
      .. '|' .. table.concat(entries, '|')

    -- The panel's own click handler: it wraps the file-local
    -- ExecuteConstructionAction, so this is exactly a button click.
    CP.ConstructionClickFunction(
      { mouseClickType = UIMouseClickType.Left, isShiftHeld = shift },
      { tpId = tpId, isFactory = isFactory, selectedUnits = units })
    return true
  end

  BH.Fire = function(key, shift, reverse)
    local ok, res = pcall(fire, key, shift, reverse)
    if not ok then Warn('BuildHotkeys: ' .. tostring(res)) return false end
    return res
  end

  -- Construction has the highest group priority, so these run before the
  -- Orders group; returning false when nothing matched lets the event fall
  -- through to whatever the key normally does.
  for _, b in ipairs({ __BINDINGS__ }) do
    if BH.saved[b.hk] == nil then BH.saved[b.hk] = grp[b.hk] or BH.NIL end
    grp[b.hk] = { press = function() return BH.Fire(b.key, b.shift, b.rev) end }
  end

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
  __SdbBuildHotkeys = nil
  __SdbBuildHotkeysCount = nil
  __SdbBuildHotkeysCycle = nil
end";
    }
}
