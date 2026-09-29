
-- Zone Control: tunes some unit templates as the game loads them, before
-- their colliders, range rings and movement are built from them. The
-- numbers, and why, are in zonecontrol/balance.lua (TemplateTuning).
local zoneControlReadUnitTemplate = ReadUnitTemplate
function ReadUnitTemplate(tp, tpId, ...)
    local tune = Import("zonecontrol/balance.lua").TemplateTuning[tpId]
    if tune and tp then
        if tune.visionRadius and tp.intel then tp.intel.visionRadius = tune.visionRadius end
        if tp.movement then
            for _, key in ipairs({ "speed", "acceleration", "rotationSpeed" }) do
                if tune[key] then tp.movement[key] = tune[key] end
            end
        end
        for _, weapon in ipairs(tp.weapons or {}) do
            if weapon.category ~= "DeathExplosion" then
                if tune.rangeMax and weapon.rangeMax then weapon.rangeMax = tune.rangeMax end
                if tune.damageRadius and weapon.damageRadius then weapon.damageRadius = tune.damageRadius end
            end
        end
    end
    return zoneControlReadUnitTemplate(tp, tpId, ...)
end
