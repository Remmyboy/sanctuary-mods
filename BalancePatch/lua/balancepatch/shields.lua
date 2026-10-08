-- Shields stop aircraft that fly inside them. A shield only blocks a shot whose path crosses
-- its bubble, so a gunship or bomber inside one hit everything under it. Here an aircraft's
-- projectile that starts inside an enemy bubble and lands inside the same bubble, or its beam
-- fired from inside one, hits the shield instead. Host only; installed from the appends.

local Units, Shields = __Entities.Units, __Entities.Shields

local function isAir(unit)
    return unit ~= nil and not unit.dead and unit:HasTags(Tags.AIR)
end

local function inside(shield, pos)
    local radii = shield.tp and shield.tp.radii
    if not radii then return false end
    local _, c = Engine.GetGlobalEntityWorldPosition(shield.id)
    if not c then return false end
    local dx, dy, dz = pos.x - c.x, pos.y - c.y, pos.z - c.z
    return dx * dx + dy * dy + dz * dz < radii.x * radii.x
end

-- An enabled shield, hostile to army, whose bubble holds pos.
local function covering(pos, army)
    for _, shield in pairs(Shields) do
        if shield.enabled and army:IsEnemy(shield.army) and inside(shield, pos) then return shield end
    end
end

-- The unit a bone belongs to, cached per bone (a bone's index is only reused once its unit is gone).
local owners = {}
local function ownerOf(boneId)
    local cached = owners[boneId.index]
    if cached and not cached.dead then return cached end
    local id = boneId
    for _ = 1, 16 do
        local unit = Units[id.index]
        if unit then owners[boneId.index] = unit; return unit end
        local _, parent = Engine.GetGlobalEntityParent(id)
        if not parent or not Engine.IsValidGlobalID(parent) then return nil end
        id = parent
    end
end

-- Projectiles remember where an aircraft fired them from.
function HookProjectile(HostProjectile)
    local init = HostProjectile.__init
    HostProjectile.__init = function(self, globalId, armyId, template, damage, damageRadius, lifetime, muzzleId, ...)
        init(self, globalId, armyId, template, damage, damageRadius, lifetime, muzzleId, ...)
        if muzzleId and Engine.IsValidGlobalID(muzzleId) and isAir(ownerOf(muzzleId)) then
            local _, pos = Engine.GetGlobalEntityWorldPosition(muzzleId)
            self.balanceAirOrigin = pos and { x = pos.x, y = pos.y, z = pos.z }
        end
    end
end

-- Wraps ProcessRayCollisionEvent: an aircraft's shot from inside an enemy bubble that hits a unit
-- or the ground inside it goes into the shield. tickStep is Constants.TickTimeStep.
function WrapRayCollision(original, tickStep)
    return function(ev)
        local projectile = __Entities.Projectiles[ev.rayGlobalID.index]
        local origin = projectile and projectile.balanceAirOrigin
        if origin then
            local army = Armies[projectile.armyId]
            local shield = army and covering(origin, army)
            if shield then
                local other = ev.otherGlobalID
                local unit = other.index ~= 0 and Engine.IsValidGlobalID(other) and Units[other.index]
                if other.index == 0 or (unit and army:IsEnemy(unit.army)) then
                    local d = math.max(ev.hitDistance - 0.01, 0)
                    local o, dir = ev.rayOrigin, ev.rayDirection
                    local hit = { x = o.x + dir.x * d, y = o.y + dir.y * d, z = o.z + dir.z * d }
                    if inside(shield, hit) then
                        shield:TakeDamage(projectile.damage)
                        local t = math.min(math.max(d / ev.rayMaxDistance, 0.01), 0.99) * tickStep
                        projectile:Destroy(t, ImpactSurface.Default, shield.id)
                        return
                    end
                end
            end
        end
        return original(ev)
    end
end

-- Wraps HostBeam:Fire: an aircraft's beam from inside an enemy bubble puts what it would do to
-- anything inside the bubble into the shield. layers is common/layers.lua.
function HookBeam(HostBeam, layers)
    local fire = HostBeam.Fire
    HostBeam.Fire = function(self)
        if not isAir(self.unit) then return fire(self) end
        local _, origin = Engine.GetGlobalEntityWorldPosition(self.id)
        local army = self.unit.army
        local shield = origin and covering(origin, army)
        if not shield then return fire(self) end
        local _, forward = Engine.GetGlobalEntityForwardDirection(self.id)
        local events = Engine.RayCast(layers.CollisionWorld.Collision, origin, forward, self.rangeMax,
            layers.CollisionLayer.Shields + layers.CollisionLayer.Units)
        for _, ev in ipairs(events) do
            if ev.otherGlobalID.index == 0 then return ev.hitDistance end
            if Engine.IsValidGlobalID(ev.otherGlobalID) then
                local unit = Units[ev.otherGlobalID.index]
                local other = Shields[ev.otherGlobalID.index]
                if unit and army:IsEnemy(unit.army) then
                    local d = ev.hitDistance
                    local hit = { x = origin.x + forward.x * d, y = origin.y + forward.y * d, z = origin.z + forward.z * d }
                    if inside(shield, hit) then shield:TakeDamage(self.damage) else unit:TakeDamage(self.damage, DestroyType.Kinetic) end
                    return d
                end
                if other and other.enabled and army:IsEnemy(other.army) then
                    other:TakeDamage(self.damage)
                    return ev.hitDistance
                end
            end
        end
        return self.rangeMax
    end
end
