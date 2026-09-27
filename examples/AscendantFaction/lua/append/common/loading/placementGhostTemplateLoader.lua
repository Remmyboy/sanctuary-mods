-- Appended to the game's common/loading/placementGhostTemplateLoader.lua.
--
-- A unit borrows another unit's model with general.modelTpId, which the unit
-- loader honours, but the placement ghost (the see-through building you drag
-- around before placing it) is built from the unit's own id and would find no
-- model. Build it from the borrowed id instead.

local originalCreate = CreatePlacementGhostPrefab

function CreatePlacementGhostPrefab(tp)
    local modelTpId = tp.general and tp.general.modelTpId
    if not modelTpId then
        return originalCreate(tp)
    end
    local ownTpId = tp.general.tpId
    tp.general.tpId = modelTpId
    local results = { pcall(originalCreate, tp) }
    tp.general.tpId = ownTpId
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2)
end
