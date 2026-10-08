-- Balance Patch: applies balancepatch/changes.lua to unit and projectile
-- templates as the game reads them (common/systems/templateLoader.lua), so
-- everything built from a template afterwards (colliders, range rings, the
-- build menus, the AI's cost sums) sees the new numbers.
--
-- A change selects templates (ids, idPattern, tags, notTags, notIds) and edits fields
-- by path: "weapons.2.damage", "weapons.*.damage" (every weapon except death
-- explosions; where = { rangeRingType = "IndirectFire" } narrows them),
-- "economy.cost.alloys". expect = { path = value } skips the
-- change, with a warning, when the game's value is no longer the one the
-- change was made against, so a game patch can't silently stack with it.

local Sections = Import("balancepatch/changes.lua").Sections
local Options = Import("modoptions/sanctuarymods.balancepatch.lua").Options

local applied, skipped, units = 0, 0, {}

local function split(path)
    local parts = {}
    for part in string.gmatch(path, "[^%.]+") do
        table.insert(parts, tonumber(part) or part)
    end
    return parts
end

-- Calls fn(table, key) for every field the path names in node. where
-- (field = value) filters the elements the first "*" goes through.
local function matches(child, where)
    for k, v in pairs(where or {}) do
        if child[k] ~= v then return false end
    end
    return true
end

local function each(node, parts, i, fn, where)
    local key = parts[i]
    if i == #parts then
        if key == "*" then
            for k in pairs(node) do fn(node, k) end
        else
            fn(node, key)
        end
        return
    end
    if key == "*" then
        for _, child in ipairs(node) do
            if type(child) == "table" and child.category ~= "DeathExplosion" and matches(child, where) then
                each(child, parts, i + 1, fn)
            end
        end
    elseif type(node[key]) == "table" then
        each(node[key], parts, i + 1, fn, where)
    end
end

local function copy(value)
    if type(value) ~= "table" then return value end
    local out = {}
    for k, v in pairs(value) do out[k] = copy(v) end
    return out
end

local function same(a, b)
    if type(a) ~= type(b) then return false end
    if type(a) ~= "table" then
        if type(a) == "number" then return math.abs(a - b) < 1e-6 end
        return a == b
    end
    for k, v in pairs(a) do if not same(v, b[k]) then return false end end
    for k in pairs(b) do if a[k] == nil then return false end end
    return true
end

local function hasTag(tp, tag)
    for _, t in ipairs(tp.tags or {}) do
        if t == tag then return true end
    end
    return false
end

local function selects(change, tp, tpId)
    if change.ids then
        local found = false
        for _, id in ipairs(change.ids) do
            if id == tpId then found = true break end
        end
        if not found then return false end
    end
    if change.idPattern and not string.find(tpId, change.idPattern) then return false end
    for _, tag in ipairs(change.tags or {}) do
        if not hasTag(tp, tag) then return false end
    end
    for _, tag in ipairs(change.notTags or {}) do
        if hasTag(tp, tag) then return false end
    end
    for _, id in ipairs(change.notIds or {}) do
        if id == tpId then return false end
    end
    return true
end

local function expectHolds(change, tp, tpId)
    for path, want in pairs(change.expect or {}) do
        local ok = false
        each(tp, split(path), 1, function(t, k) ok = same(t[k], want) end, change.where)
        if not ok then
            Warn("Balance Patch: " .. tpId .. " " .. path .. " is not what this patch was made for; skipping: " .. (change.why or ""))
            return false
        end
    end
    return true
end

-- Returns how many fields it changed.
local function apply(change, tp, tpId)
    local n = 0
    for path, value in pairs(change.set or {}) do
        each(tp, split(path), 1, function(t, k)
            if t[k] ~= nil or not string.find(path, "*", 1, true) then
                t[k] = copy(value)
                n = n + 1
            end
        end, change.where)
    end
    for _, path in ipairs(change.clear or {}) do
        each(tp, split(path), 1, function(t, k)
            if t[k] ~= nil then
                t[k] = nil
                n = n + 1
            end
        end, change.where)
    end
    for path, factor in pairs(change.scale or {}) do
        each(tp, split(path), 1, function(t, k)
            if type(t[k]) == "number" then
                local v = t[k] * factor
                if change.round then v = math.floor(v + 0.5) end
                t[k] = v
                n = n + 1
            end
        end, change.where)
    end
    return n
end

-- kind: "unit" or "projectile"
function Patch(tp, tpId, kind)
    if not tp or not tpId then return end
    for _, section in ipairs(Sections) do
        if Options[section.key] ~= false then
            for _, change in ipairs(section.changes) do
                if (change.kind or "unit") == kind and selects(change, tp, tpId) then
                    if expectHolds(change, tp, tpId) then
                        local n = apply(change, tp, tpId)
                        if n > 0 then
                            applied = applied + n
                            units[tpId] = true
                        end
                    else
                        skipped = skipped + 1
                    end
                end
            end
        end
    end
end

function Summary()
    local n = 0
    for _ in pairs(units) do n = n + 1 end
    local on = {}
    for _, section in ipairs(Sections) do
        if Options[section.key] ~= false then table.insert(on, section.key) end
    end
    return string.format("Balance Patch: %d fields changed on %d templates, %d changes skipped (%s)",
        applied, n, skipped, table.concat(on, ", "))
end
