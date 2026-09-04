using System;
using System.Collections.Generic;
using System.Linq;
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

        private bool _installed;
        private float _accum;
        private string _installedSignature;
        private int _builds;

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

        private void Update()
        {
            SharedTick();

            if (!_cfgEnabled.Value)
            {
                if (_installed) Remove();
                return;
            }

            if (!InMatch)
            {
                // Each match builds a fresh Lua state, which takes the binding
                // with it — so the next one reinstalls from scratch.
                _installed = false;
                _installedSignature = null;
                return;
            }

            _accum += Time.unscaledDeltaTime;
            if (_accum < 1f) return;
            _accum = 0f;

            var signature = Signature();
            if (_installed)
            {
                // Rebound from the mod manager mid-match: swap the layout over.
                if (signature != _installedSignature)
                {
                    Remove();
                }
                else
                {
                    ReportBuilds();
                    return;
                }
            }

            if (!LuaReady) return;
            Install(signature);
        }

        private string Signature() =>
            string.Join("|", Roles.All.Select(r => r.Name + "=" + _cfgKeys[r.Name].Value).ToArray());

        private void ReportBuilds()
        {
            // A flat global, not a field on the state table: the read-back
            // bridge is lua_getglobal, so it can only resolve a bare name.
            var raw = GetLuaGlobal("__SdbBuildHotkeysCount");
            if (int.TryParse(raw, out var count) && count > _builds)
            {
                _builds = count;
                Logger.LogInfo($"Build hotkeys: {count} build(s) issued this match.");
            }
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

  local function candidates(role, buildable)
    local expr = role.expr()
    local out = {}
    for tpId in pairs(buildable) do
      -- DEMO_UI_ONLY templates are drawn as blank, unclickable buttons and
      -- left out of the panel's own hotkey list; they are not buildable.
      if expr[tpId] and not Tags.DEMO_UI_ONLY[tpId] and techOf(tpId) <= role.maxTier then
        table.insert(out, tpId)
      end
    end
    table.sort(out, function(a, b)
      local ta, tb = techOf(a), techOf(b)
      if ta ~= tb then return ta > tb end
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

    for _, role in ipairs(list) do
      if role.mode == want then
        local cands = candidates(role, buildable)
        if #cands > 0 then
          -- Cycle only while the previous press is still uncommitted, i.e. its
          -- template is still on the cursor. Placing it, cancelling, or
          -- changing selection drops us back to the highest tier. A factory
          -- never enters build mode, so repeat presses there queue more of the
          -- same rather than walking down the tiers.
          local idx, st = 1, BH.state
          if st.key == key and st.role == role.name
             and BM.GetBuildMode() and BM.GetBuildTpId() == st.tpId then
            idx = (st.index % #cands) + 1
          end
          local tpId = cands[idx]
          BH.state = { key = key, role = role.name, index = idx, tpId = tpId }
          -- The panel's own click handler: it wraps the file-local
          -- ExecuteConstructionAction, so this is exactly a button click.
          CP.ConstructionClickFunction(
            { mouseClickType = UIMouseClickType.Left, isShiftHeld = shift },
            { tpId = tpId, isFactory = isFactory, selectedUnits = units })
          __SdbBuildHotkeysCount = __SdbBuildHotkeysCount + 1
          return true
        end
      end
    end
    return false
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
  __SdbBuildHotkeys = nil
end";
    }
}
