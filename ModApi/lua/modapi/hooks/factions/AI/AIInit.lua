-- Sanctuary Mod API: an army the AI takes over mid-match plays its
-- faction's AI too.
local ModFactions = Import("modapi/factions.lua")
local stockInitForPlayer = InitAIInfrastructureForPlayer
if stockInitForPlayer then
    function InitAIInfrastructureForPlayer(armyIndex, ...)
        ModFactions.SetPendingFaction(Armies[armyIndex] and Armies[armyIndex].faction)
        local results = { pcall(stockInitForPlayer, armyIndex, ...) }
        ModFactions.SetPendingFaction(nil)
        if not results[1] then error(results[2], 0) end
        return unpack(results, 2, table.maxn(results))
    end
end
