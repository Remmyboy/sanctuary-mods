-- Sanctuary Mod API: each AI seat plays the AI the lobby host picked for it.
local ModAi = Import("modapi/ai.lua")

-- CreateArmies gives every lobby AI the default settings, then spawns the
-- starting units (where a modded faction's AI goes in). Seats the host gave
-- a mod's AI take it last, so the host's pick wins over the faction's.
local stockCreateArmies = CreateArmies
function CreateArmies(...)
    local results = { stockCreateArmies(...) }
    for _, army in pairs(Armies) do
        local options = army.lobbyOptions
        local pick = options and type(options.aiSettings) == "table" and ModAi.ForArmy(options.armyID)
        if pick then
            local s = options.aiSettings
            s.modDirectory = pick.directory
            s.modName = pick.name
            s.useLayer = { land = pick.land, air = pick.air, water = pick.water }
        end
    end
    return unpack(results, 1, table.maxn(results))
end
