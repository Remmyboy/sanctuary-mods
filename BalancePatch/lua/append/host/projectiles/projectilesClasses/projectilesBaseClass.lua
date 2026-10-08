-- Balance Patch: projectiles remember where an aircraft fired them from (balancepatch/shields.lua).
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.fixes ~= false then
    Import("balancepatch/shields.lua").HookProjectile(HostProjectile)
end
