-- Unit Restrictions: which units are out of this match, from the options the
-- lobby host picked. Every check the mod adds (build menus, the host's
-- queue, build-order and can-build checks) asks IsRestricted.
--
-- A unit is out when the host picked it in the unit list, or when a section
-- it belongs to is switched off. Sections take mobile units only, and never
-- engineers or commanders, so every army can still build; experimentals are
-- every TECH4 unit, structures included.

local Options = Import("modoptions/sanctuarymods.unitrestrictions.lua").Options

local picked = type(Options.units) == "table" and Options.units or {}
local decided = {}

-- Anything restricted at all: with nothing, the mod changes nothing.
Any = (Options.noLand or Options.noAir or Options.noNaval or Options.noExperimental or next(picked) ~= nil) and true or false

-- The sections switched off, by the AI's layer names (AI useLayer).
LayersOff = { land = Options.noLand and true or nil, air = Options.noAir and true or nil, water = Options.noNaval and true or nil }

local function tagged(tag, tpId)
    return Tags[tag][tpId] and true or false
end

local function decide(tpId)
    if picked[tpId] then return true end
    if Options.noExperimental and tagged("TECH4", tpId) then return true end
    if not tagged("MOBILE", tpId) or tagged("ENGINEER", tpId) or tagged("COMMAND", tpId) then return false end
    return (Options.noLand and tagged("LAND", tpId))
        or (Options.noAir and tagged("AIR", tpId))
        or (Options.noNaval and tagged("NAVAL", tpId))
        or false
end

-- Tags are complete once the templates have loaded, which is before anything
-- builds, so each answer is worked out once. An answer asked for before the
-- unit's template is in (the AI's unit list can be read early) isn't kept.
function IsRestricted(tpId)
    if not Any or type(tpId) ~= "string" then return false end
    local answer = decided[tpId]
    if answer == nil then
        answer = decide(tpId) and true or false
        if rawget(Tags, "ALL_UNITS") and Tags.ALL_UNITS[tpId] then decided[tpId] = answer end
    end
    return answer
end

if Any and Warn then
    local list = {}
    for id in pairs(picked) do list[#list + 1] = id end
    table.sort(list)
    Warn(string.format("Unit Restrictions: land %s, air %s, naval %s, experimentals %s; units: %s",
        Options.noLand and "off" or "on", Options.noAir and "off" or "on", Options.noNaval and "off" or "on",
        Options.noExperimental and "off" or "on", #list > 0 and table.concat(list, " ") or "none"))
end
