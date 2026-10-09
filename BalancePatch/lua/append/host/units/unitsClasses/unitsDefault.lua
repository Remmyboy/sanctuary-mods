-- Balance Patch, "startbank" option (off by default): the game starts each army with half its
-- commander's storage (250 alloys and 2500 energy of 500/5000). With the option on, a one-off gift
-- as the commander comes in tops that up to 80% (400 and 4000). Storage itself is unchanged.
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.startbank == true then
    local startShare = 0.8
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
