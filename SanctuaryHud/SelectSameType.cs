using System;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Ctrl-A selects every unit of yours of the types already selected: a T1
    // tank and a T1 scout selected, Ctrl-A, and every finished T1 tank and T1
    // scout on the map is. The game's double-click does the same for one type
    // and only on screen; its Ctrl-A is a hold-to-filter for air units, which
    // still happens when nothing is selected.
    //
    // The binding is the game's own: a runtime swap of the press handler on
    // inputSystem.lua's LoadedActionMap.Selection["Ctrl-A"], which CallAction
    // reads live, as BuildHotkeys does for its keys. No file changes.
    internal static class SelectSameType
    {
        internal static ConfigEntry<bool> Enabled;

        private static bool _installed;
        private static float _accum;
        private static float _retryAt;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "CtrlASelectsSameType", false,
                "Ctrl-A selects every finished unit of yours of the types you have selected, across the whole map: " +
                "a T1 tank and a T1 scout selected, and Ctrl-A takes every T1 tank and T1 scout. With nothing " +
                "selected Ctrl-A keeps the game's own meaning (hold it to box-select only air units).");
        }

        internal static void Shutdown() => Remove();

        internal static void Tick()
        {
            _accum += Time.unscaledDeltaTime;
            if (_accum < 1f) return;
            _accum = 0f;

            EnsureLuaBridge();
            if (!LuaReady)
            {
                _installed = false;
                return;
            }
            var want = Enabled != null && Enabled.Value;
            if (_installed)
            {
                if (!want) Remove();
                // A new match's VM starts without it. (A number: the
                // read-back bridge reads no booleans.)
                else if (GetLuaGlobal("__SdbSameTypeVer") == null) _installed = false;
                else return;
            }
            if (!want || Time.unscaledTime < _retryAt) return;
            if (RunLua(InstallChunk))
            {
                _installed = true;
                _log?.LogInfo("Ctrl-A selects all units of the selected types for this match.");
            }
            else _retryAt = Time.unscaledTime + 30f;
        }

        private static void Remove()
        {
            if (!_installed) return;
            _installed = false;
            try
            {
                if (LuaReady) RunLua("if __SdbSameType then __SdbSameType.Remove() end");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Ctrl-A select could not be removed: {e.Message}");
            }
        }

        private const string InstallChunk = @"
if not __SdbSameType then
  local IS = Import('client/input/inputSystem.lua')
  local SS = Import('client/input/selectionSystem.lua')
  local grp = IS.LoadedActionMap and IS.LoadedActionMap.Selection
  local entry = grp and grp['Ctrl-A']
  if not entry then error('SanctuaryHud: no Ctrl-A binding to extend') end
  local S = { entry = entry, orig = entry.press }

  -- Every finished, selectable unit sharing a template with the selection.
  -- Keyed by local id, as the selection system keys everything it selects.
  local function selectSameTypes()
    if IsObserver() then return false end
    local want, any = {}, false
    for _, u in pairs(SS.GetSelectedUnits()) do
      local g = u.tp and u.tp.general
      if g then want[g.tpId] = true any = true end
    end
    if not any then return false end
    local out = {}
    for _, u in pairs(__Entities.Units) do
      local g = u.tp and u.tp.general
      if g and want[g.tpId] and u.localId and not u.dead and not u.deleted
         and u:IsSelectable() and u:IsCompleted() then
        out[u.localId.index] = u
      end
    end
    -- What was selected stays, a half-built one included.
    for k, e in pairs(SS.GetSelectedEntities() or {}) do
      if out[k] == nil then out[k] = e end
    end
    SS.SetSelectedEntities(out)
    return true
  end

  entry.press = function(...)
    local ok, res = pcall(selectSameTypes)
    if not ok then Warn('SanctuaryHud Ctrl-A: ' .. tostring(res)) end
    if ok and res then return true end
    if S.orig then return S.orig(...) end
  end
  S.mine = entry.press
  S.Remove = function()
    if S.entry.press == S.mine then S.entry.press = S.orig end
    __SdbSameType = nil
    __SdbSameTypeVer = nil
  end
  __SdbSameType = S
  __SdbSameTypeVer = 1
end";
    }
}
