-- Balance Patch: the stock AI builds generators until its energy income is a set
-- multiple of its alloy income (20 early, 13-15 later), made for units that cost
-- 10 to 20 energy per alloy. With land units at 6 (the "economy" section) it would
-- build far more power than it spends, so those multiples are scaled down.
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.economy ~= false then
    local energyRatioScale = 0.7
    local balancePatchLessRatio = LessThanEnergyToResourceRatioIncome
    local balancePatchMoreRatio = MoreThanEnergyToResourceRatioIncome

    function LessThanEnergyToResourceRatioIncome(armyIndex, baseName, ratio, ...)
        if type(ratio) == "number" then ratio = ratio * energyRatioScale end
        return balancePatchLessRatio(armyIndex, baseName, ratio, ...)
    end

    function MoreThanEnergyToResourceRatioIncome(armyIndex, baseName, ratio, ...)
        if type(ratio) == "number" then ratio = ratio * energyRatioScale end
        return balancePatchMoreRatio(armyIndex, baseName, ratio, ...)
    end
end
