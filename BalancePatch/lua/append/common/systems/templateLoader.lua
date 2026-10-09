-- Balance Patch: edits templates as the game reads them, before anything is
-- built from them. The changes are in balancepatch/changes.lua.
local balancePatchReadUnitTemplate = ReadUnitTemplate
function ReadUnitTemplate(tp, tpId, ...)
    Import("balancepatch/patch.lua").Patch(tp, tpId, "unit")
    return balancePatchReadUnitTemplate(tp, tpId, ...)
end

local balancePatchReadProjectileTemplate = ReadProjectileTemplate
function ReadProjectileTemplate(tp, tpId, ...)
    Import("balancepatch/patch.lua").Patch(tp, tpId, "projectile")
    return balancePatchReadProjectileTemplate(tp, tpId, ...)
end

-- "economy" section: wrecks reclaim 3x as fast. GenerateUnitWreckage gives a wreck its
-- unit's build time as harvestTime; reclaim progress and income both run off it, so a
-- third of it is three times the rate for the same total.
local balancePatchReclaimSpeed = 3
local balancePatchReadPropTemplate = ReadPropTemplate
function ReadPropTemplate(tp, tpId, ...)
    if tp and tp.general and tp.general.unitTpId and tp.economy and tp.economy.harvestTime
        and Import("modoptions/sanctuarymods.balancepatch.lua").Options.economy ~= false then
        tp.economy.harvestTime = math.max(1, tp.economy.harvestTime / balancePatchReclaimSpeed)
    end
    return balancePatchReadPropTemplate(tp, tpId, ...)
end

local balancePatchLoadAllTemplates = LoadAllTemplates
function LoadAllTemplates(...)
    local result = { balancePatchLoadAllTemplates(...) }
    Warn(Import("balancepatch/patch.lua").Summary())
    return unpack(result)
end
