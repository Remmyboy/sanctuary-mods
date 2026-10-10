using BepInEx.Configuration;

namespace SanctuaryHud
{
    // Two small fixes to what the game's client does with orders and queues.
    //
    // The upgrade badge. A factory wears an upgrade adornment over its icon
    // whenever an upgrade is anywhere in its queue (ClientUnit:
    // CheckShowUpgradingAdornment tests IsUpgradeQueued), so one queued
    // behind twenty tanks badges it as upgrading. With this on the badge is
    // up only while the upgrade is under way (IsUpgrading), refreshed when
    // it starts and ends.
    //
    // Assisting an unfinished factory. A right-click from engineers on an
    // unfinished building of yours is a repair, which ends when the building
    // is up: tell an engineer to help put up a factory, Shift-click it to
    // assist it once built, and it goes idle instead. The host's assist
    // order already builds an unfinished target first and then helps what
    // it produces (HostUnit:AssistBehaviorThread), so with this on that
    // right-click, on a building that can build, is an assist.
    //
    // Client side only, standard orders. The class methods go through the
    // class's own assignment, which carries them to every unit type that
    // doesn't define its own (classy.lua).
    internal static class OrderFixes
    {
        private static ConfigEntry<bool> _cfgBadge, _cfgAssist;

        private static readonly LuaHook Hook = new LuaHook("__SdbOrderFixes", "order fixes", Chunk)
        {
            LogInstalls = false,
        };

        private static string _chunk, _chunkSignature;

        internal static void Bind(ConfigFile config)
        {
            _cfgBadge = config.Bind("QoL", "UpgradeBadgeOnlyWhileUpgrading", false,
                "Show a factory's upgrade badge only while it is upgrading. The game shows it as soon as an upgrade is " +
                "anywhere in the factory's queue, however far back.");
            _cfgAssist = config.Bind("QoL", "AssistUnfinishedFactories", false,
                "Right-clicking an unfinished factory with engineers assists it instead of repairing it: they help " +
                "finish it, then go on helping with what it builds. The game's repair order ends once the building is up.");
        }

        internal static void Shutdown() => Hook.Remove();

        internal static void Tick() => Hook.Tick(_cfgBadge.Value || _cfgAssist.Value);

        /// The install chunk with the settings in; a change of setting is a
        /// different chunk, which LuaHook swaps in.
        private static string Chunk()
        {
            var signature = (_cfgBadge.Value ? "b" : "-") + (_cfgAssist.Value ? "a" : "-");
            if (_chunk == null || signature != _chunkSignature)
            {
                _chunkSignature = signature;
                _chunk = InstallChunk
                    .Replace("__BADGE__", _cfgBadge.Value ? "true" : "false")
                    .Replace("__ASSIST__", _cfgAssist.Value ? "true" : "false");
            }
            return _chunk;
        }

        private const string InstallChunk = @"
if not __SdbOrderFixes then
  local S = { hooks = {} }
  local CU = Import('client/units/unitsClasses/unitsBaseClass.lua').ClientUnit
  local IEF = Import('client/inputEventsFunctions.lua')

  -- A class is a proxy: read and assign through it (assigning updates the
  -- unit types under it). A module's environment is a plain table.
  local function hook(tbl, key, make, plain)
    local orig = plain and rawget(tbl, key) or tbl[key]
    if type(orig) ~= 'function' then error('order fixes: nothing to hook at ' .. key) end
    local mine = make(orig)
    if plain then rawset(tbl, key, mine) else tbl[key] = mine end
    table.insert(S.hooks, { t = tbl, k = key, orig = orig, mine = mine, plain = plain })
  end
  local function unhook()
    for _, h in ipairs(S.hooks) do
      local now = h.plain and rawget(h.t, h.k) or h.t[h.k]
      if now == h.mine then
        if h.plain then rawset(h.t, h.k, h.orig) else h.t[h.k] = h.orig end
      end
    end
  end

  -- Every badge as it should be now.
  local function refreshBadges()
    local focus = GetFocusArmy()
    for _, u in pairs(__Entities.Units) do
      if u.armyId == focus and not u.dead and u.tp and u.tp.construction then
        pcall(u.CheckShowUpgradingAdornment, u) -- lua-check: ok
      end
    end
  end

  local ok, err = pcall(function()
    if __BADGE__ then
      hook(CU, 'CheckShowUpgradingAdornment', function(orig) return function(self, ...)
        if __SdbOrderFixes ~= S then return orig(self, ...) end
        if not self.tp.construction or not (self.icons and self.icons.Upgrade) then return end
        self.icons.Upgrade:SetEnabled(self:IsUpgrading())
      end end)
      for _, key in ipairs({ 'OnStartUpgrading', 'OnEndUpgrading' }) do
        hook(CU, key, function(orig) return function(self, ...)
          local r = orig(self, ...)
          if __SdbOrderFixes == S then pcall(self.CheckShowUpgradingAdornment, self) end -- lua-check: ok
          return r
        end end)
      end
    end
    if __ASSIST__ then
      -- OnMouseRightUp calls this only for engineers on an unfinished (or
      -- damaged) unit of an ally; an unfinished one that builds gets the
      -- assist instead.
      hook(IEF, 'IssueRepairOrder', function(orig) return function(...)
        if __SdbOrderFixes == S then
          local t = IEF.GetHoverUnit()
          if t and t.tp and t.tp.construction and t.tp.construction.canBuild and t:HasTags(Tags.STRUCTURE)
             and not t:IsCompleted() and not t:IsUpgrading() then
            return IEF.IssueAssistOrder()
          end
        end
        return orig(...)
      end end, true)
    end
  end)
  if not ok then
    unhook()
    error(err)
  end

  S.Remove = function()
    unhook()
    __SdbOrderFixes = nil
    pcall(refreshBadges) -- lua-check: ok
  end
  __SdbOrderFixes = S
  refreshBadges()
end";
    }
}
