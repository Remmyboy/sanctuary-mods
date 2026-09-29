-- The rest of modapi/factions.lua: what the framework's hooks and a mod's
-- own Lua ask about the factions above. Every faction number here is the
-- game's: FactionsData's index, which is the lobby's faction value + 1.

local stockLetters = { EDA = "ue", CHOSEN = "uc", GUARD = "ug" }

local baseByTag = {}
for _, f in ipairs(List) do
    if f.index == f.base and not baseByTag[f.tag] then baseByTag[f.tag] = f end
end

--- The modded faction entry for a faction number (a commander choice has
--- its own number and entry), or nil for a stock faction.
function Get(index)
    for _, f in ipairs(List) do
        if f.index == index then return f end
    end
    return nil
end

--- The faction entry a mod declared, by the mod's id and the faction's key,
--- or nil when that mod isn't picked. The first commander's entry.
function Find(modId, key)
    for _, f in ipairs(List) do
        if f.mod == modId and f.key == key and f.index == f.base then return f end
    end
    return nil
end

--- Faction number -> faction tag, for every faction in the match. What the
--- stock AI's own three-entry tables said, with the modded factions added.
function TagsByIndex()
    local t = { [1] = "EDA", [2] = "CHOSEN", [3] = "GUARD" }
    for _, f in ipairs(List) do t[f.index] = f.tag end
    return t
end

--- The AI a faction number plays with ({ directory, name, land, air, water }),
--- or nil for the stock AI.
function AIOf(index)
    local f = Get(index)
    return f and f.ai or nil
end

--- The stock faction ("EDA", "CHOSEN" or "GUARD") a unit template should
--- look like: shields, build beams, factory platforms. Nil for a stock unit,
--- or one whose faction didn't say.
function LookFor(tags)
    if type(tags) ~= "table" then return nil end
    for _, t in ipairs(tags) do
        if stockLetters[t] then return nil end
    end
    for _, t in ipairs(tags) do
        local f = baseByTag[t]
        if f and f.looksLike then return f.looksLike end
    end
    return nil
end

--- The same, from a template's hasTags set.
function LookForHasTags(hasTags)
    if type(hasTags) ~= "table" or hasTags.EDA or hasTags.CHOSEN or hasTags.GUARD then return nil end
    for tag, f in pairs(baseByTag) do
        if hasTags[tag] and f.looksLike then return f.looksLike end
    end
    return nil
end

--- A faction tag as the stock faction it looks like, or itself.
function LookOfTag(tag)
    local f = baseByTag[tag]
    return f and f.looksLike or tag
end

--- The unit-id prefix of a stock faction ("ue" for "EDA").
function LetterOf(stockTag)
    return stockLetters[stockTag]
end

-- The AI takeover asks for default settings without saying for whom; the
-- hook around it leaves the army's faction here first.
local pendingFaction = nil

function SetPendingFaction(index)
    pendingFaction = index
end

function TakePendingFaction()
    local index = pendingFaction
    pendingFaction = nil
    return index
end
