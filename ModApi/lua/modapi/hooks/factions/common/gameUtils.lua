-- Sanctuary Mod API: an AI army of a modded faction plays that faction's AI.
local ModFactions = Import("modapi/factions.lua")

-- The game asks without saying for which faction; the takeover hook in
-- AI/AIInit.lua leaves it with ModFactions first.
local stockGetDefaultAISettings = GetDefaultAISettings
function GetDefaultAISettings(faction)
    local settings = stockGetDefaultAISettings()
    faction = faction or ModFactions.TakePendingFaction()
    local ai = faction and ModFactions.AIOf(faction)
    if ai and settings.aiSettings then
        local s = settings.aiSettings
        s.modDirectory = ai.directory
        s.modName = ai.name
        s.useLayer = { land = ai.land, air = ai.air, water = ai.water }
    end
    return settings
end

-- CreateArmies gives every lobby AI its lobby settings, then spawns the
-- starting units: by then each army knows its faction.
local stockSpawnInitialUnits = SpawnInitialUnits
function SpawnInitialUnits(...)
    for _, army in pairs(Armies) do
        local options = army.lobbyOptions
        local ai = options and type(options.aiSettings) == "table" and ModFactions.AIOf(army.faction)
        if ai then
            -- Since game 0.0.1.21 the host sets each AI seat up in the lobby
            -- (difficulty, expansions, layers): only the AI itself changes,
            -- and the layers its AI can't play go off.
            local s = options.aiSettings
            local layers = s.useLayer or {}
            s.modDirectory = ai.directory
            s.modName = ai.name
            s.useLayer = {
                land = layers.land ~= false and ai.land,
                air = layers.air ~= false and ai.air,
                water = layers.water ~= false and ai.water,
            }
        end
    end
    return stockSpawnInitialUnits(...)
end
