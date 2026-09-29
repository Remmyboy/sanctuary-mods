-- Appended to the game's common/systems/tags.lua.
--
-- Every builder's build list is its template's `canBuild` expression (for
-- factories, e.g. "Tags.EDA * Tags.BUILDABLE_BY_T1_FACTORY * ..."), turned
-- into a set of unit ids by ParseTagsFromString. Both the build menu
-- (buildQueueUtils.GetBuildableTags) and the host's check on every queued
-- item (buildQueueUtils.CanBuild) go through it, so filtering its result
-- restricts what the UI offers and what the simulation accepts alike.
--
-- Only expressions naming a BUILDABLE_BY_ tag are touched: weapon targeting
-- and everything else that parses tags is left alone. Structures always stay
-- buildable, so factories, extractors and generators still work.

local Options = Import("modoptions/sanctuarymods.example.engineersandraiders.lua").Options
local original = _G.ParseTagsFromString
local filtered = {}

local function allowed(tpId)
    if not Tags.MOBILE[tpId] then return true end    -- structures
    if Tags.ENGINEER[tpId] then return true end      -- every tier of engineer
    if not Tags.RAIDER[tpId] then return false end
    if Options.raiders == "all" then return true end
    if Options.raiders == "none" then return false end
    return Tags.TECH1[tpId] and true or false        -- T1 raiders
end

function _G.ParseTagsFromString(str)
    local data = original(str)
    if type(str) ~= "string" or type(data) ~= "table" or not string.find(str, "BUILDABLE_BY_", 1, true) then
        return data
    end
    local cached = filtered[str]
    if cached then return cached end
    local result = setmetatable({}, getmetatable(data))
    for tpId, v in pairs(data) do
        if allowed(tpId) then result[tpId] = v end
    end
    filtered[str] = result
    return result
end

if Log then Log("Engineers and Raiders test mod: build lists limited to engineers and raiders (" .. tostring(Options.raiders) .. ").") end
