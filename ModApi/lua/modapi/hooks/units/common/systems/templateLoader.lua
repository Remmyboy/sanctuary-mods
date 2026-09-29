-- Sanctuary Mod API: a unit that borrows another's model (general.modelTpId)
-- leaves that model's wreck, and gets the hierarchy maps that capture and
-- wrecks need. The game builds all three from the unit's own id.
local stockGenerateUnitWreckage = GenerateUnitWreckage
function GenerateUnitWreckage(tp, tpId)
    local modelTpId = tp and tp.general and tp.general.modelTpId
    return stockGenerateUnitWreckage(tp, modelTpId or tpId)
end

local function withModelIds(templates, fn, ...)
    local swapped = {}
    for _, t in pairs(templates or {}) do
        local g = t.general
        if g and g.modelTpId and g.tpId then
            swapped[g] = g.tpId
            g.tpId = g.modelTpId
        end
    end
    local results = { pcall(fn, ...) }
    for g, tpId in pairs(swapped) do g.tpId = tpId end
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end

local stockCreateUnitHierarchyMaps = CreateUnitHierarchyMaps
function CreateUnitHierarchyMaps(unitTemplates, ...)
    return withModelIds(unitTemplates, stockCreateUnitHierarchyMaps, unitTemplates, ...)
end

local stockCreateUnitWreckHierarchyMaps = CreateUnitWreckHierarchyMaps
function CreateUnitWreckHierarchyMaps(unitTemplates, ...)
    return withModelIds(unitTemplates, stockCreateUnitWreckHierarchyMaps, unitTemplates, ...)
end
