-- Zone Control, client side: the zone count banner, and the names over the
-- shops and heroes.

local Panels = Import("client/ui/panels.lua")

-- The top bar, and HUD mods such as SanctuaryHud's economy strip, cover the
-- top of the screen where the banner sits, so it starts a line down.
local BannerLead = "\n"

---@type table<integer, string> unit index -> its name
local labels = {}

---@param data {text:string, enabled:boolean}
RegisterListener("client_ZoneControlStatus", function(data)
    Panels.LogPanel.SetPersistentText(BannerLead .. (data.text or ""))
    Panels.LogPanel.SetPersistentTextEnabled(data.enabled and true or false)
end)

---The host's full set of names, sent whenever one changes.
---@param data {labels: {index: integer, text: string}[]}
RegisterListener("client_ZoneControlLabels", function(data)
    local fresh = {}
    for _, label in ipairs(data.labels or {}) do
        fresh[label.index] = label.text
    end
    labels = fresh
end)

-- A name under a unit shows only while it's drawn continuously, so it's
-- drawn every frame, after the game's own frame update (clientMain.lua).
-- In the Playtest build names don't show at all, so the banner says which
-- shop is which as well.
local function DrawLabels()
    local units = __Entities.Units
    for index, text in pairs(labels) do
        local unit = units[index]
        if unit and unit.id then
            Engine.DrawCustomName(unit.id, text, 1)
        end
    end
end

local originalRenderUpdate = _G.OnRenderUpdate
_G.OnRenderUpdate = function(...)
    originalRenderUpdate(...)
    DrawLabels()
end
