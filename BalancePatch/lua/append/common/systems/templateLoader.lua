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

local balancePatchLoadAllTemplates = LoadAllTemplates
function LoadAllTemplates(...)
    local result = { balancePatchLoadAllTemplates(...) }
    Warn(Import("balancepatch/patch.lua").Summary())
    return unpack(result)
end
