-- Balance Patch: an aircraft's beam fired from inside an enemy shield's bubble hits the shield
-- (balancepatch/shields.lua).
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.fixes ~= false then
    Import("balancepatch/shields.lua").HookBeam(HostBeam, Import("common/layers.lua"))
end
