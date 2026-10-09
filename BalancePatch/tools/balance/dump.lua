-- Balance tool, step 1: the game's unit and projectile templates with the Balance Patch
-- applied by the mod's own patch.lua, written as JSON. Runs under the game's LuaJIT outside
-- the game (tools/GameRef luarun); balance.mjs calls it, see there.
--
--   luarun dump.lua <game LJ\lua dir> <mod lua dir> <changes.lua> <options> <out.json>
--
-- options: "vanilla" (no patch at all) or "key=1,key=0,..." for every section; a section
-- not listed is on, as in patch.lua. Also written: the wreck templates the game derives
-- (templateLoader.lua GenerateUnitWreckage) and the adjacency buffs as the game registers
-- them (host/systems/adjacencyBuffs.lua run with a recording Buffs.RegisterBuff).

local gameLua, modLua, changesPath, optionSpec, outPath = ...
assert(outPath, "usage: dump.lua <game lua dir> <mod lua dir> <changes.lua> <options> <out.json>")
local function slash(p) return (p:gsub("\\", "/"):gsub("/$", "")) end
gameLua, modLua, changesPath = slash(gameLua), slash(modLua), slash(changesPath)

-- ---------------------------------------------------------------- files

local function listDirs(dir)
    local p = assert(io.popen('cmd /c dir /b /ad "' .. dir:gsub("/", "\\") .. '" 2>nul'))
    local out = {}
    for line in p:lines() do out[#out + 1] = line end
    p:close()
    table.sort(out)
    return out
end

local function exists(path)
    local f = io.open(path, "rb")
    if f then f:close() return true end
    return false
end

-- Globals a data file names that don't exist resolve to a symbol, written as "$Name.Field".
local function symbol(path)
    return setmetatable({}, {
        __index = function(_, k) return symbol(path .. "." .. tostring(k)) end,
        __symbol = path,
    })
end

-- Runs a file in its own environment, like the game's Import, and returns that environment.
local function runFile(path, env)
    local chunk = assert(loadfile(path))
    setfenv(chunk, env)
    chunk()
    return env
end

local function dataEnv()
    return setmetatable({}, { __index = function(_, k) return symbol(k) end })
end

-- ---------------------------------------------------------------- the patch

local options = {}
local vanilla = optionSpec == "vanilla"
if not vanilla then
    for key, value in optionSpec:gmatch("([%w_]+)=(%w+)") do
        options[key] = not (value == "0" or value == "false")
    end
end

-- Stand-ins for the game's globals that patch.lua and the game's files call.
-- lua-check: globals Warn, Log, Import
local warnings = {}
local modules = {}
function Warn(msg) warnings[#warnings + 1] = tostring(msg) end
function Log() end
function Import(path)
    if modules[path] then return modules[path] end
    local env
    if path:match("^modoptions/") then
        env = { Options = options }
    elseif path == "balancepatch/changes.lua" then
        env = runFile(changesPath, setmetatable({}, { __index = _G }))
    else
        env = runFile(modLua .. "/" .. path, setmetatable({}, { __index = _G }))
    end
    modules[path] = env
    return env
end

local Patch = not vanilla and Import("balancepatch/patch.lua") or nil

-- ---------------------------------------------------------------- templates

local function loadTemplates(dir, top, kind)
    local out = {}
    for _, folder in ipairs(listDirs(gameLua .. "/" .. dir)) do
        local tpId = folder:lower()
        local file = gameLua .. "/" .. dir .. "/" .. folder .. "/" .. tpId .. ".santp"
        if exists(file) then
            local tp = runFile(file, dataEnv())[top]
            if tp then
                if Patch then Patch.Patch(tp, tpId, kind) end
                out[tpId] = tp
            end
        end
    end
    return out
end

local projectiles = loadTemplates("common/projectiles/projectilesTemplates", "ProjectileTemplate", "projectile")
local units = loadTemplates("common/units/unitsTemplates", "UnitTemplate", "unit")

-- Wrecks as GenerateUnitWreckage makes them: half the alloys (at least 1), no energy,
-- harvest time = build time. Only the economy is kept.
local wrecks = {}
for tpId, tp in pairs(units) do
    local e = tp.economy
    if e then
        wrecks[tpId] = {
            harvestTime = e.buildTime or 10,
            alloys = (e.cost and e.cost.alloys) and math.max(1, math.floor(e.cost.alloys / 2)) or 10,
            energy = 0,
        }
    end
end

-- ---------------------------------------------------------------- adjacency

local adjacency = {}
do
    local tagsProxy = setmetatable({}, { __index = function(_, k)
        return setmetatable({ k }, {
            __add = function(a, b) local r = { unpack(a) } for _, v in ipairs(b) do r[#r + 1] = v end return setmetatable(r, getmetatable(a)) end,
            __mul = function(a, b) return b end,   -- STRUCTURE * (A + B): keep the target list
        })
    end })
    local recorded = {}
    local env = setmetatable({
        Tags = tagsProxy,
        Import = function(path)
            if path == "host/systems/buffs.lua" then
                return { RegisterBuff = function(buff) recorded[#recorded + 1] = buff end }
            end
            return Import(path)
        end,
    }, { __index = _G })
    runFile(gameLua .. "/host/systems/adjacencyBuffs.lua", env)
    for _, buff in ipairs(recorded) do
        local targets = {}
        for _, t in ipairs(buff.targetTags or {}) do targets[#targets + 1] = t end
        adjacency[buff.name] = { category = buff.category, extra = buff.extra, resource = buff.resource,
            targetTags = targets, applyMethod = buff.applyMethod }
    end
    -- unit adjacency field "T1EnergyGeneratorAdjacencyBuff" -> its buff names
    for name, list in pairs(env.AdjacencyBuffs or {}) do adjacency[name] = list end
end

-- ---------------------------------------------------------------- JSON

local function isArray(t)
    local n = 0
    for k in pairs(t) do
        if type(k) ~= "number" or k < 1 or k % 1 ~= 0 then return false end
        n = n + 1
    end
    for i = 1, n do if t[i] == nil then return false end end
    return true, n
end

local function encode(v, buf)
    local tv = type(v)
    if tv == "nil" then buf[#buf + 1] = "null"
    elseif tv == "boolean" then buf[#buf + 1] = tostring(v)
    elseif tv == "number" then
        if v ~= v or v == math.huge or v == -math.huge then buf[#buf + 1] = "null"
        elseif v % 1 == 0 and math.abs(v) < 1e15 then buf[#buf + 1] = string.format("%d", v)
        else buf[#buf + 1] = string.format("%.10g", v) end
    elseif tv == "string" then
        buf[#buf + 1] = '"' .. v:gsub('[%c"\\]', function(c)
            if c == '"' then return '\\"' elseif c == "\\" then return "\\\\" end
            return string.format("\\u%04x", c:byte())
        end) .. '"'
    elseif tv == "table" then
        local mt = getmetatable(v)
        if mt and mt.__symbol then return encode("$" .. mt.__symbol, buf) end
        local arr, n = isArray(v)
        if arr and n > 0 then
            buf[#buf + 1] = "["
            for i = 1, n do if i > 1 then buf[#buf + 1] = "," end encode(v[i], buf) end
            buf[#buf + 1] = "]"
        else
            local keys = {}
            for k in pairs(v) do keys[#keys + 1] = k end
            table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
            buf[#buf + 1] = "{"
            for i, k in ipairs(keys) do
                if i > 1 then buf[#buf + 1] = "," end
                encode(tostring(k), buf)
                buf[#buf + 1] = ":"
                encode(v[k], buf)
            end
            buf[#buf + 1] = "}"
        end
    else
        buf[#buf + 1] = "null"
    end
end

local summary = Patch and Patch.Summary() or "vanilla"
local buf = {}
encode({
    meta = { options = vanilla and "vanilla" or options, changes = vanilla and nil or changesPath, summary = summary, warnings = warnings },
    units = units, projectiles = projectiles, wrecks = wrecks, adjacency = adjacency,
}, buf)
local f = assert(io.open(outPath, "wb"))
f:write(table.concat(buf))
f:close()
print(summary .. (#warnings > 0 and ("; " .. #warnings .. " warnings") or ""))
