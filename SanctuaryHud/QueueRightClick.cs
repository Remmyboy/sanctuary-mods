using BepInEx.Configuration;

namespace SanctuaryHud
{
    // Right-clicking a factory queue tile takes from that tile, not from the
    // last item of the same unit further back.
    //
    // The game's click (constructionBuildQueuePanel.lua UpdateQueueAmount)
    // sends one of the item's queueItemIds, and ModifyBuildQueue takes a
    // removal from the back of the queue, from the first item holding that id.
    // With repeat build on, HostFactory:CompleteBuildQueueItem puts a copy of
    // each finished item on the end carrying the same ids, so a right-click on
    // the front tile takes from the copy at the back.
    //
    // This swaps UpdateQueueAmount on the panel's module for one that sends an
    // id no later item has; when every id of the item is shared, it takes the
    // items behind it off (last first), takes from the item, and puts them
    // back, as QueueReorder does. Left clicks go to the game's own function.
    // The HUD's build strip passes its clicks to the game's buttons, so this
    // covers it too. Client side only, standard queue commands.
    internal static class QueueRightClick
    {
        internal static ConfigEntry<bool> Enabled;

        // In each match's VM while switched on; LuaHook puts it back in a new
        // VM and takes it out when switched off or unloaded.
        private static readonly LuaHook Hook = new LuaHook("__SdbQueueClick", "factory queue right-click fix", InstallChunk);

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "QueueRightClickTakesClickedItem", true,
                "Fixes the game's factory queue: with repeat build on, right-clicking a queue item removes from the last " +
                "item of that unit in the queue instead of the one clicked. With this on it removes from the one clicked.");
        }

        internal static void Shutdown() => Hook.Remove();

        internal static void Tick() => Hook.Tick(Enabled != null && Enabled.Value);

        // Guarded, so a copy left in the VM by an earlier version of the mod
        // (which has no hash marker) is kept rather than wrapped twice; its
        // Remove takes it out the same way.
        private const string InstallChunk = @"
if not __SdbQueueClick then
  local P = Import('client/ui/constructionBuildQueuePanel.lua')
  local BQ = Import('common/commands/definitions/buildQueue.lua')
  local UI = Import('client/ui/uiManager.lua')
  if type(P.UpdateQueueAmount) ~= 'function' then error('SanctuaryHud: no queue click handler to fix') end
  local S = { orig = P.UpdateQueueAmount }

  -- The way a queue click goes: predicted locally, recorded as pending, sent.
  local function step(u, id, tpId, delta)
    local newId = buildQueueUtils.ModifyBuildQueue(u, id, tpId, delta, true)
    if not u.id then return end
    if delta > 0 then
      if not newId then return end
      id = newId
    end
    table.insert(u.buildQueuePendingOperations, { deltaAmount = delta, queueItemId = id, tpID = tpId })
    BQ.RequestQueueAmount.Send({ u.id }, { id }, tpId, delta)
  end

  local function takeFrom(u, k, tpId, delta)
    local q = u.predictedBuildQueue
    local item = q and q[k]
    if not item or item.tpId ~= tpId then return end
    -- Never past this item into another holding its id.
    if -delta > item.count then delta = -item.count end

    local later = {}
    for j = k + 1, #q do
      for id in pairs(q[j].queueItemIds) do later[id] = true end
    end
    for id in pairs(item.queueItemIds) do
      if not later[id] then return step(u, id, tpId, delta) end
    end

    -- Every id it has is on a repeat copy further back: take the items behind
    -- it off, last first, so it is the last holding its id, then put them back.
    local tail = {}
    for j = k + 1, #q do
      tail[#tail + 1] = { id = next(q[j].queueItemIds), tpId = q[j].tpId, count = q[j].count }
    end
    local id = next(item.queueItemIds)
    for j = #tail, 1, -1 do step(u, tail[j].id, tail[j].tpId, -tail[j].count) end
    step(u, id, tpId, delta)
    for j = 1, #tail do step(u, -1, tail[j].tpId, tail[j].count) end
  end

  P.UpdateQueueAmount = function(clickData, payload)
    if clickData.mouseClickType ~= UIMouseClickType.Right or IsObserver() then
      return S.orig(clickData, payload)
    end
    local ok, err = pcall(function()
      local delta = clickData.isShiftHeld and -5 or -1
      for _, u in pairs(payload.selectedUnits) do
        takeFrom(u, payload.queueIndex, payload.tpID, delta)
      end
      UI.SetUIDirty()
    end)
    if not ok then Warn('SanctuaryHud queue right-click: ' .. tostring(err)) end
  end
  S.mine = P.UpdateQueueAmount
  S.Remove = function()
    if P.UpdateQueueAmount == S.mine then P.UpdateQueueAmount = S.orig end
    __SdbQueueClick = nil
    UI.SetUIDirty()
  end
  __SdbQueueClick = S
  -- The queue's buttons hold the function they were made with: remake them.
  UI.SetUIDirty()
end";
    }
}
