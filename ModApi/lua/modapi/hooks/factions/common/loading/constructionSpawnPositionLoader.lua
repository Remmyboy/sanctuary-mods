-- Sanctuary Mod API: a modded faction's factories find the bone units are
-- built on the way the stock faction they look like does (the game goes by
-- the unit id's prefix).
local ModFactions = Import("modapi/factions.lua")
local stockGetConstructorSpawnBones = GetConstructorSpawnBones
function GetConstructorSpawnBones(tp, skeletonBones)
    local look = ModFactions.LookFor(tp and tp.tags)
    local letter = look and ModFactions.LetterOf(look)
    if not letter then return stockGetConstructorSpawnBones(tp, skeletonBones) end
    local tpId = tp.general.tpId
    tp.general.tpId = letter .. string.sub(tpId, 3)
    local results = { pcall(stockGetConstructorSpawnBones, tp, skeletonBones) }
    tp.general.tpId = tpId
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end
