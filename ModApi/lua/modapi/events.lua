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
-- Helpers for host scripts (Mod API 1.8):
--   Events.Players()          -- the player armies, AI included, by army id
--   Events.ArmyName(army)     -- the player's name, or the army's
--   Events.Ally(a, b), Events.Enemy(a, b)      -- both ways round
--   Events.Guard(what, fn, ...)  -- runs fn; an error is reported once
--   Events.Report(what, err)     -- in the log, and the match's message log
--   Events.Tell(army, text)      -- a line in one player's message log
--   Events.SendToEachClient(name, fn)  -- fn(army, clientId)'s table, if changed
--   Events.OnRequest(name, fn)   -- fn(army, data, clientId) for SendToHost(data, name)
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

-- Every tick, so nothing is made here: the timers still wanted are packed
-- down in place, and the due ones go in a list kept for the purpose. Which
-- run is settled before any of them does: a timer one of them sets up waits
-- for the next tick.
local dueNow = {}

local function runTimers(tick)
    local count = #timers
    local kept, due = 0, 0
    for i = 1, count do
        local t = timers[i]
        if not t.cancelled then
            if t.due <= tick then
                due = due + 1
                dueNow[due] = t
            end
            if t.every or t.due > tick then
                kept = kept + 1
                timers[kept] = t
            end
        end
    end
    for i = kept + 1, count do timers[i] = nil end
    for i = 1, due do
        local t = dueNow[i]
        dueNow[i] = nil
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
-- What is dealing damage right now, innermost last, and the units taking it.
-- Both run on every hit, so their records are kept and reused by depth
-- rather than made each time: sources[i] = { army, unit, cause }, hits[i] =
-- { victim, army, unit, cause, destroyType, final, info }. Nothing outside
-- this file sees them; the info table handlers get is made per hit, and only
-- when a handler needs it.
local sources, sourceDepth = {}, 0
local hits, hitDepth = {}, 0
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

-- Pops the sources back to depth d whatever happened (letting go of the
-- units they held), then returns or rethrows.
local function unwindSources(d, ok, ...)
    for i = sourceDepth, d + 1, -1 do
        local s = sources[i]
        s.army, s.unit, s.cause = nil, nil, nil
    end
    sourceDepth = d
    if not ok then error((...), 0) end
    return ...
end

local function withSource(army, unit, cause, fn, ...)
    local d = sourceDepth
    local s = sources[d + 1]
    if not s then
        s = {}
        sources[d + 1] = s
    end
    s.army, s.unit, s.cause = army, unit, cause
    sourceDepth = d + 1
    return unwindSources(d, pcall(fn, ...))
end

local function currentSource()
    return sourceDepth > 0 and sources[sourceDepth] or nil
end

local function currentInfo(destroyType)
    local src = currentSource()
    return {
        army = src and src.army or nil,
        unit = src and src.unit or nil,
        cause = src and src.cause or "none",
        destroyType = destroyType,
    }
end

-- The info for a hit, made the first time a handler needs it and shared by
-- every handler of that hit after, as one table always was.
local function infoOf(hit)
    local info = hit.info
    if not info then
        info = { army = hit.army, unit = hit.unit, cause = hit.cause, destroyType = hit.destroyType }
        hit.info = info
    end
    return info
end

local function currentHit(victim)
    local hit = hitDepth > 0 and hits[hitDepth] or nil
    if hit and hit.victim == victim then return hit end
    return nil
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
                return withSource(army, unit, "projectile", original, event, ...)
            end
        end)
        wrap(collision, "ProcessAreaDamage", function(original)
            return function(position, radius, damage, army, damageFriendly, ...)
                if not damageWanted then return original(position, radius, damage, army, damageFriendly, ...) end
                local outer = currentSource()
                if outer and outer.cause == "deathExplosion" then
                    return withSource(outer.army, outer.unit, outer.cause, original, position, radius, damage, army, damageFriendly, ...)
                elseif outer and (army == nil or outer.army == army) then
                    -- Splash from a projectile hit keeps the unit that fired.
                    return withSource(outer.army, outer.unit, "area", original, position, radius, damage, army, damageFriendly, ...)
                end
                return withSource(army, nil, "area", original, position, radius, damage, army, damageFriendly, ...)
            end
        end)
        wrap(HostBeam, "Fire", function(original)
            return function(self, ...)
                if not damageWanted or not self.unit then return original(self, ...) end
                return withSource(self.unit.army, self.unit, "beam", original, self, ...)
            end
        end)
        wrap(HostUnit, "CheckDashCollisionsWithUnits", function(original)
            return function(self, ...)
                if not damageWanted then return original(self, ...) end
                return withSource(self.army, self, "dash", original, self, ...)
            end
        end)
        wrap(HostUnit, "CreateDeathExplosions", function(original)
            return function(self, ...)
                if not damageWanted then return original(self, ...) end
                return withSource(self.army, self, "deathExplosion", original, self, ...)
            end
        end)

        -- The hit itself.
        wrap(HostUnit, "TakeDamage", function(original)
            return function(self, amount, destroyType, ...)
                if not damageWanted or self.dead or not self.canTakeDamage then
                    return original(self, amount, destroyType, ...)
                end
                -- Who is dealing it is noted now, as the hit starts.
                local d = hitDepth
                local hit = hits[d + 1]
                if not hit then
                    hit = {}
                    hits[d + 1] = hit
                end
                local src = currentSource()
                hit.victim, hit.final, hit.info = self, nil, nil
                hit.army = src and src.army or nil
                hit.unit = src and src.unit or nil
                hit.cause = src and src.cause or "none"
                hit.destroyType = destroyType
                hitDepth = d + 1
                local ok, err = pcall(original, self, amount, destroyType, ...)
                -- Read before the record is let go: a handler below may
                -- deal damage itself, and reuse it.
                local final = hit.final
                local info = final and #damagedHandlers > 0 and infoOf(hit) or nil
                hit.victim, hit.army, hit.unit, hit.info = nil, nil, nil, nil
                hitDepth = d
                if not ok then error(err, 0) end
                if info then
                    for _, fn in ipairs(damagedHandlers) do call("OnUnitDamaged", fn, self, final, info) end
                end
            end
        end)
        wrap(HostUnit, "ProcessDamage", function(original)
            return function(self, damage, ...)
                local amount = original(self, damage, ...)
                if not damageWanted then return amount end
                local hit = currentHit(self)
                if #damageModifiers > 0 then
                    local info = hit and infoOf(hit) or currentInfo(nil)
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
                local hit = currentHit(self)
                local info
                if hit then
                    info = infoOf(hit)
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

-- ---- helpers for host scripts ------------------------------------------------
--
-- What every rules mod ended up writing for itself. The game's modules are
-- imported when first needed: this file can be imported before they exist.

local function lobby() return Import("common/lobby.lua") end
local function session() return Import("common/commands/definitions/session.lua") end

--- The players' armies, AI included, by ascending army id: every army but
--- the civilian ones and empty start slots. A new list each call.
function Events.Players()
    local ids = {}
    for id in pairs(Armies or {}) do ids[#ids + 1] = id end
    table.sort(ids)
    local out = {}
    for _, id in ipairs(ids) do
        local army = Armies[id]
        local emptySlot = army.lobbyOptions and army.lobbyOptions.isEmptySlot
        if not army.civilian and not emptySlot then out[#out + 1] = army end
    end
    return out
end

--- The name the player chose in the lobby, or the army's own name.
function Events.ArmyName(army)
    return (army.lobbyOptions and army.lobbyOptions.playerName) or army.name
end

--- Makes two armies allies, both ways round.
function Events.Ally(a, b)
    a:SetAlly(b)
    b:SetAlly(a)
end

--- Makes two armies enemies, both ways round.
function Events.Enemy(a, b)
    a:SetEnemy(b)
    b:SetEnemy(a)
end

local reportedWhat = {}

--- Reports a problem once for each `what`: in the game's log (Warn), and on
--- the host in the match's message log too, where players see it. Name the
--- mod in `what` ("Alice's No Rush: spawning"). A traceback is folded onto
--- one line and cut short.
function Events.Report(what, err)
    what = tostring(what)
    if reportedWhat[what] then return end
    reportedWhat[what] = true
    local text = tostring(err or "")
    text = string.gsub(text, "stack traceback:", "")
    text = string.gsub(text, "%s*\n%s*", " < ")
    if #text > 400 then text = string.sub(text, 1, 400) .. "..." end
    local line = what .. ": " .. text
    if Warn then Warn(line) elseif Log then Log(line) end
    if side == "host" then pcall(function() session().AddLog.Send(line) end) end
end

--- Runs fn(...) and returns true; if it errors, reports it (Events.Report)
--- and returns false.
function Events.Guard(what, fn, ...)
    local ok, err = xpcall(fn, debug.traceback, ...)
    if not ok then Events.Report(what, err) end
    return ok
end

--- Host only: a line in the message log of the player an army belongs to.
--- Nothing for an AI's army.
function Events.Tell(army, text)
    local player = lobby().ArmyToPlayer[army.id]
    if player and player.clientID then
        local ok, err = pcall(session().AddLog.SendTo, player.clientID, text)
        if not ok then Events.Report("[Mod API] Events.Tell", err) end
    end
end

local sentTo = {}   -- [name] = { [client id] = what it was last sent, as JSON }

--- Host only: sends every client (each player, and each observer) the table
--- fn(army, clientId) returns for it, as SendToClient(data, name, clientId)
--- would, but only when it differs from what that client was last sent
--- under that name. army is the client's army, nil for an observer; return
--- nil to send nothing. The client receives it with
--- RegisterListener("client_" .. name, fn).
function Events.SendToEachClient(name, fn)
    local last = sentTo[name]
    if not last then
        last = {}
        sentTo[name] = last
    end
    for clientId, player in pairs(lobby().Players) do
        local army = player.armyID and Armies[player.armyID] or nil
        local data = fn(army, clientId)
        if data ~= nil then
            local text = json.encode(data)
            if text ~= last[clientId] then
                last[clientId] = text
                local ok, err = pcall(SendToClient, data, name, clientId)
                if not ok then Events.Report("[Mod API] Events.SendToEachClient " .. tostring(name), err) end
            end
        end
    end
end

local requestHandlers = {}
local requestsHooked = false

-- A client's SendToHost reaches the host as an ExecuteHostFunction command,
-- which the game passes on without saying which client sent it. The engine
-- does say, to the command's handler: that handler is wrapped here to keep
-- it, for the names a mod has asked for only. Everything else goes on to the
-- game as before.
local function hookRequests()
    if requestsHooked then return end
    requestsHooked = true
    local Call = session().ExecuteHostFunction
    local originalReceive = Call.Receive
    Call.Receive = function(data, command)
        local ok, decoded = pcall(json.decode, data and data.call or "")
        local fn = ok and type(decoded) == "table" and requestHandlers[decoded.functionName]
        if not fn then return originalReceive(data, command) end
        local clientId = command and command.clientID
        local player = clientId and lobby().Players[clientId]
        local army = player and player.armyID and Armies[player.armyID] or nil
        call("OnRequest", fn, army, decoded.functionData, clientId)
    end
end

--- Host only: runs fn(army, data, clientId) when a client calls
--- SendToHost(data, name), instead of the game's own handling. army is the
--- sender's army (nil for an observer), as the engine names the client it
--- came from, so a player can't act for another. One handler per name: a
--- later one replaces it.
function Events.OnRequest(name, fn)
    assert(type(name) == "string", "Events.OnRequest takes a request name")
    assert(type(fn) == "function", "Events.OnRequest takes a function")
    requestHandlers[name] = fn
    if side == "host" then hookRequests() end
end

--- Called once by the framework from the end of host/hostMain.lua or
--- client/clientMain.lua. Not for mods.
function Events._Install(where)
    if side then return end
    side = where
    if where == "host" and next(requestHandlers) then hookRequests() end
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
