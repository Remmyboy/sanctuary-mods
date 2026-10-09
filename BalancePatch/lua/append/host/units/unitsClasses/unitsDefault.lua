-- Balance Patch, "economy" section: the game starts each army with half its commander's storage
-- (unitsDefault.lua, HostCommander:OnStopBeingBuilt). A one-off gift as the commander comes in
-- tops that up to full (500 alloys and 5000 energy). Storage itself is unchanged.
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.economy ~= false then
    local startShare = 1
    local balancePatchCommanderBuilt = HostCommander.OnStopBeingBuilt
    function HostCommander:OnStopBeingBuilt(...)
        local result = balancePatchCommanderBuilt(self, ...)
        local storage = self.tp.economy and self.tp.economy.storage
        if storage and self.army then
            for name, amount in pairs(storage) do
                Armies[self.army.id]:GiveResources(name, amount * (startShare - 0.5))
            end
        end
        return result
    end
end
