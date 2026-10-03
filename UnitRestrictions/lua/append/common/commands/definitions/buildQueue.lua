
-- Unit Restrictions: appended to the game's common/commands/definitions/buildQueue.lua.
--
-- The host takes a client's "queue N of this unit" request as it comes; only
-- the build menu filters. A restricted unit asked for anyway (a hotkey, a
-- stale menu, a modified client) is turned away here, before it reaches the
-- queue. The request is still acknowledged and the factory's real queue sent
-- back, so the client drops its prediction instead of waiting for it.

local unitRestrictions = Import("unitrestrictions/rules.lua")
if unitRestrictions.Any then
    local receive = RequestQueueAmount.Receive

    function RequestQueueAmount.Receive(data)
        if not data or not data.deltaAmount or data.deltaAmount <= 0 or not unitRestrictions.IsRestricted(data.tpId) then
            return receive(data)
        end
        for _, unitID in ipairs(data.unitsIDs or {}) do
            local unit = GetUnitById(unitID)
            if unit then
                OnPendingQueueOperationCompleted.Send(unitID)
                if unit.SendUpdateBuildQueueCommand then unit:SendUpdateBuildQueueCommand() end
            end
        end
    end
end
