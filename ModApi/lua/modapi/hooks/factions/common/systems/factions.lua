-- Sanctuary Mod API: the picked gameplay mods' factions, after the game's
-- three. The lobby's faction value + 1 indexes this table, and the Mod API
-- numbers the modded ones in the host's pick order, so two faction mods can
-- be picked together. A faction with several commanders has an entry per
-- commander (variantOf names the first one's index).
local ModFactions = Import("modapi/factions.lua")
for _, f in ipairs(ModFactions.List) do
    FactionsData[f.index] = {
        name = f.name,
        tpLetter = f.tpLetter,
        tag = f.tag,
        initialUnit = f.initialUnit,
        commander = f.commander,
        variantOf = f.index ~= f.base and f.base or nil,
        mod = f.mod,
    }
end

-- A unit's faction is its tag's first entry, never a commander variant.
local stockGetUnitFactionData = GetUnitFactionData
function GetUnitFactionData(unit)
    for i, fac in ipairs(FactionsData) do
        if not fac.variantOf and unit:HasTags(Tags[fac.tag]) then
            return i, fac.name, fac.tag
        end
    end
    return stockGetUnitFactionData(unit)
end
