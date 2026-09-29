-- Sanctuary Mod API: a modded faction's units get the build beam and shield
-- materials of the stock faction it looks like (mod.json "looksLike").
-- Both are picked by a faction tag in the template's tags while its prefab
-- is built. The game's tag sets are already made by then, so lending the
-- tag for that moment changes nothing else.
local ModFactions = Import("modapi/factions.lua")
local stockCreateUnitPrefab = CreateUnitPrefab
function CreateUnitPrefab(tp)
    local look = ModFactions.LookFor(tp and tp.tags)
    if not look then return stockCreateUnitPrefab(tp) end
    table.insert(tp.tags, look)
    local results = { pcall(stockCreateUnitPrefab, tp) }
    for i = table.getn(tp.tags), 1, -1 do
        if tp.tags[i] == look then
            table.remove(tp.tags, i)
            break
        end
    end
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end
