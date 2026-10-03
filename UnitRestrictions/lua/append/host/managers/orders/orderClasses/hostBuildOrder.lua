
-- Unit Restrictions: appended to the game's host/managers/orders/orderClasses/hostBuildOrder.lua.
--
-- An engineer's build order places the structure's ghost before any builder
-- checks it may build it. No builder can start a restricted one, so the
-- ghost would sit there for good: the order is invalid instead, and the
-- order manager drops it and its ghost.

local unitRestrictions = Import("unitrestrictions/rules.lua")
if unitRestrictions.Any then
    local isOrderValid = HostBuildOrder.IsOrderValid

    function HostBuildOrder:IsOrderValid(...)
        if unitRestrictions.IsRestricted(self.tpId) then return false end
        return isOrderValid(self, ...)
    end
end
