
-- Unit Restrictions: appended to the game's AI/AIInit.lua (host only).
--
-- The AI's plans are switched by layer (land, air, water): with a layer off
-- it runs the plans made for that, such as an air factory first when there's
-- no land. A section the host switched off turns the matching layer off for
-- every AI army, so the AI plays the match it is in rather than waiting on
-- land units it will never get. Seats keep any layers already off.

local unitRestrictions = Import("unitrestrictions/rules.lua")
if next(unitRestrictions.LayersOff) then
    local function restrictLayers(settings)
        if type(settings) ~= "table" then return end
        local layers = {}
        for layer, used in pairs(settings.useLayer or {}) do layers[layer] = used end
        for layer in pairs(unitRestrictions.LayersOff) do layers[layer] = false end
        settings.useLayer = layers
    end

    -- Match start: every AI seat, before its threads start.
    local initAll = InitAIInfrastructure
    function InitAIInfrastructure(...)
        for _, army in pairs(Armies) do
            if army.lobbyOptions then restrictLayers(army.lobbyOptions.aiSettings) end
        end
        return initAll(...)
    end

    -- An AI taking over a player's army mid-match gets fresh default
    -- settings inside this call, so its layers are set after it.
    local initOne = InitAIInfrastructureForPlayer
    function InitAIInfrastructureForPlayer(armyIndex, ...)
        local results = { initOne(armyIndex, ...) }
        local army = Armies[armyIndex]
        if army and army.ai then restrictLayers(army.ai.settings) end
        return unpack(results, 1, table.maxn(results))
    end
end
