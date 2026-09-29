-- Sanctuary Mod API: a unit that borrows another's model (general.modelTpId)
-- gets that model's placement ghost too. The game builds the ghost's model
-- path and its prefab name from the unit's own id: the model comes from the
-- borrowed id, and the name stays the unit's, or it would clash with the
-- model unit's own ghost.
local stockCreatePlacementGhostPrefab = CreatePlacementGhostPrefab
function CreatePlacementGhostPrefab(tp)
    local modelTpId = tp and tp.general and tp.general.modelTpId
    if not modelTpId then return stockCreatePlacementGhostPrefab(tp) end
    local ownTpId = tp.general.tpId
    local stockPrefabTemplate = EngineClasses.PrefabTemplate
    EngineClasses.PrefabTemplate = function(name, ...)
        if name == modelTpId .. "PlacementGhost" then name = ownTpId .. "PlacementGhost" end
        return stockPrefabTemplate(name, ...)
    end
    tp.general.tpId = modelTpId
    local results = { pcall(stockCreatePlacementGhostPrefab, tp) }
    tp.general.tpId = ownTpId
    EngineClasses.PrefabTemplate = stockPrefabTemplate
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end
