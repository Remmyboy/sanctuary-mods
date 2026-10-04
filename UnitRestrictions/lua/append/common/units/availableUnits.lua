
-- Unit Restrictions: appended to the game's common/units/availableUnits.lua.
--
-- The AI plans with this list: which strategies it can run (CanBuildTag),
-- what its factories want (GetWantUnits), what its engineers build
-- (GetWantStructures) and which upgrades to start all skip a unit that
-- reads as unavailable here. A restricted unit that only failed at the
-- build step left the AI choosing plans it could never finish, so it built
-- its economy and nothing else. Here a restricted unit reads as blacklisted
-- and the AI plans around it.
--
-- The list's entries are moved behind a metatable, so a lookup always asks
-- the rules first; nothing else reads it (the template loader's use of it is
-- switched off in the game).

local unitRestrictions = Import("unitrestrictions/rules.lua")
if unitRestrictions.Any then
    local listed = {}
    for tpId, available in pairs(AvailableUnits) do listed[tpId] = available end
    for tpId in pairs(listed) do AvailableUnits[tpId] = nil end
    setmetatable(AvailableUnits, {
        __index = function(_, tpId)
            if unitRestrictions.IsRestricted(tpId) then return false end
            return listed[tpId]
        end,
        __newindex = listed,
        __pairs = function() return next, listed, nil end,
    })
end
