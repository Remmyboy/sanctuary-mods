-- Phantom-X, client side: the panel. Everything it shows comes from the
-- host (host.lua), which sends each player only what their role lets them
-- know; every button sends a request back for the host to check and carry
-- out. Drawn with the Mod API's Lua UI (modapi/ui.lua), so the mod needs no
-- DLL.

local UI = Import("modapi/ui.lua").UI
-- Replay notes are Mod API 1.6; an older one has none, and replays show the
-- recorder's view only.
local Replay = (function()
    local ok, m = pcall(Import, "modapi/replay.lua")
    if ok and type(m) == "table" and m.Replay then return m.Replay end
    return { Playing = function() return false end, Note = function() end, Get = function() return nil end }
end)()
local Events = Import("modapi/events.lua").Events

local RequestName = "PhantomXRequest"
local StateName = "PhantomXState"
local StoryName = "PhantomXStory"
local NoteKey = "sanctuarymods.phantomx"

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

-- Vision after an alliance changes. The game updates the army's intel mask
-- (client/army.lua SetRelation) but never re-checks the units already on the
-- map: each keeps the visible or hidden state, and the fog-lighting range
-- rings, it had before, until the host next sends that unit new intel. So a
-- broken alliance left an ex-ally's units drawn and lighting the fog, and a
-- new one left an ally's units hidden. The game's matches never change
-- alliances mid-match; this one does. So once the alliances change, every
-- unit, its beams, and every shield go through the game's own intel check
-- again, on the next tick (a change arrives as two commands, one each way).
do
    local ClientArmy = Import("client/army.lua")
    local stale = false

    local originalSetRelationship = ClientArmy.SetRelationship
    ClientArmy.SetRelationship = function(...)
        originalSetRelationship(...)
        stale = true
    end

    Events.OnTick(function()
        if not stale then return end
        stale = false
        for _, unit in pairs(__Entities.Units) do
            if unit.RecalculateIntel then
                -- Forced: OnIntelVision also decides the range rings by whether
                -- the unit is now ours or an ally's.
                pcall(unit.RecalculateIntel, unit, true) -- lua-check: ok
                if unit.RecalculateBeamsIntel then pcall(unit.RecalculateBeamsIntel, unit) end -- lua-check: ok
            end
        end
        for _, shield in pairs(__Entities.Shields) do
            if shield.RecalculateIntel then pcall(shield.RecalculateIntel, shield) end -- lua-check: ok
        end
    end)
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

-- Pushes what follows to the right edge, so the rows' buttons line up
-- (UI.Fill is Mod API 1.6; an older one gets plain space).
local function Fill()
    return UI.Fill and UI.Fill() or UI.Space(8)
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
    if type(s.paying) == "table" and s.paying.target == p.id then
        items[#items + 1] = UI.Text("marking " .. Thousands(s.paying.left) .. " left", { size = 18, color = Colours.paladin })
    end
    items[#items + 1] = Fill()
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
        items[#items + 1] = UI.Text("Paladin marks: " .. s.marks, { size = 20, color = Colours.paladin })
    end
    if type(s.paying) == "table" then
        -- The mark lands once it's paid, from stored alloys only: what the
        -- economy is spending never reaches it.
        local pay = s.paying
        local paid = pay.cost > 0 and (pay.cost - pay.left) / pay.cost or 0
        items[#items + 1] = UI.Text(string.format("Marking %s: %s of %s alloys left",
            pay.name, Thousands(pay.left), Thousands(pay.cost)), { size = 20, color = Colours.paladin })
        -- UI.Bar is Mod API 1.9; an older one shows the text alone.
        if UI.Bar then items[#items + 1] = UI.Bar(paid, { color = Colours.paladin }) end
        items[#items + 1] = UI.Text("Paid from storage: spend less to finish sooner", { size = 18, color = Grey })
    end
    if s.counts then items[#items + 1] = UI.Text(s.counts, { size = 20, color = Grey }) end

    if s.vote then
        if s.vote == 0 then
            items[#items + 1] = UI.Row({
                UI.Text("How many phantoms?", { size = 20 }),
                Fill(),
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
                Fill(),
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

-- ============================================================
-- Replays
-- ============================================================
--
-- A replay only holds what the recording player was sent: their own role,
-- and what they learnt. So once a match is over the host sends everyone the
-- whole story (who was what, and when things happened), and it is saved
-- beside this player's replay. Playing that replay, the panel tells the
-- story from it instead, for whichever player is being viewed: View on a row
-- switches to them. A replay recorded before this (or by a player who left
-- before the end) has only the recorder's view, as before.

local showAll = true    -- every role, or only what the viewed player knew

local function FocusArmy()
    local ok, id = pcall(function() return _G.GetFocusArmy and _G.GetFocusArmy() end)
    return ok and id or nil
end

local function StoryAt(story, now)
    local at = { roles = {}, dead = {}, revealed = {}, marked = {} }
    for _, e in ipairs(story.events or {}) do
        if (e.t or 0) > now then break end
        if e.kind == "assign" then
            at.assigned = true
            for _, r in ipairs(e.roles or {}) do at.roles[r.id] = r.role end
        elseif e.kind == "dead" then at.dead[e.id] = true
        elseif e.kind == "reveal" then at.revealed[e.id] = e.to or "everyone"
        elseif e.kind == "mark" and e.hit then at.marked[e.target] = true
        elseif e.kind == "war" then at.war = true
        elseif e.kind == "over" then at.result = e.result
        end
    end
    return at
end

---Whether the viewed player knew this player's role at this point.
local function Knew(at, viewer, id)
    if showAll or id == viewer then return true end
    if at.dead[id] then return true end
    local to = at.revealed[id]
    if not to then return false end
    local mine = viewer and at.roles[viewer]
    return to == "everyone" or to == mine or (to == "both" and (mine == "phantom" or mine == "paladin"))
end

local function RenderStory(story)
    local now = Events.GameTime()
    local at = StoryAt(story, now)
    local focus = FocusArmy()
    local viewing
    for _, p in ipairs(story.players or {}) do
        if p.id == focus then viewing = p end
    end

    local items = {}
    if viewing then
        local role = at.roles[viewing.id]
        items[#items + 1] = UI.Text("Viewing " .. viewing.name, { size = 22 })
        items[#items + 1] = UI.Text(role and string.upper(role) or "Assignment pending",
            { size = 26, color = role and Colours[role] or Colours.pending })
    else
        items[#items + 1] = UI.Text("Watching everyone", { size = 26, color = Colours.observer })
    end
    if at.result then
        items[#items + 1] = UI.Text(at.result, { size = 24, color = "3DAFFF" })
    elseif at.war then
        items[#items + 1] = UI.Text("Phantom war", { size = 22, color = Colours.phantom })
    elseif not at.assigned and story.assignAt then
        items[#items + 1] = UI.Text("Phantoms are chosen in " .. string.format("%d:%02d",
            math.floor(math.max(0, story.assignAt - now) / 60), math.floor(math.max(0, story.assignAt - now) % 60)),
            { size = 20, color = Grey })
    end
    items[#items + 1] = UI.Row({
        UI.Text(showAll and "Showing every role" or "Showing what they knew", { size = 18, color = Grey }),
        Fill(),
        UI.Button(showAll and "Hide spoilers" or "Show all", function()
            showAll = not showAll
            RenderStory(story)
        end, { size = 18 }),
    })

    items[#items + 1] = UI.Rule()
    for _, p in ipairs(story.players or {}) do
        local row = { UI.Swatch(ArmyColour(p.id), 16) }
        local alive = not at.dead[p.id]
        row[#row + 1] = UI.Text(p.name .. (p.id == focus and " (viewing)" or ""), { size = 20, color = alive and "FFFFFF" or Grey })
        local role = at.roles[p.id]
        if role and Knew(at, focus, p.id) then
            row[#row + 1] = UI.Text(string.upper(role), { size = 18, color = Colours[role] or Grey })
        end
        if at.marked[p.id] then row[#row + 1] = UI.Text("marked", { size = 18, color = Colours.paladin }) end
        if not alive then row[#row + 1] = UI.Text("out", { size = 18, color = Grey }) end
        row[#row + 1] = Fill()
        if p.id ~= focus then
            row[#row + 1] = UI.Button("View", function()
                pcall(function() _G.SetFocusArmy(p.id) end) -- lua-check: ok
            end, { size = 18 })
        end
        items[#items + 1] = UI.Row(row, { spacing = 8 })
    end
    panel:Set(items)
end

local function ReplayTick()
    local story = Replay.Get(NoteKey)
    if not story then return false end
    local ok, err = xpcall(RenderStory, debug.traceback, story)
    if not ok then Warn("Phantom-X replay panel: " .. tostring(err)) end
    return true
end

if Replay.Playing() then
    -- Redrawn as the replay plays, and at once when the view switches to
    -- another player (the replay may be paused, with no ticks to wait for).
    Events.Every(0.5, ReplayTick)
    local setFocus = _G.SetFocusArmy
    if type(setFocus) == "function" then
        _G.SetFocusArmy = function(...)
            local r = setFocus(...)
            pcall(ReplayTick) -- lua-check: ok
            return r
        end
    end
else
    -- Live: the whole story arrives once the match is over; keep it with
    -- this player's replay.
    RegisterListener("client_" .. StoryName, function(story)
        local ok, err = pcall(Replay.Note, NoteKey, story)
        if not ok then Warn("Phantom-X couldn't save the story for the replay: " .. tostring(err)) end
    end)
end

RegisterListener("client_" .. StateName, function(state)
    -- In a replay with the story, the story tells it.
    if Replay.Playing() and Replay.Get(NoteKey) then return end
    local ok, err = xpcall(Render, debug.traceback, state)
    if not ok then Warn("Phantom-X panel: " .. tostring(err)) end
end)
