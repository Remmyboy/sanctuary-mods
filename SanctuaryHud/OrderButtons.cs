using BepInEx.Configuration;

namespace SanctuaryHud
{
    // Fixes to the game's own order buttons (client/ui/ordersPanel.lua), so
    // they work in the game's panel and in the orders row that clicks them.
    //
    // Stop. Since 0.0.1.22 the panel builds Stop with CreateButton, which
    // wraps its click function as an order task: clicking it enters an
    // order mode whose task is a function, nothing is issued, and every
    // left click on the map after it is swallowed until a right-click. With
    // this on Stop clears the selection's orders again, as the hotkey does.
    //
    // Load. The panel has a Load button with no order behind it, and no
    // unit lists Load among its orders, so it is never enabled. Loading is
    // a right-click from a transport on a unit it can carry. With this on
    // the button is enabled for any selection with a transport (a unit that
    // can Unload), and clicking it enters an order mode like Unload's: the
    // next left click on a unit of yours that a transport can carry orders
    // the pick-up (IssueLoadOrder, the same order the right-click gives).
    // In 0.0.1.22 only transports whose prefab has attach-point bones carry
    // anything (the T2s and the Haulier; the T1s, Emissary and Albatross
    // have none), and the host drops the order for the rest without a word.
    //
    // Client side only, standard orders.
    internal static class OrderButtons
    {
        private static ConfigEntry<bool> _cfg;

        private static readonly LuaHook Hook = new LuaHook("__SdbOrderButtons", "order button fixes", InstallChunk)
        {
            LogInstalls = false,
        };

        internal static void Bind(ConfigFile config)
        {
            _cfg = config.Bind("QoL", "FixOrderButtons", true,
                "Make the game's Stop order button stop units again (in this build it does nothing and leaves the " +
                "mouse in an order mode), and give transports a working Load button: click it, then click one of your " +
                "units to pick it up.");
        }

        internal static void Shutdown() => Hook.Remove();

        internal static void Tick() => Hook.Tick(_cfg.Value);

        private const string InstallChunk = @"
if not __SdbOrderButtons then
  local S = { hooks = {}, buttons = {}, loadMode = false }
  local Panels = Import('client/ui/panels.lua')
  local OP = Import('client/ui/ordersPanel.lua').OrdersPanel
  local OM = Import('client/managers/orders/clientOrderMode.lua')
  local IEF = Import('client/inputEventsFunctions.lua')
  local SS = Import('client/input/selectionSystem.lua')
  local UIM = Import('client/ui/uiManager.lua')
  local OT = Import('common/commonOrders.lua').OrderTasks

  -- A class is a proxy: read and assign through it. A module's
  -- environment is a plain table.
  local function hook(tbl, key, make, plain)
    local orig = plain and rawget(tbl, key) or tbl[key]
    if type(orig) ~= 'function' then error('order buttons: nothing to hook at ' .. key) end
    local mine = make(orig)
    if plain then rawset(tbl, key, mine) else tbl[key] = mine end
    table.insert(S.hooks, { t = tbl, k = key, orig = orig, mine = mine, plain = plain })
  end
  local function setClick(button, fn)
    table.insert(S.buttons, { b = button, orig = button.leftClickFunction, mine = fn })
    button.leftClickFunction = fn
  end
  local function unhook()
    for _, h in ipairs(S.hooks) do
      local now = h.plain and rawget(h.t, h.k) or h.t[h.k]
      if now == h.mine then
        if h.plain then rawset(h.t, h.k, h.orig) else h.t[h.k] = h.orig end
      end
    end
    for _, h in ipairs(S.buttons) do
      if h.b.leftClickFunction == h.mine then h.b.leftClickFunction = h.orig end
    end
  end

  local function canTransport(unit)
    local orders = unit and unit.tp and unit.tp.general and unit.tp.general.orders
    return orders ~= nil and orders.Unload == true
  end

  local ok, err = pcall(function()
    local panel = Panels.OrdersPanel
    if not (panel and panel.buttons) then error('order buttons: no orders panel') end
    local stop, load = panel.buttons.Stop, panel.buttons.Load

    if stop then
      setClick(stop, function()
        if OM.IsOrderModeEnabled then OM.ResetOrderMode() end
        IEF.IssueClearOrder()
      end)
    end

    if load then
      setClick(load, function()
        OM.EnableOrderMode(OT.LOAD)
        if OM.IsOrderModeEnabled then
          S.loadMode = true
          UIM.SetCursor(UIM.CursorType.AllyOrder)
        end
      end)

      -- Any way out of order mode, or into another one, ends the load mode.
      hook(OM, 'ResetOrderMode', function(orig) return function(...)
        S.loadMode = false
        return orig(...)
      end end, true)

      -- The click in load mode: a unit of the transports' own army that a
      -- transport can carry (the host takes only those). True when issued,
      -- which ends the mode unless Shift is held.
      hook(OM, 'IssueOrderModeOrder', function(orig) return function(...)
        if __SdbOrderButtons ~= S or not S.loadMode then return orig(...) end
        if SS.SelectionBox:IsActive() then return false end
        local target = IEF.GetHoverUnit()
        if not (target and target.entityType == 'unit' and not target.dead
                and target:HasTags(Tags.TRANSPORTABLE)) then return false end
        for _, unit in pairs(SS.GetSelectedUnits()) do
          if canTransport(unit) and unit.armyId == target.armyId then
            IEF.IssueLoadOrder()
            return true
          end
        end
        return false
      end end, true)

      -- Enabled whenever the selection has a transport. The panel disables
      -- it again on each update (no unit lists Load), so this runs after.
      hook(OP, 'Update', function(orig) return function(self, selected, ...)
        local r = orig(self, selected, ...)
        if __SdbOrderButtons == S and self.buttons and self.buttons.Load then
          local any = false
          for _, unit in pairs(selected or {}) do
            if canTransport(unit) then any = true break end
          end
          if self.buttons.Load:GetDisabled() == any then self.buttons.Load:SetDisabled(not any) end
        end
        return r
      end end)
    end
  end)
  if not ok then
    unhook()
    error(err)
  end

  S.Remove = function()
    if S.loadMode then pcall(OM.ResetOrderMode) end -- lua-check: ok
    unhook()
    __SdbOrderButtons = nil
  end
  __SdbOrderButtons = S
end";
    }
}
