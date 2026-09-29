-- Sanctuary Mod API: a unit that borrows another's model (general.modelTpId)
-- and has no portrait of its own wears that model's portrait, instead of the
-- "coming soon" picture (which some UI mods hide, build button and all).
-- modapi/units.lua lists the portraits the picked mods' art packs bring.
local OwnPortraits = Import("modapi/units.lua").OwnPortraits
local stockInitializeTextures = InitializeTextures
function InitializeTextures(...)
    local results = { stockInitializeTextures(...) }
    for tpID, tp in pairs(__Templates.Units) do
        local modelTpId = tp.general and tp.general.modelTpId
        if modelTpId and not OwnPortraits[tpID] and UnitTemplateIDToIconID[modelTpId] then
            UnitTemplateIDToIconID[tpID] = UnitTemplateIDToIconID[modelTpId]
        end
    end
    return unpack(results, 1, table.maxn(results))
end
