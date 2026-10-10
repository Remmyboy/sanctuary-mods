-- Phantom-X, host side: the rules.
--
-- A port of Phantom-X for Forged Alliance (faf-phantomx v268: Novaprim3,
-- Duck_42, mead and others). Everyone starts allied. A few minutes in, some
-- players are secretly made phantoms. A phantom wins by being the last one
-- alive, and is fed a share of the innocents' income to do it; the
-- innocents win by killing every phantom. Paladins are innocents with a
-- smaller share of that bonus, which a phantom can take away by paying to
-- "mark" them. Phantoms (and paladins, by option) are revealed at set
-- times. When only phantoms are left, they turn on each other.
--
-- Players act through the Phantom-X panel (client.lua): break or offer
-- alliances, mark a suspected paladin, vote on the number of phantoms,
-- volunteer to be one. Each request reaches the host as a client-to-host
-- function call, with the client it came from, so nobody can act for
-- another player. Every player is sent only what their role lets them know.
--
-- Numbers are in balance.lua.

local Events = Import("modapi/events.lua").Events
-- The host helpers below (Events.Guard, OnRequest and the rest) came with
-- Mod API 1.8.
if not Events.OnRequest then
    Warn("Phantom-X needs Mod API 1.8.0 or later (a newer Mod Manager); its rules are off this match.")
    return
end
local Options = Import("modoptions/sanctuarymods.phantomx.lua").Options
local B = Import("phantomx/balance.lua")
local Lobby = Import("common/lobby.lua")
local SessionCommands = Import("common/commands/definitions/session.lua")
local WinCondition = Import("common/winCondition.lua").WinCondition

local RequestName = "PhantomXRequest"
local StateName = "PhantomXState"
local StoryName = "PhantomXStory"
local StorageSource = -7171     -- the economy's key for the phantoms' extra storage

local Colours = {
    phantom = "FF4A4A",
    innocent = "6BE36B",
    paladin = "FFD24A",
    neutral = "3DAFFF",
}

local PaladinRatio = { none = 0, ["1:1"] = 1, ["1:2"] = 1 / 2, ["1:3"] = 1 / 3, ["2:3"] = 2 / 3 }

local players = {}          -- every player army, AI included, by ascending id
local P = {}                -- [army] = what the rules know about that player
local phase = "pending"     -- pending, playing, war (the phantom war), over
local declareAt = Options.assignMinutes * 60
local phantoms, innocents, paladins = {}, {}, {}   -- innocents includes the paladins
local voteOpen, volunteerOpen = false, false
local decided = {}          -- [army id] = WinCondition once the match is decided
local known = {}            -- [viewer army] = { [army] = role } learnt from reveals and marks
local public = {}           -- [army] = role everyone knows
local alerts = { all = {} } -- [army or "all"] = the latest few notices
local alertSeq = 0
local nextRevealAt = nil    -- game time of the next reveal
local result = nil          -- how the match ended
local paying = {}           -- [phantom] = { left = alloys still owed, cost = the whole price, target = army }
local vampireHooked = false
local dirty = true
local started = false
local bonusEntity = {}      -- [army] = the economy entity its bonus arrives through
local story = {}            -- what happened when, sent to everyone once it's over (for replays)

-- ============================================================
-- The story, for replays
-- ============================================================
--
-- A replay holds only what the recording player was sent, so it can't show
-- the other players' roles. Once the match is over, the whole story goes to
-- every client, which saves it beside its replay (modapi/replay.lua).

local function Story(kind, fields)
    fields = fields or {}
    fields.kind = kind
    fields.t = math.floor(Events.GameTime() * 10 + 0.5) / 10
    story[#story + 1] = fields
end

-- ============================================================
-- Reporting
-- ============================================================
--
-- A problem goes in the game's log and the match's message log, once for
-- each what: "Phantom-X: bonus".

local Guard = Events.Guard

-- ============================================================
-- Players
-- ============================================================

local Name = Events.ArmyName
local Ally, Enemy = Events.Ally, Events.Enemy

local function Alive(army)
    return P[army] ~= nil and not P[army].dead
end

local function ClientOf(army)
    local player = Lobby.ArmyToPlayer[army.id]
    return player and player.clientID
end

local function Living(list)
    local out = {}
    for _, army in ipairs(list) do
        if Alive(army) then out[#out + 1] = army end
    end
    return out
end

local function Plural(n, one, many)
    return n .. " " .. (n == 1 and one or many)
end

local function Clock(seconds)
    seconds = math.max(0, math.floor(seconds + 0.5))
    return string.format("%d:%02d", math.floor(seconds / 60), seconds % 60)
end

-- ============================================================
-- Notices
-- ============================================================

local function Push(key, notice)
    local list = alerts[key] or {}
    list[#list + 1] = notice
    while #list > 4 do table.remove(list, 1) end
    alerts[key] = list
end

---A notice on the panel and in the message log. to: an army, a list of
---armies, or nil for everyone.
local function Alert(to, title, text, colour)
    alertSeq = alertSeq + 1
    local notice = { id = alertSeq, title = title, text = text, color = colour }
    local line = title .. (text and (": " .. text) or "")
    if to == nil then
        Push("all", notice)
        SessionCommands.AddLog.Send(line)
    else
        if to.id then to = { to } end
        for _, army in ipairs(to) do
            Push(army, notice)
            Events.Tell(army, line)
        end
    end
    dirty = true
end

-- ============================================================
-- Economy
-- ============================================================

local function Row(livePhantoms)
    if livePhantoms <= 0 then return nil end
    return B.Economy[math.min(livePhantoms, #B.Economy)]
end

local function Give(army, alloys, energy)
    local economy = army.economy
    if not economy then return end
    if alloys > 0 then economy:GiveResources("alloys", alloys) end
    if energy > 0 then economy:GiveResources("energy", energy) end
end

---Pays a bonus (per tick) as income rather than a gift: a resource entity
---on the army's economy, so the game counts it in the income it shows (its
---own panel, the HUD) and in its stall maths, instead of resources turning
---up in storage from nowhere. The economy scales generation by its income
---multiplier, which the shares were already taken from, so that is
---divided out. The economy is only told when the amount changes: each
---update copies the entity and redoes the army's totals.
local function SetBonusIncome(army, alloys, energy)
    local economy = army.economy
    if not economy then return end
    local e = bonusEntity[army]
    if not e then
        if alloys <= 0 and energy <= 0 then return end
        e = { id = "phantomx.bonus", category = "generation", income = { alloys = 0, energy = 0 }, outcome = {} }
        economy:AddResourceEntity(e)
        bonusEntity[army] = e
    end
    local m = economy.incomeAndBuildMultiplier
    if type(m) ~= "number" or m <= 0 then m = 1 end
    local a, en = math.max(0, alloys) / m, math.max(0, energy) / m
    if e.income.alloys == a and e.income.energy == en then return end
    -- Changed in place: the economy keeps its own copy of what it last added.
    e.income.alloys, e.income.energy = a, en
    economy:UpdateResourceEntity(e)
end

---What the panel shows of a bonus, per second.
local function ShowBonus(army, alloys, energy, percent)
    local b = P[army].bonus
    if not b then
        b = {}
        P[army].bonus = b
    end
    b.alloys, b.energy, b.percent = alloys, energy, percent
end

---The bonus an army is being paid this tick, as it arrives.
local function BonusOf(army)
    local e = bonusEntity[army]
    if not e then return 0, 0 end
    local m = army.economy and army.economy.incomeAndBuildMultiplier
    if type(m) ~= "number" or m <= 0 then m = 1 end
    return e.income.alloys * m, e.income.energy * m
end

local paid = {}             -- [army] = true for those GiveBonus is paying

local function StopTheRest()
    for army in pairs(bonusEntity) do
        if not paid[army] then SetBonusIncome(army, 0, 0) end
    end
end

---Once a second, and when alliances change: each live phantom gets its
---share of the live innocents' combined income, and each unmarked paladin
---a part of that, as income, paid every tick until the next time. Paladins
---are innocents, so their own bonus is left out of what is shared (it would
---feed itself otherwise); everyone not paid this time goes back to none.
local function GiveBonus()
    for army in pairs(paid) do paid[army] = nil end
    local live = Living(phantoms)
    local row = Row(#live)
    if not row then return StopTheRest() end
    local multiplier = Options.bonus / 100

    local alloys, energy = 0, 0
    local liveInnocents = Living(innocents)
    for _, army in ipairs(liveInnocents) do
        local ownA, ownE = BonusOf(army)
        alloys = alloys + math.max(0, army.economy:GetResourceIncome("alloys") - ownA)
        energy = energy + math.max(0, army.economy:GetResourceIncome("energy") - ownE)
    end

    local allied, enemy = false, false
    for _, phantom in ipairs(live) do
        for _, innocent in ipairs(liveInnocents) do
            if phantom:IsAlly(innocent) then allied = true else enemy = true end
        end
    end
    local share = 0
    if allied and enemy then share = row.mix
    elseif allied then share = row.ally
    elseif enemy then share = row.enemy end
    share = share * multiplier

    for _, phantom in ipairs(live) do
        local own = share
        if allied then
            for _, other in ipairs(live) do
                if other ~= phantom and not phantom:IsAlly(other) then
                    own = own * row.war
                    break
                end
            end
        end
        paid[phantom] = true
        SetBonusIncome(phantom, alloys * own, energy * own)
        ShowBonus(phantom, alloys * own * Events.TicksPerSecond, energy * own * Events.TicksPerSecond, own * 100)
    end

    local paladinShare = share * Options.paladinBonus / 100
    for _, paladin in ipairs(Living(paladins)) do
        if P[paladin].marked then
            ShowBonus(paladin, 0, 0, 0)
        else
            paid[paladin] = true
            SetBonusIncome(paladin, alloys * paladinShare, energy * paladinShare)
            ShowBonus(paladin, alloys * paladinShare * Events.TicksPerSecond,
                energy * paladinShare * Events.TicksPerSecond, paladinShare * 100)
        end
    end
    StopTheRest()
end

---Phantom war: a phantom gets back a share of the cost of each enemy unit
---it kills (FA: of the mass and energy value it destroys).
local function OnUnitKilled(victim, info)
    if phase ~= "war" then return end
    local killer = info and info.army
    local victimArmy = victim and victim.army
    if not killer or not victimArmy or killer == victimArmy then return end
    if not Alive(killer) or P[killer].role ~= "phantom" or not killer:IsEnemy(victimArmy) then return end
    local row = Row(#Living(phantoms))
    local cost = victim.tp and victim.tp.economy and victim.tp.economy.cost
    if not row or not cost then return end
    local share = row.vampire * Options.bonus / 100
    local alloys, energy = (cost.alloys or 0) * share, (cost.energy or 0) * share
    Give(killer, alloys, energy)
    local p = P[killer]
    p.vampire = p.vampire or { alloys = 0, energy = 0 }
    p.vampire.alloys = p.vampire.alloys + alloys
    p.vampire.energy = p.vampire.energy + energy
    p.vampire.percent = share * 100
end

---Before the assignment, for the balancer: alloys spent, plus energy at
---B.EnergyPerAlloy (FA: mass and energy spent).
local function TallySpending()
    for _, army in ipairs(players) do
        if Alive(army) and army.economy then
            local p = P[army]
            p.score = p.score + army.economy:GetResourceOutcome("alloys")
                + army.economy:GetResourceOutcome("energy") / B.EnergyPerAlloy
        end
    end
end

-- ============================================================
-- Paladin marks
-- ============================================================

local function MarkCost()
    local innocentCount, paladinCount = #innocents, #paladins
    -- The chance a random innocent is a paladin, as FA worked it out.
    local p = paladinCount / (innocentCount + paladinCount)
    if p <= 0 then p = 1 / (innocentCount + 1) end
    local minutes = math.max(0, (Events.GameTime() - declareAt) / 60)
    return math.floor((B.MarkBaseCost / p + B.MarkMaxCost * math.atan(B.MarkSlope * minutes)) * p)
end

---Every player but those in the lists given.
local function Everyone(...)
    local skip = {}
    for _, list in ipairs({ ... }) do
        for _, army in ipairs(list) do skip[army] = true end
    end
    local out = {}
    for _, army in ipairs(players) do
        if not skip[army] then out[#out + 1] = army end
    end
    return out
end

local function ResolveMark(phantom, target)
    if not Alive(phantom) then return end
    local t = P[target]
    Story("mark", { by = phantom.id, target = target.id, hit = t.role == "paladin" and not t.marked })
    if t.role == "paladin" and not t.marked then
        t.marked = true
        for _, other in ipairs(phantoms) do
            known[other] = known[other] or {}
            known[other][target] = "paladin"
        end
        Alert(target, "You have been marked by a phantom", "You no longer get a paladin bonus.", Colours.phantom)
        Alert(phantoms, Name(target) .. " was a paladin, and is marked", "Victory is a step closer.", Colours.phantom)
        Alert(Everyone(phantoms, { target }), "A paladin has been marked", "They no longer get a bonus.", Colours.paladin)
    elseif t.role == "paladin" then
        Alert(phantom, "The mark has no effect", "That paladin was already marked.", Colours.phantom)
        Alert(Everyone({ phantom }), "A paladin mark has been used", "The paladin it hit was already marked.", Colours.neutral)
    else
        Alert(phantom, "The mark has no effect", Name(target) .. " isn't a paladin.", Colours.phantom)
        Alert(Everyone({ phantom }), "A paladin mark has been used", "The player it hit is unaffected.", Colours.neutral)
    end
end

---Every tick: a mark is paid from stored alloys as they come in.
local function TakePayments()
    for army, pay in pairs(paying) do
        if not Alive(army) then
            paying[army] = nil
        else
            pay.left = pay.left - army.economy:TakeResources("alloys", pay.left)
            if pay.left <= 0.5 then
                paying[army] = nil
                ResolveMark(army, pay.target)
            end
        end
    end
end

-- ============================================================
-- Winning and losing
-- ============================================================

local function RevealAll()
    for _, army in ipairs(players) do
        if P[army].role then public[army] = P[army].role end
    end
end

local function EndMatch(winners, title, text)
    phase = "over"
    result = title
    paying = {}
    nextRevealAt = nil
    RevealAll()
    local ids = {}
    for _, army in ipairs(winners) do ids[#ids + 1] = army.id end
    Story("over", { result = title, winners = ids })
    local everyone = {}
    for _, army in ipairs(players) do
        everyone[#everyone + 1] = { id = army.id, name = Name(army), role = P[army].role, marked = P[army].marked or nil }
    end
    local ok, err = pcall(SendToAllClients, { players = everyone, events = story, assignAt = declareAt }, StoryName)
    if not ok then Events.Report("Phantom-X: story", err) end
    for _, army in ipairs(winners) do
        decided[army.id] = WinCondition.Won
        SessionCommands.ExecuteClientFunction.Send("WinConditionUpdate", { armyID = army.id, condition = WinCondition.Won })
    end
    Alert(nil, title, text, Colours.neutral)
end

local function StartPhantomWar()
    phase = "war"
    Story("war")
    nextRevealAt = nil
    local live = Living(phantoms)
    for a = 1, #live do
        P[live[a]].offers = {}
        for b = a + 1, #live do Enemy(live[a], live[b]) end
    end
    if not vampireHooked and type(Events.OnUnitKilled) == "function" then
        vampireHooked = true
        Events.OnUnitKilled(OnUnitKilled)
    end
    Alert(nil, "Phantom war", "Only phantoms are left. Vampire rules now in effect.", Colours.phantom)
end

local function OnDeath(army)
    paying[army] = nil
    local p = P[army]
    Story("dead", { id = army.id, role = p.role })
    if phase == "pending" or not p.role then
        Alert(nil, Name(army) .. " is out", nil, Colours.neutral)
        return
    end
    if not Options.deathReveal then
        Alert(nil, Name(army) .. " is out", nil, Colours.neutral)
        return
    end
    public[army] = p.role
    if p.role == "phantom" then
        local left = #Living(phantoms)
        Alert(nil, "Phantom assassinated", Name(army) .. " was a phantom. "
            .. (left == 0 and "Every phantom is dead." or (Plural(left, "phantom remains", "phantoms remain") .. ".")),
            Colours.phantom)
    else
        local left = #Living(innocents)
        Alert(nil, "Innocent assassinated", Name(army) .. " was " .. (p.role == "paladin" and "a paladin" or "innocent") .. ". "
            .. (left == 0 and "Every innocent is dead." or (Plural(left, "innocent remains", "innocents remain") .. ".")),
            Colours.innocent)
    end
end

local function Check()
    if phase == "over" then return end
    for _, army in ipairs(players) do
        local p = P[army]
        if not p.dead and not army:IsAlive() then
            p.dead = true
            OnDeath(army)
        end
    end

    if phase == "pending" then
        local living = Living(players)
        if #players >= 2 and #living <= 1 then EndMatch(living, "Last one standing", nil) end
        return
    end

    local livePhantoms, liveInnocents = Living(phantoms), Living(innocents)
    if #livePhantoms == 0 and #liveInnocents > 0 then
        EndMatch(liveInnocents, "Innocents win", "Every phantom is dead.")
    elseif #liveInnocents == 0 and #livePhantoms == 1 then
        EndMatch(livePhantoms, "Phantom victory", Name(livePhantoms[1]) .. " is the last one standing.")
    elseif #liveInnocents == 0 and #livePhantoms == 0 then
        EndMatch({}, "Nobody wins", nil)
    elseif #liveInnocents == 0 and phase ~= "war" then
        StartPhantomWar()
    end
end

---The game calls an army the winner once every enemy is dead. Here nearly
---everyone starts allied and alliances come and go, so this mod decides:
---an army has lost when it's dead, has won when EndMatch says, and is
---undecided until then.
local function TakeOverWinCondition()
    local HostArmy = Import("host/systems/army.lua").Army
    HostArmy.ComputeWinCondition = function(self)
        if not self:IsAlive() then return WinCondition.Lost end
        return decided[self.id] or WinCondition.Undecided
    end
end

-- ============================================================
-- Reveals
-- ============================================================

local function RevealGap(i)
    if i == 1 then return Options.reveal1 > 0 and Options.reveal1 * 60 or nil end
    local value = Options["reveal" .. i]
    if value == "same" then return B.RevealGap end
    local minutes = tonumber(value)
    return minutes and minutes * 60 or nil
end

---Whether viewer sees reveals, by the revealTo option.
local function SeesReveals(viewer)
    local role = P[viewer].role
    local to = Options.revealTo
    return to == "everyone" or (to == "phantoms" and role == "phantom") or (to == "paladins" and role == "paladin")
        or (to == "both" and (role == "phantom" or role == "paladin"))
end

local function Reveal(army)
    if not Alive(army) or phase == "over" then return false end
    local role = P[army].role
    local viewers = {}
    for _, viewer in ipairs(players) do
        if viewer ~= army and SeesReveals(viewer) then
            viewers[#viewers + 1] = viewer
            known[viewer] = known[viewer] or {}
            known[viewer][army] = role
        end
    end
    if Options.revealTo == "everyone" then public[army] = role end
    Story("reveal", { id = army.id, role = role, to = Options.revealTo })
    if #viewers > 0 then
        Alert(viewers, Name(army) .. " is a " .. role .. "!", nil, Colours[role])
    end
    return true
end

---The next live player of the list not yet revealed.
local function NextHidden(list)
    for _, army in ipairs(list) do
        if Alive(army) and not public[army] and not P[army].revealed then return army end
    end
    return nil
end

local function RevealsPhantoms() return Options.revealWho == "phantoms" or Options.revealWho == "both" end
local function RevealsPaladins() return Options.revealWho == "paladins" or Options.revealWho == "both" end

local function Hidden()
    local n = 0
    if RevealsPhantoms() then
        for _, army in ipairs(Living(phantoms)) do if not P[army].revealed then n = n + 1 end end
    end
    if RevealsPaladins() then
        for _, army in ipairs(Living(paladins)) do if not P[army].revealed then n = n + 1 end end
    end
    return n
end

local function RevealRound()
    if phase ~= "playing" then return end
    if RevealsPhantoms() then
        local army = NextHidden(phantoms)
        if army and Reveal(army) then P[army].revealed = true end
    end
    if RevealsPaladins() then
        local army = NextHidden(paladins)
        if army then
            P[army].revealed = true
            Events.After(RevealsPhantoms() and B.PaladinRevealDelay or 0, function()
                Guard("Phantom-X: reveal", Reveal, army)
            end)
        end
    end
    dirty = true
end

local function DoneRevealing(anyPlanned)
    nextRevealAt = nil
    if phase ~= "playing" then return end
    local hidden = Hidden()
    if not anyPlanned then
        Alert(nil, "Nothing will be revealed", "Trust your instincts.", Colours.neutral)
    elseif hidden == 0 then
        Alert(nil, "The players have been revealed", "Let the games begin.", Colours.neutral)
    else
        Alert(nil, "Nothing more will be revealed", Plural(hidden, "more player lurks", "more players lurk") .. " in the shadows.",
            Colours.neutral)
    end
end

local function PlanReveals()
    local at = Events.GameTime()
    local planned = {}
    for i = 1, 3 do
        local gap = RevealGap(i)
        if not gap then break end
        at = at + gap
        planned[#planned + 1] = at
    end
    local now = Events.GameTime()
    for i, when in ipairs(planned) do
        Events.After(when - now, function()
            Guard("Phantom-X: reveal", RevealRound)
            nextRevealAt = planned[i + 1]
        end)
    end
    nextRevealAt = planned[1]
    local last = planned[#planned] or now
    Events.After(last - now + B.RevealGap, function() Guard("Phantom-X: reveals", DoneRevealing, #planned > 0) end)
end

-- ============================================================
-- The assignment
-- ============================================================

local function PhantomCount(pool)
    local count
    if Options.phantoms == "vote" then
        local votes = { 0, 0, 0 }
        for _, army in ipairs(players) do
            local v = P[army].vote
            if v then votes[v] = votes[v] + 1 end
        end
        -- The most votes wins; a tie goes to fewer phantoms.
        local best, most = nil, 0
        for n = 1, 3 do
            if votes[n] > most then best, most = n, votes[n] end
        end
        count = best or math.ceil(#pool * B.PhantomShare)
    else
        count = tonumber(Options.phantoms) or 1
    end
    return math.max(1, math.min(count, #pool - 1))
end

---Draws count players, each with one ticket, volunteers with more.
local function DrawPhantoms(pool, count)
    local chosen = {}
    for _ = 1, count do
        local tickets = {}
        for _, army in ipairs(pool) do
            if not chosen[army] then
                local n = 1
                if Options.picking == "volunteer" and P[army].volunteered then n = n + B.VolunteerTokens end
                for _ = 1, n do tickets[#tickets + 1] = army end
            end
        end
        if #tickets == 0 then break end
        chosen[tickets[math.random(#tickets)]] = true
    end
    local out = {}
    for _, army in ipairs(pool) do if chosen[army] then out[#out + 1] = army end end
    return out
end

---For two phantoms (FA's autobalance): the first at random, the second the
---one that makes the two phantoms' spending, scaled by how outnumbered they
---are, closest to the innocents'.
local function BalancedPhantoms(pool)
    local first = pool[math.random(#pool)]
    local total = 0
    for _, army in ipairs(pool) do total = total + P[army].score end
    local scale = (#pool - 2) / 2
    local best, bestGap
    for _, army in ipairs(pool) do
        if army ~= first then
            local pair = P[first].score + P[army].score
            local rest = total - pair
            local gap = rest > 0 and math.abs(1 - pair * scale / rest) or math.huge
            if not bestGap or gap < bestGap then best, bestGap = army, gap end
        end
    end
    if bestGap == math.huge then return DrawPhantoms(pool, 2) end
    return { first, best }
end

local function MarksFor(phantomCount, paladinCount)
    local m = Options.marks
    if m == "none" then return 0 end
    if m == "perPaladin" then return paladinCount end
    if m == "perPhantom" then return phantomCount end
    if m == "twoPerPhantom" then return 2 * phantomCount end
    return tonumber(m) or 0
end

local function Assign()
    voteOpen, volunteerOpen = false, false
    local pool = Living(players)
    if #pool < 2 then
        phase = "playing"
        EndMatch(pool, "Last one standing", nil)
        return
    end

    local count = PhantomCount(pool)
    local chosen = (Options.picking == "balance" and count == 2) and BalancedPhantoms(pool) or DrawPhantoms(pool, count)
    local isPhantom = {}
    for _, army in ipairs(chosen) do isPhantom[army] = true end
    for _, army in ipairs(pool) do
        if isPhantom[army] then phantoms[#phantoms + 1] = army else innocents[#innocents + 1] = army end
    end

    local paladinCount = math.min(math.floor((PaladinRatio[Options.paladins] or 0) * #phantoms + 1e-9), #innocents)
    local candidates = {}
    for _, army in ipairs(innocents) do candidates[#candidates + 1] = army end
    for _ = 1, paladinCount do
        local army = table.remove(candidates, math.random(#candidates))
        paladins[#paladins + 1] = army
    end
    -- Phantoms in a random order, so the first one revealed isn't the lowest army.
    for i = #phantoms, 2, -1 do
        local j = math.random(i)
        phantoms[i], phantoms[j] = phantoms[j], phantoms[i]
    end

    local marks = MarksFor(#phantoms, #paladins)
    local each, extra = math.floor(marks / #phantoms), marks % #phantoms
    for i, army in ipairs(phantoms) do
        P[army].role = "phantom"
        P[army].marks = each + (i <= extra and 1 or 0)
        -- Never trade resources away: an ally's overflow would carry the bonus.
        army.economy:SetResourceSharing(false)
        army.economy:AddStorage(StorageSource, { alloys = B.PhantomStorage.alloys, energy = B.PhantomStorage.energy })
    end
    for _, army in ipairs(innocents) do P[army].role = "innocent" end
    for _, army in ipairs(paladins) do P[army].role = "paladin" end
    local roles = {}
    for _, army in ipairs(pool) do roles[#roles + 1] = { id = army.id, role = P[army].role } end
    Story("assign", { roles = roles })

    phase = "playing"
    for _, army in ipairs(phantoms) do
        Alert(army, "Designation: PHANTOM", "Kill everyone. Be the last one standing.", Colours.phantom)
    end
    for _, army in ipairs(innocents) do
        if P[army].role == "paladin" then
            Alert(army, "Designation: PALADIN", "Kill every phantom. You get a share of the phantom bonus.", Colours.paladin)
        else
            Alert(army, "Designation: INNOCENT", "Kill every phantom.", Colours.innocent)
        end
    end
    local text = Plural(#phantoms, "phantom walks", "phantoms walk") .. " among you"
    if #paladins > 0 then text = text .. ", and " .. Plural(#paladins, "paladin hunts", "paladins hunt") .. " them" end
    Alert(nil, "The phantoms have been chosen", text .. ".", Colours.neutral)

    PlanReveals()
end

local function OpenVote()
    if phase ~= "pending" or Options.phantoms ~= "vote" then return end
    voteOpen = true
    Alert(nil, "Vote: how many phantoms?", "Vote on the Phantom-X panel. A tie goes to fewer.", Colours.neutral)
end

local function OpenVolunteers()
    if phase ~= "pending" then return end
    if Options.picking == "volunteer" then
        volunteerOpen = true
        Alert(nil, "Volunteers wanted", "Want to be a phantom? Say so on the Phantom-X panel. It improves your odds.",
            Colours.neutral)
    elseif Options.picking == "balance" then
        Alert(nil, "Automatic team balancing is on", "With two phantoms, they're picked to balance the teams.", Colours.neutral)
    end
end

-- ============================================================
-- Requests from the panel
-- ============================================================

---army: the requesting player's, as the engine says which client sent it.
local function HandleRequest(army, data)
    if phase == "over" or type(data) ~= "table" then return end
    if not army or not Alive(army) then return end
    local p = P[army]
    local target = tonumber(data.target) and Armies[tonumber(data.target)]
    local validTarget = target and target ~= army and Alive(target)
    local op = data.op

    if op == "war" and validTarget and army:IsAlly(target) then
        Enemy(army, target)
        p.offers[target] = nil
        P[target].offers[army] = nil
        Alert(nil, Name(army) .. " has broken the alliance with " .. Name(target), nil, Colours.phantom)
        -- Who is allied with whom sets the phantoms' share: not left to the
        -- next once-a-second bonus.
        if phase == "playing" then GiveBonus() end

    elseif op == "ally" and validTarget and not army:IsAlly(target) then
        if phase == "war" then
            Alert(army, "No alliances in a phantom war", nil, Colours.phantom)
        elseif p.offers[target] then
            p.offers[target] = nil
            Alert(army, "Alliance offer to " .. Name(target) .. " withdrawn", nil, Colours.neutral)
        elseif P[target].offers[army] or not ClientOf(target) then
            -- Both want it (an AI always does).
            P[target].offers[army] = nil
            Ally(army, target)
            Alert(nil, Name(army) .. " and " .. Name(target) .. " are allies again", nil, Colours.innocent)
            if phase == "playing" then GiveBonus() end
        else
            p.offers[target] = true
            Alert(target, Name(army) .. " offers an alliance", "Accept it on the Phantom-X panel.", Colours.innocent)
            Alert(army, "Alliance offered to " .. Name(target), "They have to offer one back.", Colours.neutral)
        end

    elseif op == "mark" and validTarget and p.role == "phantom" and (p.marks or 0) > 0 and not paying[army]
        and (phase == "playing" or phase == "war") then
        local cost = MarkCost()
        p.marks = p.marks - 1
        paying[army] = { left = cost, cost = cost, target = target }
        Alert(army, "Marking " .. Name(target), "It lands once " .. cost .. " alloys are paid, taken from your storage.",
            Colours.phantom)

    elseif op == "vote" and voteOpen and not p.vote then
        local n = tonumber(data.value)
        if n == 1 or n == 2 or n == 3 then
            p.vote = n
            Alert(nil, Name(army) .. " voted for " .. Plural(n, "phantom", "phantoms"), nil, Colours.neutral)
        end

    elseif op == "volunteer" and volunteerOpen and p.volunteered == nil then
        p.volunteered = data.value == true
        Alert(army, p.volunteered and "You volunteered to be a phantom" or "You didn't volunteer", nil, Colours.neutral)
    end
    dirty = true
end

-- Requests come in as the game's client-to-host function calls, with the
-- client they came from (the Mod API keeps that, which the game's own
-- handling drops). Before the match has started they are ignored.
Events.OnRequest(RequestName, function(army, data)
    if started then Guard("Phantom-X: request", HandleRequest, army, data) end
end)

-- ============================================================
-- What each player is told
-- ============================================================

local function RoleSeenBy(viewer, army)
    local role = P[army].role
    if not role then return nil end
    if viewer == nil or viewer == army or phase == "over" then return role end
    if public[army] then return public[army] end
    return known[viewer] and known[viewer][army] or nil
end

local function Rounded(bonus)
    if not bonus then return nil end
    return { alloys = math.floor(bonus.alloys * 10 + 0.5) / 10, energy = math.floor(bonus.energy + 0.5),
             percent = math.floor(bonus.percent * 10 + 0.5) / 10 }
end

local function NoticesFor(viewer)
    local list = {}
    for _, n in ipairs(alerts.all) do list[#list + 1] = n end
    if viewer and alerts[viewer] then
        for _, n in ipairs(alerts[viewer]) do list[#list + 1] = n end
    end
    table.sort(list, function(a, b) return a.id < b.id end)
    while #list > 4 do table.remove(list, 1) end
    return list
end

---viewer: the player's army, or nil for an observer (who sees everything).
local function StateFor(viewer)
    local p = viewer and P[viewer]
    local now = Events.GameTime()
    local s = {
        phase = phase,
        role = p and (p.role or "pending") or "observer",
        dead = p and p.dead or false,
        marked = p and p.marked or false,
        result = result,
        alerts = NoticesFor(viewer),
        players = {},
    }

    if phase == "pending" then
        s.timer = "Phantoms are chosen in " .. Clock(declareAt - now)
    elseif nextRevealAt and phase == "playing" and Hidden() > 0 then
        s.timer = "Next reveal in " .. Clock(nextRevealAt - now)
    end

    if p and not p.dead and (phase == "playing" or phase == "war") then
        if p.role == "phantom" or p.role == "paladin" then s.bonus = Rounded(phase == "war" and p.vampire or p.bonus) end
        if p.role == "phantom" then
            s.marks = p.marks or 0
            s.markCost = MarkCost()
            local pay = paying[viewer]
            if pay then
                s.paying = { left = math.ceil(pay.left), cost = pay.cost, target = pay.target.id, name = Name(pay.target) }
            end
        end
    end

    if phase ~= "pending" and (Options.deathReveal or not p) then
        s.counts = "Phantoms " .. #Living(phantoms) .. " of " .. #phantoms .. "  |  Innocents " .. #Living(innocents) .. " of " .. #innocents
    end

    if voteOpen and p and not p.dead then s.vote = p.vote or 0 end
    if volunteerOpen and p and not p.dead then
        s.volunteer = p.volunteered == nil and "ask" or (p.volunteered and "yes" or "no")
    end

    local canAct = p and not p.dead and phase ~= "over"
    for _, army in ipairs(players) do
        local other = P[army]
        local entry = { id = army.id, name = Name(army), alive = not other.dead, me = army == viewer,
                        role = RoleSeenBy(viewer, army) }
        if viewer and army ~= viewer then
            entry.ally = viewer:IsAlly(army)
            entry.offered = p.offers[army] or false
            entry.wants = other.offers[viewer] or false
            entry.canWar = canAct and not other.dead and entry.ally
            entry.canAlly = canAct and not other.dead and not entry.ally and phase ~= "war"
            -- Every phantom is told who a mark hit, so a marked paladin loses the button.
            entry.canMark = canAct and not other.dead and p.role == "phantom" and (p.marks or 0) > 0 and not paying[viewer]
                and not other.marked
                and (phase == "playing" or phase == "war") and not (known[viewer] and known[viewer][army] == "phantom")
        end
        s.players[#s.players + 1] = entry
    end
    return s
end

---A client's army, if it plays; observers see as nil.
local function StateOf(army)
    return StateFor(army and P[army] and army or nil)
end

---Each client is sent its state when it has changed.
local function Broadcast()
    dirty = false
    Events.SendToEachClient(StateName, StateOf)
end

-- ============================================================
-- Setup
-- ============================================================

local function Start()
    local seed = 0
    if os and os.time then seed = seed + os.time() end
    if os and os.clock then seed = seed + math.floor(os.clock() * 1000) end
    math.randomseed(seed)
    for _ = 1, 4 do math.random() end

    players = Events.Players()
    for _, army in ipairs(players) do P[army] = { offers = {}, score = 0 } end

    -- Everyone allied, whatever the lobby's teams, and nobody's overflow
    -- going to anyone else.
    for a = 1, #players do
        if players[a].economy then players[a].economy:SetResourceSharing(false) end
        for b = a + 1, #players do Ally(players[a], players[b]) end
    end
    TakeOverWinCondition()
    started = true

    Events.After(math.max(0, declareAt - B.VoteLead), function() Guard("Phantom-X: vote", OpenVote) end)
    Events.After(math.max(0, declareAt - B.VolunteerLead), function() Guard("Phantom-X: volunteers", OpenVolunteers) end)
    Events.After(declareAt, function()
        Guard("Phantom-X: assignment", Assign)
        if phase == "playing" then Guard("Phantom-X: bonus", GiveBonus) end
    end)
    Events.Every(1, function()
        -- The bonus is paid every tick at the rate worked out here. Worked
        -- out before the check, so the second a phantom war starts it was
        -- last set with the dead innocents' income, as when it was worked
        -- out every tick.
        if phase == "playing" then Guard("Phantom-X: bonus", GiveBonus) end
        Guard("Phantom-X: check", Check)
        dirty = true
    end)
    Events.OnTick(function()
        if phase == "pending" then Guard("Phantom-X: spending", TallySpending) end
        if phase == "playing" or phase == "war" then Guard("Phantom-X: marks", TakePayments) end
        if dirty then Guard("Phantom-X: state", Broadcast) end
    end)

    Alert(nil, "Phantom-X", "Everyone is allied. In " .. Clock(declareAt) .. " some of you become phantoms.", Colours.neutral)
    -- Warn: Log is debug-level and never reaches Player.log.
    Warn("Phantom-X: " .. #players .. " players, phantoms chosen at " .. declareAt .. " s")
end

Events.OnMatchStart(function()
    Guard("Phantom-X: setup", Start)
end)
