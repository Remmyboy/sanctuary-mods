-- Sanctuary Mod API: on-screen panels for gameplay mods, written in Lua.
--
-- Part of the framework, not the game: the Mod API puts this file in place
-- (at modapi/ui.lua) when a picked gameplay mod uses it, and draws what it
-- describes on the game's own HUD canvas, in the game's font and at its UI
-- scale. A mod needs no DLL of its own to show a panel.
--
--   local UI = Import("modapi/ui.lua").UI
--
--   local panel = UI.Panel("alice.norush", { title = "NO RUSH", x = 20, y = 300 })
--   panel:Set({
--       UI.Text("Attacks allowed in 4:12", { size = 24, color = "FFC040" }),
--       UI.Row({ UI.Swatch("3DAFFF"), UI.Text("Bob"), UI.Button("Wave", function() ... end) }),
--       UI.Rule(),
--   })
--   panel:Show(false)
--   UI.Toast("Attacks allowed", "Good luck", { seconds = 6 })
--
-- Client side only: call it from a mod's clientScript (or anything the
-- client imports). Each player sees their own copy, so what one player's
-- panel shows is up to that player's client. A button's function runs in
-- that client too: to change the match, send a request to the host from it
-- (SendToHost) and act on it in the host script.
--
-- Players can drag a panel, resize it by the grip in its bottom corner, and
-- click its title to fold it away to the title and back. Each player's
-- position, size and folding are remembered by panel id, so give a panel an
-- id starting with your mod's id.
--
-- Colours are "RRGGBB" (or "#RRGGBB", "RRGGBBAA"), or anything with r, g, b
-- (or x, y, z) fields from 0 to 1, such as an army's colour.

UI = {}

local panels = {}           -- [id] = panel
local panelOrder = {}       -- ids, in creation order
local toasts = {}           -- the latest few: { id, title, text, seconds, color }
local toastSeq = 0
local revision = 0

local function colour(value)
    if value == nil then return nil end
    if type(value) == "string" then
        local hex = string.gsub(value, "^#", "")
        if string.find(hex, "^%x%x%x%x%x%x$") or string.find(hex, "^%x%x%x%x%x%x%x%x$") then return string.upper(hex) end
        return nil
    end
    local ok, r, g, b = pcall(function()
        return value.r or value.x, value.g or value.y, value.b or value.z
    end)
    if not ok or type(r) ~= "number" or type(g) ~= "number" or type(b) ~= "number" then return nil end
    local function byte(f) return math.max(0, math.min(255, math.floor(f * 255 + 0.5))) end
    return string.format("%02X%02X%02X", byte(r), byte(g), byte(b))
end

local function options(o)
    return type(o) == "table" and o or {}
end

-- ---- elements -----------------------------------------------------------------
--
-- Each returns a plain table describing the element; panels are rebuilt
-- from them on every Set, and the framework reuses what it already drew, so
-- calling Set every second costs little.

--- A line of text. o: size (canvas units, default 20), color,
--- width (wraps to that width), rich (TextMeshPro tags such as <b>; off by
--- default, so a player's name is shown as it is).
function UI.Text(text, o)
    o = options(o)
    return { t = "text", text = tostring(text or ""), size = o.size, color = colour(o.color),
             width = o.width, rich = o.rich and true or nil }
end

--- A button. onClick(button) runs in this client when it's clicked with
--- the left mouse button ("left") or the right ("right"). o: color (of the
--- label), enabled (false greys it out and ignores clicks), size.
function UI.Button(label, onClick, o)
    o = options(o)
    return { t = "button", text = tostring(label or ""), fn = onClick, color = colour(o.color),
             enabled = o.enabled ~= false, size = o.size }
end

--- Elements side by side. o: spacing (default 8).
function UI.Row(items, o)
    o = options(o)
    return { t = "row", items = items or {}, spacing = o.spacing }
end

--- Elements one above another. o: spacing (default 4).
function UI.Column(items, o)
    o = options(o)
    return { t = "column", items = items or {}, spacing = o.spacing }
end

--- A thin line across the panel.
function UI.Rule()
    return { t = "rule" }
end

--- Empty space, size canvas units each way (default 8).
function UI.Space(size)
    return { t = "space", size = size }
end

--- Stretchy space in a row: takes whatever width is left, so what comes
--- after it lines up on the right. A row with one in it stretches to the
--- panel's width, so the rows of a list line up with each other.
function UI.Fill()
    return { t = "fill" }
end

--- A small square of colour, such as an army's. size defaults to 16.
function UI.Swatch(color, size)
    return { t = "swatch", color = colour(color) or "FFFFFF", size = size }
end

--- A progress bar, value from 0 (empty) to 1 (full), stretched across the
--- width it sits in. o: color (of the filled part; default the HUD accent),
--- width (the least it shrinks to, default 120), height (default 10).
--- Mod API 1.9.
function UI.Bar(value, o)
    o = options(o)
    value = tonumber(value) or 0
    if value ~= value then value = 0 end
    return { t = "bar", value = math.max(0, math.min(1, value)), color = colour(o.color),
             width = o.width, height = o.height }
end

-- ---- what the framework reads ---------------------------------------------------

local function publish()
    revision = revision + 1
    local list = {}
    for _, id in ipairs(panelOrder) do
        local p = panels[id]
        list[#list + 1] = { id = id, rev = p.rev, title = p.title, x = p.x, y = p.y, align = p.align,
                            visible = p.visible, items = p.items }
    end
    _G.__ModApiUI = json.encode({ rev = revision, panels = list, toasts = toasts })
    _G.__ModApiUIRev = tostring(revision)
end

-- Copies an element tree for the framework, swapping each button's function
-- for a number the click comes back with.
local function flatten(items, callbacks)
    local out = {}
    if type(items) ~= "table" then return out end
    for _, item in ipairs(items) do
        if type(item) == "table" and type(item.t) == "string" then
            local copy = {}
            for k, v in pairs(item) do
                if k ~= "fn" and k ~= "items" then copy[k] = v end
            end
            if type(item.fn) == "function" then
                callbacks[#callbacks + 1] = item.fn
                copy.id = #callbacks
            end
            if item.items then copy.items = flatten(item.items, callbacks) end
            out[#out + 1] = copy
        end
    end
    return out
end

local Panel = {}
Panel.__index = Panel

--- Replaces everything in the panel with items (a list of elements).
function Panel:Set(items)
    if self.removed then return end
    local callbacks = {}
    self.items = flatten(items, callbacks)
    self.callbacks = callbacks
    self.rev = self.rev + 1
    publish()
end

function Panel:Show(visible)
    visible = visible ~= false
    if self.removed or self.visible == visible then return end
    self.visible = visible
    publish()
end

function Panel:SetTitle(title)
    title = title and tostring(title) or nil
    if self.removed or self.title == title then return end
    self.title = title
    publish()
end

function Panel:Remove()
    if self.removed then return end
    self.removed = true
    panels[self.id] = nil
    for i, id in ipairs(panelOrder) do
        if id == self.id then table.remove(panelOrder, i) break end
    end
    publish()
end

--- A new panel, shown and empty, or the existing one with that id.
--- o: title, x and y (where it first appears, in 1080p pixels from
--- the top-left; a player's drag overrides them), align = "right" to
--- measure x from the right edge of the screen instead.
function UI.Panel(id, o)
    assert(type(id) == "string" and id ~= "", "UI.Panel takes an id string")
    if panels[id] then return panels[id] end
    o = options(o)
    local panel = setmetatable({
        id = id, title = o.title and tostring(o.title) or nil, x = o.x or 20, y = o.y or 300,
        align = o.align == "right" and "right" or nil, visible = o.visible ~= false,
        items = {}, callbacks = {}, rev = 0,
    }, Panel)
    panels[id] = panel
    panelOrder[#panelOrder + 1] = id
    publish()
    return panel
end

--- A big notice across the top of the screen for a few seconds, over
--- everything else. o: seconds (default 6), color (of the title).
function UI.Toast(title, text, o)
    o = options(o)
    toastSeq = toastSeq + 1
    toasts[#toasts + 1] = { id = toastSeq, title = tostring(title or ""), text = text and tostring(text) or nil,
                            seconds = o.seconds or 6, color = colour(o.color) }
    while #toasts > 4 do table.remove(toasts, 1) end
    publish()
end

--- Called by the framework when a button is clicked.
function UI._Click(panelId, rev, index, button)
    local panel = panels[panelId]
    -- A click on something the panel has since replaced does nothing.
    if not panel or panel.rev ~= rev then return end
    local fn = panel.callbacks[index]
    if not fn then return end
    local ok, err = xpcall(function() fn(button or "left") end, debug.traceback)
    if not ok then Warn("[Mod API] UI button in " .. panelId .. " failed: " .. tostring(err)) end
end
