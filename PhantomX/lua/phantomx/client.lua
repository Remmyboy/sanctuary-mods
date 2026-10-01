-- Phantom-X, client side: the panel. Everything it shows comes from the
-- host (host.lua), which sends each player only what their role lets them
-- know; every button sends a request back for the host to check and carry
-- out. Drawn with the Mod API's Lua UI (modapi/ui.lua), so the mod needs no
-- DLL.

local UI = Import("modapi/ui.lua").UI

local RequestName = "PhantomXRequest"
local StateName = "PhantomXState"

local Grey = "A8B0BC"
local Colours = {
    phantom = "FF4A4A",
    innocent = "6BE36B",
    paladin = "FFD24A",
    pending = "D0D6DE",
    observer = "D0D6DE",
}

-- Top left, under the economy bar: the right side has the commander and idle panels.
local panel = UI.Panel("sanctuarymods.phantomx", { title = "PHANTOM-X", x = 20, y = 70 })
local lastAlert = 0

local function Request(op, target, value)
    SendToHost({ op = op, target = target, value = value }, RequestName)
end

local function ArmyColour(id)
    local army = Armies and Armies[id]
    return army and army.color or "FFFFFF"
end

local function Thousands(n)
    local s = tostring(math.floor(n + 0.5))
    local out = string.reverse(string.gsub(string.reverse(s), "(%d%d%d)", "%1,"))
    return (string.gsub(out, "^,", ""))
end

local function RoleLine(s)
    if s.role == "observer" then return "Observing", Colours.observer end
    if s.dead then return "You are out", Grey end
    if s.role == "phantom" then return "PHANTOM: kill everyone", Colours.phantom end
    if s.role == "paladin" then
        return s.marked and "PALADIN (marked): kill every phantom" or "PALADIN: kill every phantom", Colours.paladin
    end
    if s.role == "innocent" then return "INNOCENT: kill every phantom", Colours.innocent end
    return "Assignment pending", Colours.pending
end

local function PlayerRow(s, p)
    local items = { UI.Swatch(ArmyColour(p.id), 16) }
    local nameColour = p.alive and "FFFFFF" or Grey
    items[#items + 1] = UI.Text(p.name .. (p.me and " (you)" or ""), { size = 20, color = nameColour })
    if p.role then
        items[#items + 1] = UI.Text(string.upper(p.role), { size = 18, color = Colours[p.role] or Grey })
    end
    if not p.alive then
        items[#items + 1] = UI.Text("out", { size = 18, color = Grey })
    elseif not p.me and p.ally ~= nil then
        items[#items + 1] = UI.Text(p.ally and "ally" or "enemy", { size = 18, color = p.ally and Colours.innocent or Colours.phantom })
        if p.wants then items[#items + 1] = UI.Text("wants peace", { size = 18, color = Colours.paladin }) end
    end
    if p.canWar then
        items[#items + 1] = UI.Button("War", function() Request("war", p.id) end, { color = Colours.phantom, size = 18 })
    end
    if p.canAlly then
        local label = p.offered and "Withdraw" or (p.wants and "Accept peace" or "Offer peace")
        items[#items + 1] = UI.Button(label, function() Request("ally", p.id) end, { color = Colours.innocent, size = 18 })
    end
    if p.canMark then
        local label = "Mark" .. (s.markCost and (" (" .. Thousands(s.markCost) .. ")") or "")
        items[#items + 1] = UI.Button(label, function() Request("mark", p.id) end, { color = Colours.paladin, size = 18 })
    end
    return UI.Row(items, { spacing = 8 })
end

local function Render(s)
    local roleText, roleColour = RoleLine(s)
    local items = { UI.Text(roleText, { size = 26, color = roleColour }) }

    if s.result then
        items[#items + 1] = UI.Text(s.result, { size = 24, color = "3DAFFF" })
    end
    if s.timer then items[#items + 1] = UI.Text(s.timer, { size = 20, color = Grey }) end
    if s.bonus and s.phase == "war" then
        -- In the phantom war the bonus is a share of what you kill: the total so far.
        items[#items + 1] = UI.Text(string.format("Vampire: +%s alloys, +%s energy from kills (%s%%)",
            Thousands(s.bonus.alloys), Thousands(s.bonus.energy), tostring(s.bonus.percent)), { size = 20 })
    elseif s.bonus then
        items[#items + 1] = UI.Text(string.format("Bonus +%s alloys/s, +%s energy/s (%s%%)",
            tostring(s.bonus.alloys), Thousands(s.bonus.energy), tostring(s.bonus.percent)), { size = 20 })
    end
    if s.marks then
        local text = "Paladin marks: " .. s.marks
        if s.paying then text = text .. "  |  paying " .. Thousands(s.paying) .. " alloys" end
        items[#items + 1] = UI.Text(text, { size = 20, color = Colours.paladin })
    end
    if s.counts then items[#items + 1] = UI.Text(s.counts, { size = 20, color = Grey }) end

    if s.vote then
        if s.vote == 0 then
            items[#items + 1] = UI.Row({
                UI.Text("How many phantoms?", { size = 20 }),
                UI.Button("1", function() Request("vote", nil, 1) end),
                UI.Button("2", function() Request("vote", nil, 2) end),
                UI.Button("3", function() Request("vote", nil, 3) end),
            })
        else
            items[#items + 1] = UI.Text("You voted for " .. s.vote, { size = 20, color = Grey })
        end
    end
    if s.volunteer then
        if s.volunteer == "ask" then
            items[#items + 1] = UI.Row({
                UI.Text("Volunteer to be a phantom?", { size = 20 }),
                UI.Button("Yes", function() Request("volunteer", nil, true) end, { color = Colours.phantom }),
                UI.Button("No", function() Request("volunteer", nil, false) end),
            })
        else
            items[#items + 1] = UI.Text(s.volunteer == "yes" and "You volunteered" or "You didn't volunteer",
                { size = 20, color = Grey })
        end
    end

    items[#items + 1] = UI.Rule()
    for _, p in ipairs(s.players or {}) do items[#items + 1] = PlayerRow(s, p) end
    panel:Set(items)

    -- New notices go across the top of the screen, newest only.
    local newest
    for _, a in ipairs(s.alerts or {}) do
        if a.id > lastAlert then
            lastAlert = a.id
            newest = a
        end
    end
    if newest then UI.Toast(newest.title, newest.text, { seconds = 7, color = newest.color }) end
end

RegisterListener("client_" .. StateName, function(state)
    local ok, err = xpcall(Render, debug.traceback, state)
    if not ok then Warn("Phantom-X panel: " .. tostring(err)) end
end)
