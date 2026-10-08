-- Balance Patch: a weapon whose muzzles aren't in its model (the Guardian T1 and T3 bombers, the
-- Chosen T3 bomber, units with no model) has no muzzle groups, and SelectTarget indexed the first
-- one. The error stopped the targeting update for every weapon queued after it, every tick, so one
-- such bomber with an enemy in range froze retargeting for the rest of the game. It picks no
-- target instead.
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.fixes ~= false then
    local balancePatchSelectTarget = HostWeapon.SelectTarget
    function HostWeapon:SelectTarget(...)
        local group = self.muzzleGroups and self.muzzleGroups[1]
        if not (group and group[1]) then return nil end
        return balancePatchSelectTarget(self, ...)
    end
end
