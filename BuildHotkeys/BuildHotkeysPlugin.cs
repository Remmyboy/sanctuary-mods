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

        private ConfigEntry<bool> _cfgOverlay;
        private ConfigEntry<float> _cfgOverlaySeconds;
        private ConfigEntry<float> _cfgOverlayY;
        private ConfigEntry<float> _cfgOverlayIcon;
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
            _cfgOverlay = Config.Bind("Overlay", "Show", true,
                "After a build hotkey, show what it picked and the rest of that key's cycle.");
            _cfgOverlaySeconds = Config.Bind("Overlay", "Seconds", 2.5f,
                "How long the overlay stays up after the last press.");
            _cfgOverlayIcon = Config.Bind("Overlay", "IconSize", 64f,
                "Size of each icon in the overlay, in 1080p-logical pixels.");
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

        private GUIStyle _stCycleKey, _stCycleCaption;

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
                _stCycleCaption = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            }

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            var icon = Mathf.Clamp(_cfgOverlayIcon.Value, 16f, 256f);
            var captioned = _cfgOverlayNames.Value;
            const float cellPad = 6f, padX = 8f, padY = 8f, chipW = 34f, captionH = 22f;

            var cellW = icon + cellPad * 2f;
            var cellH = icon + cellPad * 2f;
            var count = _cycleNames.Length;
            var width = padX * 2f + chipW + cellW * count;
            var height = padY * 2f + cellH + (captioned ? captionH : 0f);

            var x = Mathf.Round((Screen.width / scale - width) * 0.5f);
            var y = _cfgOverlayY.Value;

            GUI.DrawTexture(new Rect(x, y, width, height), _texPanel);
            var previousColour = GUI.color;

            // The whole strip belongs to one key, so it is named once, off to
            // the left where it can never sit on top of the art.
            GUI.Label(new Rect(x + padX, y + padY, chipW, cellH), _cycleKey, _stCycleKey);

            for (var i = 0; i < count; i++)
            {
                var cellX = x + padX + chipW + i * cellW;
                var cellY = y + padY;
                var live = i + 1 == _cycleIndex;

                if (live)
                {
                    GUI.DrawTexture(new Rect(cellX, cellY, cellW, cellH), _texRowHover);
                    GUI.DrawTexture(new Rect(cellX, cellY + cellH - 3f, cellW, 3f), _texWhite);
                }

                // Options not landed on are faded, so the live one reads at a
                // glance without having to hunt for the highlight.
                GUI.color = live ? Color.white : new Color(1f, 1f, 1f, 0.3f);
                if (_cycleIcons != null && i < _cycleIcons.Length)
                    DrawSprite(new Rect(cellX + cellPad, cellY + cellPad, icon, icon), _cycleIcons[i]);
                GUI.color = previousColour;
            }

            if (captioned)
            {
                var live = _cycleIndex - 1;
                GUI.Label(new Rect(x, y + height - captionH - 3f, width, captionH),
                    live >= 0 && live < count ? _cycleNames[live] : "", _stCycleCaption);
            }

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
            string.Join("|", Roles.All.Select(r => r.Name + "=" + _cfgKeys[r.Name].Value).ToArray());

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

            var n = parts.Length - 3;
            _cycleNames = new string[n];
            _cycleIcons = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var entry = parts[i + 3];
                var tilde = entry.LastIndexOf('~');
                if (tilde < 0) { _cycleNames[i] = entry; continue; }
                _cycleNames[i] = entry.Substring(0, tilde);
                uint.TryParse(entry.Substring(tilde + 1), out _cycleIcons[i]);
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
        private void DrawSprite(Rect rect, uint index)
        {
            var sprite = ResolveSprite(index);
            var tex = sprite == null ? null : sprite.texture;
            if (tex == null) return;
            var tr = sprite.textureRect;
            GUI.DrawTextureWithTexCoords(rect, tex,
                new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
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

            string Compose(bool withShift) =>
                (ctrl ? "Ctrl-" : "") + (withShift ? "Shift-" : "") + (alt ? "Alt-" : "") + match;

            roleKey = Compose(shift);
            bindings = new List<Binding> { new Binding { Hotkey = roleKey, RoleKey = roleKey, Shift = shift } };

            // Mirror the stock behaviour where Shift means "queue five" — but
            // only when the binding did not already claim Shift for itself.
            if (!shift)
            {
                bindings.Add(new Binding { Hotkey = Compose(true), RoleKey = roleKey, Shift = true });
            }
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
                "{hk=" + Quote(b.Hotkey) + ",key=" + Quote(b.RoleKey) + ",shift=" + (b.Shift ? "true" : "false") + "}");

            var chunk = InstallChunk
                .Replace("__ROLES__", string.Join(",", roleEntries.ToArray()))
                .Replace("__BINDINGS__", string.Join(",", bindingEntries.ToArray()));

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

  local function fire(key, shift)
    local units = SS.GetSelectedEntities()
    if not units then return false end

    -- Same split the construction panel makes: engineers place structures,
    -- everything else that can build queues units.
    local engineerTags = Tags.COMMAND + Tags.ENGINEER + Tags.ENGINEERING_STATION
    local isEngineer, isFactory, buildable = false, false, {}
    for _, u in pairs(units) do
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

    -- Cycle only while the previous press is still uncommitted, i.e. its
    -- template is still on the cursor. Placing it, cancelling, or changing
    -- selection drops us back to the start. A factory never enters build mode,
    -- so repeat presses there queue more of the same rather than walking the
    -- cycle.
    local idx, st = 1, BH.state
    if st.key == key and st.mode == want
       and BM.GetBuildMode() and BM.GetBuildTpId() == st.tpId then
      idx = (st.index % #cands) + 1
    end
    local tpId = cands[idx]
    BH.state = { key = key, mode = want, index = idx, tpId = tpId }

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
      entries[i] = ((g and g.displayName) or cands[i]) .. '~' .. string.format('%d', icon)
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

  BH.Fire = function(key, shift)
    local ok, res = pcall(fire, key, shift)
    if not ok then Warn('BuildHotkeys: ' .. tostring(res)) return false end
    return res
  end

  -- Construction has the highest group priority, so these run before the
  -- Orders group; returning false when nothing matched lets the event fall
  -- through to whatever the key normally does.
  for _, b in ipairs({ __BINDINGS__ }) do
    if BH.saved[b.hk] == nil then BH.saved[b.hk] = grp[b.hk] or BH.NIL end
    grp[b.hk] = { press = function() return BH.Fire(b.key, b.shift) end }
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
