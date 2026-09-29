-- Sanctuary Mod API: shield hits on a modded faction's units show the
-- impact effects of the stock faction it looks like. The game picks them by
-- the template's faction tag, and gives anything unknown Guard's.
local ModFactions = Import("modapi/factions.lua")
local stockSetUpShields = ClientUnit.SetUpShields
function ClientUnit:SetUpShields(...)
    local hasTags = self.tp and self.tp.hasTags
    local look = ModFactions.LookForHasTags(hasTags)
    if not look then return stockSetUpShields(self, ...) end
    hasTags[look] = true
    local results = { pcall(stockSetUpShields, self, ...) }
    hasTags[look] = nil
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end
