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
--   Events.IsHost()     -- true in the simulation, false in a client
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

local function call(what, fn, ...)
    local ok, err = xpcall(fn, debug.traceback, ...)
    if not ok then report(what, err) end
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
    _G.OnSimulationTickUpdate = function(...)
        original(...)
        afterTick()
    end
    if where == "host" then hookDefeats() end
end
