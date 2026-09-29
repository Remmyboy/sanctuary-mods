-- Sanctuary Mod API: adjacency connectors of a modded faction look like
-- those of the stock faction it names in mod.json.
local ModFactions = Import("modapi/factions.lua")
local stockCreateAdjacencyConnectorPrefab = CreateAdjacencyConnectorPrefab
function CreateAdjacencyConnectorPrefab(faction, size)
    return stockCreateAdjacencyConnectorPrefab(ModFactions.LookOfTag(faction), size)
end
