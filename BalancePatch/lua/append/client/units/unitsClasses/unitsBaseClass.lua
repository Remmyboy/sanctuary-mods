-- Balance Patch: a death effect the size of the blast. The game plays one onDeath effect at
-- scale 1 where a unit dies. A unit whose death explosion reaches 30 or more (only the
-- Crucible, through the "economy" section) also fills the blast out to its edge.
local balancePatchCreateOnDeathEffect = ClientUnit.CreateOnDeathEffect
function ClientUnit:CreateOnDeathEffect()
    balancePatchCreateOnDeathEffect(self)
    local radius = 0
    for _, weapon in ipairs(self.tp.weapons or {}) do
        if weapon.category == "DeathExplosion" and (weapon.damageRadius or 0) > radius then
            radius = weapon.damageRadius
        end
    end
    if radius < 30 then return end
    local effects = self.tp.visuals and self.tp.visuals.effectsInstant and self.tp.visuals.effectsInstant.onDeath
    local prefab = effects and effects[1] and effects[1].particlePrefabID
    if not prefab then return end
    local p = self:GetSimPosition()
    local function spawn(x, z, scale)
        Engine.InstantiatePrefab(prefab, EngineClasses.float3(x, p.y, z), EngineClasses.float3(scale, scale, scale), GetIdentityQuaternion(), {})
    end
    spawn(p.x, p.z, 3)
    for ring, count in ipairs({ 6, 12 }) do
        local r = radius * 0.4 * ring
        for i = 1, count do
            local a = (i + ring * 0.5) * 2 * math.pi / count
            spawn(p.x + r * math.cos(a), p.z + r * math.sin(a), 2)
        end
    end
end
