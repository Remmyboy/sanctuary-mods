
-- Unit Restrictions: appended to the game's common/utilities/buildQueueUtils.lua.
--
-- Every build list goes through these two. The build menu (and hotkey mods)
-- ask GetBuildableTags what a builder offers; the host asks CanBuild before
-- it queues, starts or keeps an item: factories, engineers, engineering
-- stations, structure and factory upgrades, and the AI's builders. Filtering
-- both takes a restricted unit off the menus and out of the simulation.

local unitRestrictions = Import("unitrestrictions/rules.lua")
if unitRestrictions.Any then
    local getBuildableTags = buildQueueUtils.GetBuildableTags
    local canBuild = buildQueueUtils.CanBuild
    -- The game hands out one cached table per build list; so does this,
    -- keeping its metatable, which the menu adds lists together with.
    local filtered = setmetatable({}, { __mode = "k" })

    function buildQueueUtils.GetBuildableTags(unit)
        local tags = getBuildableTags(unit)
        if type(tags) ~= "table" then return tags end
        local hit = filtered[tags]
        if hit then return hit end
        local result = setmetatable({}, getmetatable(tags))
        for tpId, v in pairs(tags) do
            if not unitRestrictions.IsRestricted(tpId) then result[tpId] = v end
        end
        filtered[tags] = result
        return result
    end

    function buildQueueUtils.CanBuild(unit, tpId, ...)
        if unitRestrictions.IsRestricted(tpId) then return false end
        return canBuild(unit, tpId, ...)
    end
end
