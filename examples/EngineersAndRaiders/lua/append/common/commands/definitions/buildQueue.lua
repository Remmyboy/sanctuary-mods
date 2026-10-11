-- Appended to the game's common/commands/definitions/buildQueue.lua.
--
-- The host takes a client's "add these to the queue" request without checking
-- that the factory can build it: only the build menu filters. A restriction
-- that lives in the menu alone can be walked around by anything that queues
-- directly, and a unit the factory can't build sitting in its queue confuses
-- the queue clean-up later. So the host checks each request against the same
-- (filtered) build list the menu shows, and turns away what isn't on it.
--
-- A refused request is still acknowledged, and the factory's real queue sent
-- back, so the requesting client drops its prediction instead of waiting.

local originalReceive = RequestAddToBuildQueue.Receive

local function mayQueue(unit, tpId)
    local c = unit.tp and unit.tp.construction
    if not c or not c.canBuild then return false end
    if c.upgradesTo == tpId then return true end
    return buildQueueUtils.CanBuild(unit, tpId) and true or false
end

function RequestAddToBuildQueue.Receive(data)
    if not data then return originalReceive(data) end

    -- The request's lists run in step, one entry per unit.
    local keptUnits, keptEntries, keptTargets = {}, {}, {}
    for index, unitID in ipairs(data.unitsIDs) do
        local unit = GetUnitById(unitID)
        if unit and not mayQueue(unit, data.tpId) then
            OnPendingQueueOperationCompleted.Send(unitID)
            if unit.SendUpdateBuildQueueCommand then unit:SendUpdateBuildQueueCommand() end
        else
            keptUnits[#keptUnits + 1] = unitID
            keptEntries[#keptEntries + 1] = data.entryIds[index]
            keptTargets[#keptTargets + 1] = data.targetItemEntryIds[index]
        end
    end

    if #keptUnits == 0 then return end
    data.unitsIDs = keptUnits
    data.entryIds = keptEntries
    data.targetItemEntryIds = keptTargets
    return originalReceive(data)
end
