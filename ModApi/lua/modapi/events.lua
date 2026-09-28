-- Sanctuary Mod API: match events for gameplay mods.
--
-- Part of the framework, not the game: the Mod API puts this file in place
-- (at modapi/events.lua) whenever the lobby host has picked at least one
-- gameplay mod, and hooks it into the host's simulation and every client.
-- With nothing picked, none of it exists and the game is untouched.
--
--   local Events = Import("modapi/events.lua").Events
--
--   Events.OnMatchStart(function() ... end)        -- armies and map are set up
--   Events.After(20 * 60, function() ... end)      -- once, 20 minutes in
--   Events.Every(1, function() ... end)            -- every second
--   Events.OnTick(function(tick) ... end)          -- every tick (10 a second)
--   Events.OnArmyDefeated(function(army) ... end)  -- host only
--   Events.OnUnitKilled(function(victim, info) ... end)       -- host only
--   Events.ModifyDamage(function(victim, amount, info) return amount end)
--   Events.OnUnitDamaged(function(victim, amount, info) ... end)
--   Events.Kills(army)  -- enemy units the army has killed
--   Events.IsHost()     -- true in the simulation, false in a client
--
-- info = { army, unit, cause, destroyType }: who dealt the damage (either
-- may be nil) and how: "projectile", "area", "beam", "dash",
-- "deathExplosion", or "none" when nothing did (an army's defeat, a script).
-- The kill and damage hooks only go in when a mod uses one of these, once
-- the first tick has set the match up.
--   Events.GameTime()   -- seconds since the match started
--
-- Times are game time: they pause with the game and speed up with it. A
-- timer set before the match starts counts from the start. After and Every
-- return a handle with :Cancel().
--
-- The host runs the simulation; clients only show it. Rules (what can be
-- built, who wins, when attacks are allowed) belong in the host: a mod's
-- hostScript, or code that checks Events.IsHost(). A client-side handler is
-- for what one player sees.
--
-- A handler that errors is reported in the game's log with its traceback and
-- skipped; the others still run.

Events = {}

Events.TicksPerSecond = 10

local side = nil            -- "host" or "client", once installed
local started = false
local startHandlers = {}
local tickHandlers = {}
local defeatHandlers = {}
local timers = {}
local defeated = {}

local function report(what, err)
    local text = "[Mod API] " .. what .. " handler failed: " .. tostring(err)
    if Warn then Warn(text) elseif Log then Log(text) end
end

-- Handlers that have already failed once: a broken OnTick or ModifyDamage
-- would otherwise fill the log many times a second. The first failure is
-- logged in full, later ones are not; the handler keeps being called.
local failed = setmetatable({}, { __mode = "k" })

local function handlerFailed(what, fn, err)
    if failed[fn] then return end
    failed[fn] = true
    report(what, tostring(err) .. "\n(further errors from this handler are not logged)")
end

local function call(what, fn, ...)
    local ok, err = xpcall(fn, debug.traceback, ...)
    if not ok then handlerFailed(what, fn, err) end
end

local function currentTick()
    return started and (Tick or 0) or 0
end

--- True in the host's simulation, false in a client (including a replay).
function Events.IsHost()
    return side == "host"
end

--- "host", "client", or nil before the framework has hooked in.
function Events.Side()
    return side
end

--- Seconds of game time since the match started.
function Events.GameTime()
    return currentTick() / Events.TicksPerSecond
end

--- Runs fn once the match has started: on the host after the armies and
--- map are set up (the first tick), on a client with its first tick.
--- Registered after the start, fn runs straight away.
function Events.OnMatchStart(fn)
    assert(type(fn) == "function", "Events.OnMatchStart takes a function")
    if started then
        call("OnMatchStart", fn)
    else
        startHandlers[#startHandlers + 1] = fn
    end
end

--- Runs fn(tick) every simulation tick, after the game's own update.
function Events.OnTick(fn)
    assert(type(fn) == "function", "Events.OnTick takes a function")
    tickHandlers[#tickHandlers + 1] = fn
end

--- Host only: runs fn(army) once for each army that has been defeated
--- (its last unit gone), after the game has worked out who won.
function Events.OnArmyDefeated(fn)
    assert(type(fn) == "function", "Events.OnArmyDefeated takes a function")
    defeatHandlers[#defeatHandlers + 1] = fn
end

local function addTimer(seconds, every, fn, what)
    assert(type(seconds) == "number" and seconds >= 0, "Events." .. what .. " takes a number of seconds")
    assert(type(fn) == "function", "Events." .. what .. " takes a function")
    local ticks = math.max(every and 1 or 0, math.floor(seconds * Events.TicksPerSecond + 0.5))
    local timer = {
        due = currentTick() + ticks,
        every = every and ticks or nil,
        fn = fn,
        what = what,
    }
    function timer:Cancel() self.cancelled = true end
    timers[#timers + 1] = timer
    return timer
end

--- Runs fn once, `seconds` of game time from now (or from the start).
function Events.After(seconds, fn)
    return addTimer(seconds, false, fn, "After")
end

--- Runs fn every `seconds` of game time, the first time `seconds` from now.
function Events.Every(seconds, fn)
    return addTimer(seconds, true, fn, "Every")
end

local function runTimers(tick)
    local due = {}
    local keep = {}
    for _, t in ipairs(timers) do
        if not t.cancelled then
            if t.due <= tick then due[#due + 1] = t end
            if t.every or t.due > tick then keep[#keep + 1] = t end
        end
    end
    timers = keep
    for _, t in ipairs(due) do
        if not t.cancelled then
            call(t.what, t.fn)
            if t.every then t.due = t.due + t.every end
        end
    end
end

local function afterTick()
    if not started then
        started = true
        local handlers = startHandlers
        startHandlers = {}
        for _, fn in ipairs(handlers) do call("OnMatchStart", fn) end
    end
    local tick = Tick or 0
    runTimers(tick)
    for _, fn in ipairs(tickHandlers) do call("OnTick", fn, tick) end
end

-- ---- kills and damage (host) ------------------------------------------------
--
-- The game never records who hit a unit: HostUnit:TakeDamage(amount,
-- destroyType) has no source. So the framework notes the source around each
-- place damage comes from (a projectile hit, area damage, a beam, a dash, a
-- death explosion). It's all synchronous, so "whatever is dealing damage right
-- now" is exact. The unit a projectile came from is found through its muzzle,
-- remembered as weapons are set up.

local killHandlers = {}
local damageModifiers = {}
local damagedHandlers = {}
local killsByArmy = {}
local damageWanted = false     -- any mod listening; until then the hooks just pass through
local damageHooked = false
local sourceStack = {}         -- what is dealing damage right now, innermost last
local hitStack = {}            -- units taking damage right now: { victim, info, final }
local muzzleUnits = setmetatable({}, { __mode = "v" })

local hookDamage -- below

-- A mod has asked for kills or damage. The hooks go in once the first tick
-- has set the match up, or straight away if the match is already running; a
-- match where no mod asks never has them.
local function wantDamage()
    damageWanted = true
    if started and side == "host" then hookDamage() end
end

--- Host only: runs fn(victim, info) once for each unit killed, the tick it
--- dies, after it's marked dead. info = { army, unit, cause, destroyType }:
--- the army and unit that dealt the killing damage (either can be nil),
--- and cause "projectile", "area", "beam", "dash", "deathExplosion" or
--- "none" (nothing damaged it: an army's defeat, a script). Not called for
--- units removed rather than killed (captures, upgrades, Delete). Friendly
--- and self kills are reported as they are.
function Events.OnUnitKilled(fn)
    assert(type(fn) == "function", "Events.OnUnitKilled takes a function")
    killHandlers[#killHandlers + 1] = fn
    wantDamage()
end

--- Host only: fn(victim, amount, info) runs for every hit on a unit before
--- its health changes, and may return a new amount (a number); anything
--- else leaves it as it was. Several mods' modifiers apply in turn, in the
--- order they were registered. info is as for OnUnitKilled. The result
--- never goes below 0.
function Events.ModifyDamage(fn)
    assert(type(fn) == "function", "Events.ModifyDamage takes a function")
    damageModifiers[#damageModifiers + 1] = fn
    wantDamage()
end

--- Host only: runs fn(victim, amount, info) after a unit has taken a hit,
--- with the amount it took (after every ModifyDamage). The victim may have
--- died of it.
function Events.OnUnitDamaged(fn)
    assert(type(fn) == "function", "Events.OnUnitDamaged takes a function")
    damagedHandlers[#damagedHandlers + 1] = fn
    wantDamage()
end

--- Host only: enemy units an army (or army id) has killed. Counts from the
--- first time any mod calls this or listens for kills or damage, so call it
--- once from your host script to count from the start.
function Events.Kills(army)
    wantDamage()
    local id = type(army) == "table" and army.id or army
    return killsByArmy[id] or 0
end

-- Pops a stack back to depth n-1 whatever happened, then returns or rethrows.
local function unwind(stack, n, ok, ...)
    for i = #stack, n, -1 do stack[i] = nil end
    if not ok then error((...), 0) end
    return ...
end

local function withSource(source, fn, ...)
    local n = #sourceStack + 1
    sourceStack[n] = source
    return unwind(sourceStack, n, pcall(fn, ...))
end

local function currentInfo(destroyType)
    local src = sourceStack[#sourceStack]
    return {
        army = src and src.army or nil,
        unit = src and src.unit or nil,
        cause = src and src.cause or "none",
        destroyType = destroyType,
    }
end

local function wrap(tbl, name, make)
    local original = tbl and tbl[name]
    if type(original) ~= "function" then
        report("install", "the game has no " .. name .. " to hook; kill credit may be missing for it")
        return
    end
    tbl[name] = make(original)
end

function hookDamage()
    if damageHooked then return end
    damageHooked = true

    local ok, err = pcall(function()
        local HostUnit = Import("host/units/unitsClasses/unitsBaseClass.lua").HostUnit
        local collision = Import("host/collisionUpdate.lua")
        local HostMuzzle = Import("host/units/weaponsClasses/muzzleClass.lua").HostMuzzle
        local HostBeam = Import("host/units/weaponsClasses/beam.lua").HostBeam
        local Projectiles = __Entities and __Entities.Projectiles

        -- Which unit each muzzle belongs to, for projectile credit: units
        -- already on the map, then every muzzle made from now on.
        for _, unit in pairs((__Entities and __Entities.Units) or {}) do
            for _, weapon in pairs(unit.weapons or {}) do
                for _, muzzle in ipairs(weapon.muzzles or {}) do
                    if muzzle.id then muzzleUnits[muzzle.id.index] = unit end
                end
            end
        end
        wrap(HostMuzzle, "__init", function(original)
            return function(self, muzzleBoneID, boneName, unit, ...)
                if muzzleBoneID and unit then muzzleUnits[muzzleBoneID.index] = unit end
                return original(self, muzzleBoneID, boneName, unit, ...)
            end
        end)

        -- Sources.
        wrap(collision, "ProcessRayCollisionEvent", function(original)
            return function(event, ...)
                local projectile = damageWanted and Projectiles and event and event.rayGlobalID and Projectiles[event.rayGlobalID.index]
                if not projectile then return original(event, ...) end
                local army = Armies[projectile.armyId]
                local unit = projectile.muzzleId and muzzleUnits[projectile.muzzleId.index]
                if unit and unit.army ~= army then unit = nil end -- a muzzle id reused since
                return withSource({ army = army, unit = unit, cause = "projectile" }, original, event, ...)
            end
        end)
        wrap(collision, "ProcessAreaDamage", function(original)
            return function(position, radius, damage, army, damageFriendly, ...)
                if not damageWanted then return original(position, radius, damage, army, damageFriendly, ...) end
                local outer = sourceStack[#sourceStack]
                local source
                if outer and outer.cause == "deathExplosion" then
                    source = outer
                elseif outer and (army == nil or outer.army == army) then
                    -- Splash from a projectile hit keeps the unit that fired.
                    source = { army = outer.army, unit = outer.unit, cause = "area" }
                else
                    source = { army = army, unit = nil, cause = "area" }
                end
                return withSource(source, original, position, radius, damage, army, damageFriendly, ...)
            end
        end)
        wrap(HostBeam, "Fire", function(original)
            return function(self, ...)
                if not damageWanted or not self.unit then return original(self, ...) end
                return withSource({ army = self.unit.army, unit = self.unit, cause = "beam" }, original, self, ...)
            end
        end)
        wrap(HostUnit, "CheckDashCollisionsWithUnits", function(original)
            return function(self, ...)
                if not damageWanted then return original(self, ...) end
                return withSource({ army = self.army, unit = self, cause = "dash" }, original, self, ...)
            end
        end)
        wrap(HostUnit, "CreateDeathExplosions", function(original)
            return function(self, ...)
                if not damageWanted then return original(self, ...) end
                return withSource({ army = self.army, unit = self, cause = "deathExplosion" }, original, self, ...)
            end
        end)

        -- The hit itself.
        wrap(HostUnit, "TakeDamage", function(original)
            return function(self, amount, destroyType, ...)
                if not damageWanted or self.dead or not self.canTakeDamage then
                    return original(self, amount, destroyType, ...)
                end
                local hit = { victim = self, info = currentInfo(destroyType) }
                local n = #hitStack + 1
                hitStack[n] = hit
                unwind(hitStack, n, pcall(original, self, amount, destroyType, ...))
                if hit.final and #damagedHandlers > 0 then
                    for _, fn in ipairs(damagedHandlers) do call("OnUnitDamaged", fn, self, hit.final, hit.info) end
                end
            end
        end)
        wrap(HostUnit, "ProcessDamage", function(original)
            return function(self, damage, ...)
                local amount = original(self, damage, ...)
                if not damageWanted then return amount end
                local hit = hitStack[#hitStack]
                if not (hit and hit.victim == self) then hit = nil end
                if #damageModifiers > 0 then
                    local info = hit and hit.info or currentInfo(nil)
                    for _, fn in ipairs(damageModifiers) do
                        local okFn, result = xpcall(fn, debug.traceback, self, amount, info)
                        if not okFn then
                            handlerFailed("ModifyDamage", fn, result)
                        elseif type(result) == "number" and result == result then
                            amount = result
                        end
                    end
                    if amount < 0 then amount = 0 end
                end
                if hit then hit.final = amount end
                return amount
            end
        end)

        -- The kill. HostCommander:Destroy calls HostUnit.Destroy, so this
        -- reaches commanders too.
        wrap(HostUnit, "Destroy", function(original)
            return function(self, overkillRatio, destroyType, ...)
                local wasAlive = not self.dead
                original(self, overkillRatio, destroyType, ...)
                if not (damageWanted and wasAlive and self.dead) then return end
                local hit = hitStack[#hitStack]
                local info
                if hit and hit.victim == self then
                    info = hit.info
                    info.destroyType = destroyType or info.destroyType
                else
                    info = { cause = "none", destroyType = destroyType }
                end
                if info.army and self.army and info.army ~= self.army and not self.army:IsAlly(info.army) then
                    killsByArmy[info.army.id] = (killsByArmy[info.army.id] or 0) + 1
                end
                for _, fn in ipairs(killHandlers) do call("OnUnitKilled", fn, self, info) end
            end
        end)
    end)
    if not ok then report("install", "kill and damage hooks: " .. tostring(err)) end
end

local function hookDefeats()
    local ok, winCondition = pcall(Import, "host/winCondition.lua")
    if not ok or type(winCondition) ~= "table" or type(winCondition.CheckWinCondition) ~= "function" then
        report("install", "host/winCondition.lua has no CheckWinCondition; OnArmyDefeated won't fire")
        return
    end
    local original = winCondition.CheckWinCondition
    -- The game looks it up on the module each time, so replacing it there
    -- reaches every caller.
    winCondition.CheckWinCondition = function(army, ...)
        local result = original(army, ...)
        if army and not army.civilian and not defeated[army.id] and army.IsAlive and not army:IsAlive() then
            defeated[army.id] = true
            for _, fn in ipairs(defeatHandlers) do call("OnArmyDefeated", fn, army) end
        end
        return result
    end
end

--- Called once by the framework from the end of host/hostMain.lua or
--- client/clientMain.lua. Not for mods.
function Events._Install(where)
    if side then return end
    side = where
    local original = _G.OnSimulationTickUpdate
    if type(original) ~= "function" then
        report("install", "the game has no OnSimulationTickUpdate here; match events won't fire")
        return
    end
    local hooked = false
    _G.OnSimulationTickUpdate = function(...)
        original(...)
        -- The game's hooks go in after the first tick's own update, not
        -- before it: that update is where the game sets the match up
        -- (InitLobby, the map, the armies), and importing the unit and
        -- weapon code ahead of it caches the targeting set-up before it
        -- exists, so every unit with a weapon is built without muzzles.
        -- Nothing has fired yet, and units spawned by the set-up are found
        -- by the muzzle backfill. OnMatchStart handlers run after this.
        if not hooked then
            hooked = true
            if where == "host" then
                hookDefeats()
                if damageWanted then hookDamage() end
            end
        end
        afterTick()
    end
end
