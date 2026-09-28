-- Zone Control, host side: the match's rules.
--
-- A port of johnie102's Zone Control for Forged Alliance (the 8 player V2).
-- The map is a grid of zones, each guarded by a point defence turret. There
-- are no commanders and nothing to build: every zone whose turret you own
-- sends you units. Destroy a zone's turret, then stand in it with 5 or more
-- units, more than anyone else, and it gets a turret of yours. Its owner takes
-- it back with 10 units and no enemies there. Lose every turret and you're out.
--
-- Kills earn money and levels. Levels bring better units and, from level 4,
-- heroes. Money buys attack and defence upgrades and kamikazes: send your
-- upgrader (in your base, beside your home zone) onto a shop to buy.
-- Kills need the Mod API's Events.OnUnitKilled; without it the zones still
-- play, with no money or levels.
--
-- Zone maths is done in the original map's coordinates, 512 x 512. The
-- converter centres that map in a terrain twice its size and runs z the
-- other way; ToWorld and ToMap cross between the two. Every number is in
-- balance.lua.

local Events = Import("modapi/events.lua").Events
local Options = Import("modoptions/sanctuarymods.zonecontrol.lua").Options
local B = Import("zonecontrol/balance.lua")
local GameUtils = Import("common/gameUtils.lua")
local HostOrderManager = Import("host/managers/orders/hostOrderManager.lua")
local OrderTasks = Import("common/commonOrders.lua").OrderTasks
local SessionCommands = Import("common/commands/definitions/session.lua")
local WinCondition = Import("common/winCondition.lua").WinCondition

local Map = B.Map

-- Kill credit comes from the Mod API. Without it there's no money or levels.
local economy = type(Events.OnUnitKilled) == "function"

local zones = {}            -- zones[i][j]
local playableZones = {}    -- the played ones, in grid order
local players = {}          -- every player army, AI included, by ascending id
local data = {}             -- [army] = what the rules know about that player
local neutral               -- the army holding every zone nobody has taken
local defeated = {}         -- [army id] = true
local contested = false     -- more than one team at the start, so someone can win
local ended = false
local timers = {}
local offset = 0            -- where the original map sits in the terrain
local spawnBeat = 0
local named = {}            -- [unit] = true for units with a label to keep drawn
local shopOf = {}           -- [shop unit] = { army, kind }
local lastStatus = {}       -- [client id] = the banner it has

local function Announce(text)
    SessionCommands.AddLog.Send(text)
end

-- The game's Lua warnings reach no log in this build, so problems are shown
-- in the match's own message log, once each, where a player can see them.
local reported = {}
local function Report(what, err)
    if reported[what] then return end
    reported[what] = true
    local text = tostring(err or "")
    text = string.gsub(text, "stack traceback:", "")
    text = string.gsub(text, "%s*\n%s*", " < ")
    if #text > 400 then text = string.sub(text, 1, 400) .. "..." end
    Warn("Zone Control: " .. what .. ": " .. text)
    Announce("Zone Control problem: " .. what .. ": " .. text)
end

---Runs fn, reporting rather than losing an error.
local function Guarded(what, fn, ...)
    local ok, err = xpcall(fn, debug.traceback, ...)
    if not ok then Report(what, err) end
    return ok
end

---To one army's player only, when it has one (an AI hasn't).
local function Tell(army, text)
    local player = Import("common/lobby.lua").ArmyToPlayer[army.id]
    if player and player.clientID then
        pcall(SessionCommands.AddLog.SendTo, player.clientID, text)
    end
end

local function NormalizeName(name)
    return (string.gsub(string.lower(name or ""), "[^%a%d]", ""))
end

---The converted map keeps "Zone Control" in its name, however the words are separated.
local function IsZoneControlMap()
    local info = GameInfo and GameInfo.MapInfo
    if not info then return false end
    local name = NormalizeName(info.dataName) .. " " .. NormalizeName(info.name)
    return string.find(name, "zonecontrol", 1, true) ~= nil
end

-- ============================================================
-- Coordinates
-- ============================================================

local function ToWorld(x, z)
    -- y = 0 lets CreateUnit and the orders snap to the ground.
    return EngineClasses.float3(x + offset, 0, Map.size - z + offset)
end

local function ToMap(position)
    return position.x - offset, Map.size - (position.z - offset)
end

local function ZoneAt(position)
    local x, z = ToMap(position)
    local i = math.floor((x - Map.offsetX) / Map.zoneSize) + 1
    local j = math.floor((z - Map.offsetZ) / Map.zoneSize) + 1
    return zones[i] and zones[i][j]
end

local function ZoneCentre(zone, dx, dz)
    local x = (zone.i - 0.5) * Map.zoneSize + Map.offsetX + (dx or 0)
    local z = (zone.j - 0.5) * Map.zoneSize + Map.offsetZ + (dz or 0)
    return ToWorld(x, z)
end

---A spot in a base cell, `at` metres from its corner.
local function BaseSpot(base, at)
    local x = (base[1] - 1) * Map.zoneSize + Map.offsetX + at[1]
    local z = (base[2] - 1) * Map.zoneSize + Map.offsetZ + at[2]
    return ToWorld(x, z)
end

local function IsBase(i, j)
    for _, p in ipairs(Map.players) do
        if p.base[1] == i and p.base[2] == j then return true end
    end
    return false
end

local function IsPlayable(i, j)
    if math.abs(i - Map.centre) + math.abs(j - Map.centre) > Map.radius then return false end
    return not IsBase(i, j)
end

local function Near(a, b, reach)
    return math.abs(a.x - b.x) <= reach and math.abs(a.z - b.z) <= reach
end

-- ============================================================
-- Armies
-- ============================================================

local function IsPlayerArmy(army)
    local isEmptySlot = army.lobbyOptions and army.lobbyOptions.isEmptySlot
    return not army.civilian and not isEmptySlot
end

local function ArmyName(army)
    return (army.lobbyOptions and army.lobbyOptions.playerName) or army.name
end

local function Friendly(a, b)
    return a == b or a:IsAlly(b)
end

---The neutral army is enemy to everyone, as the original's ARMY_9 is. An
---empty start slot would do, but a lobby with every slot filled has none.
local function CreateNeutralArmy()
    local ids = {}
    for id in pairs(Armies) do ids[#ids + 1] = id end
    table.sort(ids)

    -- CreateArmy's own pick of a free id misreads the map's slots, so pick it here (as survival does).
    local nextId = 1
    while Armies[nextId] do nextId = nextId + 1 end

    local army = CreateArmy(nextId, "ZoneControl_Neutral", 3, false)
    army.lobbyOptions = {
        armyID = army.id,
        playerName = "Neutral",
        team = 1000 + army.id,
        faction = 3,
        aiSettings = false,
        isEmptySlot = true,
    }

    for _, id in ipairs(ids) do
        local other = Armies[id]
        if not other.civilian then
            army:SetEnemy(other)
            other:SetEnemy(army)
        end
    end

    return army
end

-- ============================================================
-- Units
-- ============================================================

-- A name drawn under a unit shows only on the screen that draws it, and only
-- while it's drawn every tick. So the host keeps the labels and sends them
-- out when they change; each player's client draws them (client.lua).
local function Label(unit, text)
    unit.zcName = text
    named[unit] = true
end

local lastLabels
local function BroadcastLabels()
    local list = {}
    for unit in pairs(named) do
        if unit.dead or unit.deleted then
            named[unit] = nil
        else
            list[#list + 1] = { index = unit.id.index, text = unit.zcName }
        end
    end
    table.sort(list, function(a, b) return a.index < b.index end)
    local parts = {}
    for _, label in ipairs(list) do parts[#parts + 1] = label.index .. "=" .. label.text end
    local key = table.concat(parts, "\n")
    if key ~= lastLabels then
        lastLabels = key
        SendToAllClients({ labels = list }, "ZoneControlLabels")
    end
end

local function Weapons(unit)
    return unit.weapons or {}
end

---Damage a second of a unit's own weapons, as its template has them.
local function TemplateDps(unit)
    local dps = 0
    for _, weapon in ipairs(Weapons(unit)) do
        local tp = weapon.zcTemplate or weapon.tp
        if tp and tp.damage and tp.category ~= "DeathExplosion" then
            dps = dps + tp.damage * (tp.muzzleSalvoSize or 1) / math.max(tp.reloadTime or 1, 0.1)
        end
    end
    return dps
end

---Sets a unit's weapons to `scale` times their template's damage, and
---`reload` times its reload. Each weapon gets its own copy of its template
---(the template is shared by every unit of the type); beams keep their damage
---on the muzzle.
local function SetWeaponScale(unit, scale, reload)
    for _, weapon in ipairs(Weapons(unit)) do
        if weapon.tp then
            if not weapon.zcTemplate then
                weapon.zcTemplate = weapon.tp
                weapon.tp = table.shallowCopy(weapon.tp)
            end
            local base = weapon.zcTemplate
            if base.damage then weapon.tp.damage = base.damage * scale end
            if base.reloadTime then weapon.tp.reloadTime = base.reloadTime * (reload or 1) end
            for _, muzzle in ipairs(weapon.muzzles or {}) do
                if muzzle.beam and base.damage then muzzle.beam.damage = base.damage * scale end
            end
        end
    end
end

local function DisableWeapons(unit)
    for _, weapon in ipairs(Weapons(unit)) do weapon:SetEnabled(false) end
end

local function SetSpeed(unit, fn)
    local ok, err, speed = pcall(Engine.GetMovementMaxSpeed, unit.id)
    if ok and type(speed) == "number" then
        pcall(Engine.SetMovementMaxSpeed, unit.id, fn(speed))
    end
end

---Can't be hurt, killed or targeted; not counted in zones; leaves no wreck.
local function Protect(unit, keepWeapons)
    unit.zcIgnore = true
    unit.spawnsWreck = false
    unit:SetCanTakeDamage(false)
    unit:SetCanBeKilled(false)
    unit:SetTargetable(false)
    if not keepWeapons then DisableWeapons(unit) end
end

local function Create(army, tpId, position)
    local ok, unit = xpcall(CreateUnit, debug.traceback, army.id, tpId, position)
    if not ok or not unit then
        Report("couldn't create " .. tpId, unit)
        return nil
    end
    unit.spawnsWreck = false
    return unit
end

---Moves units once they exist: orders given on the tick a unit is created are ignored.
local function MoveLater(orders)
    if #orders == 0 then return end
    NewThread(function()
        WaitTicks(1)
        for _, order in ipairs(orders) do
            if not order.unit.dead then
                HostOrderManager.HostIssueOrder(OrderTasks.MOVE, { order.unit }, false, order.position, nil)
            end
        end
    end)
end

-- ============================================================
-- Turrets and zones
-- ============================================================

local function TurretType(army)
    return army == neutral and B.Turrets.neutral or B.Turrets.player
end

---Sets a player's turret to their defence level.
local function ApplyDefence(turret, d, heal)
    turret:SetMaxHealth(B.Turrets.health(d))
    if heal then turret:SetHealth(B.Turrets.health(d)) end
    turret:SetHealthRegen(B.Turrets.regen(d))
    SetWeaponScale(turret, B.Turrets.damage(d))
end

---Puts a turret of army's in the zone and makes it the zone's owner.
local function SpawnTurret(zone, army)
    local unit = Create(army, TurretType(army), ZoneCentre(zone))
    if not unit then return nil end
    unit.zcTurret = true

    if army == neutral then
        unit:SetMaxHealth(B.Turrets.neutralHealth)
        unit:SetHealth(B.Turrets.neutralHealth)
        SetWeaponScale(unit, B.Turrets.neutralDamage)
    else
        ApplyDefence(unit, data[army].defence, true)
    end

    -- The zone keeps its owner until someone takes it; only the turret goes.
    unit:AddCallback(function(destroyed)
        if zone.turret == destroyed then zone.turret = nil end
        if Events.GameTime() < B.GracePeriod then
            Report("a turret went at " .. Events.GameTime() .. "s (zone " .. zone.i .. "," .. zone.j .. ")",
                debug.traceback("health " .. tostring(destroyed:GetHealth()), 2))
        end
    end, "OnDestroyed")

    zone.owner = army
    zone.turret = unit
    return unit
end

---How many zones each army holds: the zones with its turret standing.
local function ZoneCounts()
    local counts = {}
    for _, zone in ipairs(playableZones) do
        if zone.turret then
            counts[zone.owner] = (counts[zone.owner] or 0) + 1
        end
    end
    return counts
end

---The zone a player's heroes and kamikazes appear in: home, or the nearest they still hold.
local function RallyZone(army)
    local home = data[army].home
    if home and home.owner == army and home.turret then return home end
    local best, bestDistance
    for _, zone in ipairs(playableZones) do
        if zone.owner == army and zone.turret then
            local distance = home and ((zone.i - home.i) ^ 2 + (zone.j - home.j) ^ 2) or 0
            if not bestDistance or distance < bestDistance then
                best, bestDistance = zone, distance
            end
        end
    end
    return best
end

---Who a zone without a turret goes to, if anyone, from the units in it.
local function NewOwner(zone, units)
    local owner = zone.owner
    local friendly, hostile = 0, 0
    local byArmy = {}
    for _, unit in ipairs(units) do
        local army = unit.army
        byArmy[army] = (byArmy[army] or 0) + 1
        if Friendly(owner, army) then
            friendly = friendly + 1
        else
            hostile = hostile + 1
        end
    end

    if friendly >= B.RetakeCount and hostile == 0 then
        return owner
    end

    if friendly <= B.CaptureCount then
        local best, bestCount, draw = nil, 0, false
        for army, count in pairs(byArmy) do
            if count > bestCount then
                best, bestCount, draw = army, count, false
            elseif count == bestCount then
                draw = true
            end
        end
        if best and bestCount >= B.CaptureCount and not draw then
            return best
        end
    end

    return nil
end

local function CheckZones()
    local inZone = {}
    for _, unit in ipairs(GetListOfUnits(nil, Tags.MOBILE)) do
        if not unit.dead and not unit.zcIgnore and not defeated[unit.army.id] then
            local zone = ZoneAt(unit:GetPosition())
            if zone and zone.playable and not zone.turret then
                local list = inZone[zone]
                if not list then
                    list = {}
                    inZone[zone] = list
                end
                list[#list + 1] = unit
            end
        end
    end

    for _, zone in ipairs(playableZones) do
        if not zone.turret then
            local owner = NewOwner(zone, inZone[zone] or {})
            if owner then SpawnTurret(zone, owner) end
        end
    end
end

-- ============================================================
-- Spawning
-- ============================================================

---A spawned unit as the player's level and attack make it.
local function Equip(unit, army)
    local p = data[army]
    local level = B.Levels[p.level]
    unit:SetMaxHealth(B.Units.baseHealth * level.health + B.Units.healthPerAttack * p.attack)
    unit:SetHealth(B.Units.baseHealth * level.health + B.Units.healthPerAttack * p.attack)
    local own = TemplateDps(unit)
    if own > 0 then
        SetWeaponScale(unit, level.dps * (1 + B.Units.damagePerAttack * p.attack) / own)
    end
end

---One unit from every zone a player holds, as often as their level allows, up to their cap.
local function SpawnUnits()
    spawnBeat = spawnBeat + 1
    local orders = {}
    local unitCounts = {}

    for _, zone in ipairs(playableZones) do
        local army = zone.owner
        if zone.turret and army ~= neutral and not defeated[army.id] then
            local level = B.Levels[data[army].level]
            local count = unitCounts[army] or #army:GetListOfUnits(Tags.MOBILE, true)
            if count < Options.unitCap and spawnBeat % level.every == 0 then
                local sx = math.random(0, 1) * 2 - 1
                local sz = math.random(0, 1) * 2 - 1
                local unit = Create(army, level.unit, ZoneCentre(zone, sx * B.SpawnOffset, sz * B.SpawnOffset))
                if unit then
                    Equip(unit, army)
                    count = count + 1
                    orders[#orders + 1] = {
                        unit = unit,
                        position = ZoneCentre(zone,
                            sx * B.RallyOffset + math.random(-2, 2),
                            sz * B.RallyOffset + math.random(-2, 2)),
                    }
                end
            end
            unitCounts[army] = count
        end
    end

    MoveLater(orders)
end

-- ============================================================
-- Bases, shops and upgrades
-- ============================================================

local function UpdateShopLabels(army)
    local p = data[army]
    local s = p.shops
    if not s then return end
    Label(s.info, ArmyName(army) .. ": $" .. p.money)
    Label(s.attack, "Attack " .. p.attack .. " - upgrade $" .. B.Shops.attackCost(p.attack))
    Label(s.defence, "Defence " .. p.defence .. " - upgrade $" .. B.Shops.defenceCost(p.defence))
    Label(s.kamikaze, "Kamikaze $" .. B.Shops.kamikazeCost(p.bought))
end

local function SpawnUpgrader(army)
    local p = data[army]
    if p.upgrader and not p.upgrader.deleted then p.upgrader:Delete() end
    local unit = Create(army, B.Shops.upgrader, BaseSpot(p.base, Map.upgrader))
    if not unit then return end
    Protect(unit)
    SetSpeed(unit, function() return B.Shops.upgraderSpeed end)
    Label(unit, "Upgrader: send me onto a shop")
    p.upgrader = unit
end

local function SpawnBase(army)
    local p = data[army]
    local layout = Map.baseLayouts[p.layout]
    p.shops = {}
    for _, kind in ipairs({ "info", "attack", "defence", "kamikaze" }) do
        local unit = Create(army, B.Shops[kind], BaseSpot(p.base, layout[kind]))
        if unit then
            Protect(unit)
            p.shops[kind] = unit
            shopOf[unit] = { army = army, kind = kind }
        end
    end
    SpawnUpgrader(army)
    UpdateShopLabels(army)
end

---Removes what can't be killed, so the army's defeat can finish.
local function RemoveBase(army)
    local p = data[army]
    local keep = {}
    for _, unit in pairs(p.shops or {}) do keep[#keep + 1] = unit end
    for _, unit in ipairs(p.artillery) do keep[#keep + 1] = unit end
    if p.upgrader then keep[#keep + 1] = p.upgrader end
    for _, unit in ipairs(keep) do
        if not unit.deleted then unit:Delete() end
    end
    p.shops, p.upgrader, p.artillery = nil, nil, {}
end

local function Pay(army, cost, what)
    local p = data[army]
    if p.money < cost then
        Tell(army, "Not enough money for " .. what .. ": $" .. cost .. ", you have $" .. p.money)
        return false
    end
    p.money = p.money - cost
    return true
end

local function BuyAttack(army)
    local p = data[army]
    if not Pay(army, B.Shops.attackCost(p.attack), "an attack upgrade") then return end
    p.attack = p.attack + 1
    Announce(ArmyName(army) .. ": attack upgraded to level " .. p.attack)
end

local function BuyDefence(army)
    local p = data[army]
    if not Pay(army, B.Shops.defenceCost(p.defence), "a defence upgrade") then return end
    p.defence = p.defence + 1
    for _, zone in ipairs(playableZones) do
        if zone.owner == army and zone.turret and not zone.turret.dead then
            ApplyDefence(zone.turret, p.defence, true)
        end
    end
    Announce(ArmyName(army) .. ": defence upgraded to level " .. p.defence)
end

local function BuyKamikaze(army)
    local p = data[army]
    local zone = RallyZone(army)
    if not zone then return end
    if not Pay(army, B.Shops.kamikazeCost(p.bought), "a kamikaze") then return end
    p.bought = p.bought + 1

    local unit = Create(army, B.Kamikaze.unit, ZoneCentre(zone, B.SpawnOffset, B.SpawnOffset))
    if not unit then return end
    local health = B.Kamikaze.health(p.attack)
    local blast = B.Kamikaze.blastDamage(p.attack)
    unit:SetMaxHealth(health)
    unit:SetHealth(health)
    DisableWeapons(unit)
    SetSpeed(unit, function(speed) return speed * B.Kamikaze.speed end)
    Label(unit, "Kamikaze: " .. ArmyName(army))

    -- It blows up where it died, a moment later. Not when it's merely removed.
    local originalOnDestroy = unit.OnDestroy
    unit.OnDestroy = function(self, destroyType)
        originalOnDestroy(self, destroyType)
        if destroyType == DestroyType.Delete then return end
        local at = self:GetPosition()
        local position = EngineClasses.float3(at.x, at.y, at.z)
        NewThread(function()
            WaitSeconds(B.Kamikaze.blastDelay)
            Import("host/collisionUpdate.lua").ProcessAreaDamage(position, B.Kamikaze.blastRadius, blast, army, false)
        end)
    end

    Announce(ArmyName(army) .. " bought a kamikaze")
end

local Buy = { attack = BuyAttack, defence = BuyDefence, kamikaze = BuyKamikaze }

---The shop a position is at, among an army's own.
local function ShopAt(army, position)
    local p = data[army]
    for kind in pairs(Buy) do
        local shop = p.shops and p.shops[kind]
        if shop and Near(position, shop:GetPosition(), B.Shops.reach) then return kind end
    end
    return nil
end

---Notes an upgrader being told to go to (or assist) one of its shops, as the
---order arrives: the game drops some orders an upgrader can't carry out
---(a raider can't assist), so they never become its active order.
local function NoteShopOrder(position, units, payload)
    for _, unit in ipairs(units or {}) do
        local army = unit.army
        local p = army and data[army]
        if p and unit == p.upgrader then
            local kind
            local target = payload and payload.targetGlobalId and GetUnitById(payload.targetGlobalId)
            local shop = target and shopOf[target]
            if shop and shop.army == army then
                kind = shop.kind
            elseif position then
                kind = ShopAt(army, position)
            end
            if kind then p.pendingShop = kind end
        end
    end
end

---Watches the orders players give, for NoteShopOrder. Every player order
---comes in through hostOrderManager's ClientIssueOrder, looked up on the
---module each time (orders.lua IssueOrder.Receive), so replacing it there
---reaches it.
local function WatchShopOrders()
    local originalClientIssueOrder = HostOrderManager.ClientIssueOrder
    HostOrderManager.ClientIssueOrder = function(orderId, position, unitsArray, append, orderTask, payload, ...)
        Guarded("shop orders", NoteShopOrder, position, unitsArray, payload)
        return originalClientIssueOrder(orderId, position, unitsArray, append, orderTask, payload, ...)
    end
end

---The upgrader buys from a shop when it's sent to one or told to assist it,
---then goes home. As in the original, it's where it's sent that counts, not
---where it stands: a fresh upgrader standing near a shop mustn't buy by itself.
local function CheckShops()
    for _, army in ipairs(players) do
        local p = data[army]
        local upgrader = p.upgrader
        if not defeated[army.id] and p.shops and upgrader and not upgrader.deleted then
            local kind = p.pendingShop
            if not kind then
                local order = upgrader.orders and upgrader.orders.activeOrder
                local target = order and order.positionFloat3
                kind = target and ShopAt(army, target)
            end
            p.pendingShop = nil
            if kind then
                Buy[kind](army)
                SpawnUpgrader(army)
                UpdateShopLabels(army)
            end
        end
    end
end

-- ============================================================
-- Kills and levels
-- ============================================================

local function MoneyTier(level)
    if level >= 3 then return 3 elseif level == 2 then return 2 end
    return 1
end

---The Mod API's kill credit: money and kills for whoever did it.
local function OnUnitKilled(victim, info)
    local killer = info and info.army
    local p = killer and data[killer]
    if not p or ended or defeated[killer.id] then return end

    if killer == victim.army then
        p.kills = p.kills + B.KillMoney.ownKill
        return
    end

    local victimData = data[victim.army]
    local tier = MoneyTier(victimData and victimData.level or 0)
    local byTurret = info.unit and info.unit.zcTurret
    p.kills = p.kills + 1
    p.money = p.money + (byTurret and B.KillMoney.turret[tier] or B.KillMoney.unit[tier])
end

local function KillsFor(level)
    if Options.insanity then return B.InsanityKills[level] end
    return B.Levels[level].kills
end

local function ArrivalHero(army, level)
    local hero = B.Heroes[level]
    if not hero then return end
    local p = data[army]

    if hero.unit then
        local zone = RallyZone(army)
        if not zone then return end
        local unit = Create(army, hero.unit, ZoneCentre(zone, -B.SpawnOffset, B.SpawnOffset))
        if not unit then return end
        Label(unit, hero.name .. ": " .. ArmyName(army))
        MoveLater({ { unit = unit, position = ZoneCentre(zone, -B.RallyOffset, B.RallyOffset) } })
    elseif hero.artillery then
        local layout = Map.baseLayouts[p.layout]
        local unit = Create(army, hero.artillery, BaseSpot(p.base, layout[hero.slot]))
        if not unit then return end
        Protect(unit, true)
        Label(unit, hero.name .. ": " .. ArmyName(army))
        p.artillery[#p.artillery + 1] = unit
    elseif hero.reload then
        for _, unit in ipairs(p.artillery) do SetWeaponScale(unit, 1, hero.reload) end
    end

    Announce(ArmyName(army) .. ": " .. hero.name .. " has arrived")
end

local function CheckLevels()
    for _, army in ipairs(players) do
        local p = data[army]
        while not defeated[army.id] and p.level < B.MaxLevel and p.kills >= KillsFor(p.level + 1) do
            p.level = p.level + 1
            Announce(ArmyName(army) .. " has reached " .. B.Levels[p.level].name)
            if p.base then ArrivalHero(army, p.level) end
        end
    end
end

-- ============================================================
-- Winning and losing
-- ============================================================

local function EndMatch(winners)
    ended = true
    for _, timer in ipairs(timers) do timer:Cancel() end

    -- The game only decides a winner when every enemy is gone, and the
    -- neutral army never is, so the victory is sent here.
    local names = {}
    for _, army in ipairs(winners) do
        SessionCommands.ExecuteClientFunction.Send("WinConditionUpdate", { armyID = army.id, condition = WinCondition.Won })
        names[#names + 1] = ArmyName(army)
    end
    Announce(#names > 0 and ("Zone Control: " .. table.concat(names, ", ") .. " wins!") or "Zone Control: nobody is left.")
end

local function CheckVictory()
    -- Nobody loses in the first moments: a setup problem shows its report
    -- instead of ending the match.
    if Events.GameTime() < B.GracePeriod then return end
    local counts = ZoneCounts()
    for _, army in ipairs(players) do
        if not defeated[army.id] and not counts[army] then
            defeated[army.id] = true
            RemoveBase(army)
            -- Destroys what's left of the army and tells its player they lost.
            army:Destroy()
            Announce(ArmyName(army) .. " has lost every zone and is out.")
        end
    end

    -- Alone in a lobby there's nobody to beat, so play on.
    if not contested then return end

    local left = {}
    for _, army in ipairs(players) do
        if not defeated[army.id] then left[#left + 1] = army end
    end
    for a = 1, #left do
        for b = a + 1, #left do
            if not Friendly(left[a], left[b]) then return end
        end
    end
    EndMatch(left)
end

-- ============================================================
-- The banner
-- ============================================================

local function Personal(army)
    local p = army and data[army]
    if not p or not economy or defeated[army.id] then return "" end
    local text = " || $" .. p.money .. " | level " .. p.level .. " | " .. math.floor(p.kills) .. " kills"
    if p.level < B.MaxLevel then
        text = text .. " (next level at " .. KillsFor(p.level + 1) .. ")"
    end
    local looks = B.Shops.looks
    if p.shops then
        text = text .. "\nShops: " .. looks.attack .. " = attack " .. p.attack .. ", next $" .. B.Shops.attackCost(p.attack)
            .. " | " .. looks.defence .. " = defence " .. p.defence .. ", next $" .. B.Shops.defenceCost(p.defence)
            .. " | " .. looks.kamikaze .. " = kamikaze $" .. B.Shops.kamikazeCost(p.bought)
    end
    return text
end

local function BroadcastStatus()
    if not Options.statusPanel then return end

    local counts = ZoneCounts()
    local holding = {}
    for _, army in ipairs(players) do
        if counts[army] then holding[#holding + 1] = army end
    end
    table.sort(holding, function(a, b)
        if counts[a] ~= counts[b] then return counts[a] > counts[b] end
        return a.id < b.id
    end)

    local parts = {}
    for _, army in ipairs(holding) do
        parts[#parts + 1] = ArmyName(army) .. " " .. counts[army]
    end
    parts[#parts + 1] = "Neutral " .. (counts[neutral] or 0)
    local zonesText = "ZONES | " .. table.concat(parts, " | ")

    -- Each player also sees their own money and level.
    for clientId, player in pairs(Import("common/lobby.lua").Players) do
        local text = zonesText .. Personal(player.armyID and Armies[player.armyID])
        if text ~= lastStatus[clientId] then
            lastStatus[clientId] = text
            SendToClient({ text = text, enabled = true }, "ZoneControlStatus", clientId)
        end
    end
end

-- ============================================================
-- Setup
-- ============================================================

---The zone an army's start marker is in, or the nearest free one if the
---marker isn't in a played zone.
local function HomeZone(army)
    if not GameUtils.GetMarker(army.name, "Spawn", true) then return nil end
    local position = GameUtils.MarkerToPosition(army.name, "Spawn")

    local zone = ZoneAt(position)
    if zone and zone.playable and not zone.home then return zone end

    local best, bestDistance
    for _, candidate in ipairs(playableZones) do
        if not candidate.home then
            local centre = ZoneCentre(candidate)
            local distance = (centre.x - position.x) ^ 2 + (centre.z - position.z) ^ 2
            if not bestDistance or distance < bestDistance then
                best, bestDistance = candidate, distance
            end
        end
    end
    return best
end

---The base beside a home zone, from the map's layout.
local function BaseFor(zone)
    for _, p in ipairs(Map.players) do
        if p.home[1] == zone.i and p.home[2] == zone.j then return p end
    end
    return nil
end

local function StartZoneControl()
    offset = (GameInfo.MapInfo.size[1] - Map.size) / 2

    local ids = {}
    for id in pairs(Armies) do ids[#ids + 1] = id end
    table.sort(ids)
    for _, id in ipairs(ids) do
        local army = Armies[id]
        if IsPlayerArmy(army) then
            players[#players + 1] = army
            data[army] = { kills = 0, money = 0, level = 0, attack = 0, defence = 0, bought = 0, artillery = {} }
        end
    end

    neutral = CreateNeutralArmy()

    for a = 1, #players do
        for b = a + 1, #players do
            if not Friendly(players[a], players[b]) then contested = true end
        end
    end

    for i = 1, Map.cells do
        zones[i] = {}
        for j = 1, Map.cells do
            local zone = { i = i, j = j, playable = IsPlayable(i, j), owner = neutral }
            zones[i][j] = zone
            if zone.playable then playableZones[#playableZones + 1] = zone end
        end
    end

    for _, army in ipairs(players) do
        local zone = HomeZone(army)
        if zone then
            zone.home = army
            zone.owner = army
            data[army].home = zone
            local base = BaseFor(zone)
            if base then
                data[army].base = base.base
                data[army].layout = base.layout
            end
        else
            Warn("Zone Control: " .. army.name .. " has no start marker, so no home zone")
        end
    end

    local placed = 0
    for _, zone in ipairs(playableZones) do
        if SpawnTurret(zone, zone.owner) then placed = placed + 1 end
    end
    if placed < #playableZones then
        Report("turrets", "only " .. placed .. " of " .. #playableZones .. " placed")
    end

    -- Commanders are kept out by the SpawnInitialUnits hook below; this
    -- catches any that got in another way. Delete, unlike Destroy, doesn't
    -- count as losing one.
    for _, unit in ipairs(GetListOfUnits(nil, Tags.COMMAND, true)) do
        if not unit.dead then unit:Delete() end
    end

    if economy then
        for _, army in ipairs(players) do
            if data[army].base then SpawnBase(army) end
        end
    end

    Announce("Zone Control: every zone you hold sends you units. Destroy a zone's turret, then hold the zone with "
        .. B.CaptureCount .. " or more units to take it. Lose every zone and you're out.")
    if economy then
        local looks = B.Shops.looks
        Announce("Kills earn money and levels. In your base, send your upgrader onto a shop, or tell it to assist one, to buy: the "
            .. looks.attack .. " sells attack upgrades, the " .. looks.defence .. " defence upgrades, the "
            .. looks.kamikaze .. " kamikazes. The storage shows your money.")
    else
        Announce("This Mod API has no kill credit (Events.OnUnitKilled), so there's no money or levels this match.")
    end
    BroadcastStatus()

    timers[#timers + 1] = Events.Every(1, function()
        if ended then return end
        if economy then
            Guarded("shops", CheckShops)
            Guarded("levels", CheckLevels)
        end
        Guarded("zones", CheckZones)
        Guarded("victory", CheckVictory)
        Guarded("banner", BroadcastStatus)
        if economy then
            for _, army in ipairs(players) do
                if not defeated[army.id] then Guarded("shop labels", UpdateShopLabels, army) end
            end
        end
        Guarded("labels", BroadcastLabels)
    end)
    timers[#timers + 1] = Events.Every(Options.spawnSeconds, function()
        if not ended then Guarded("spawning", SpawnUnits) end
    end)

    Log("Zone Control: " .. #players .. " players, " .. #playableZones .. " zones, neutral army " .. neutral.id
        .. ", map offset " .. offset .. (economy and ", kill credit on" or ", no kill credit"))
end

---A weapon with no muzzle (a bone the engine couldn't find, as Chosen's T2
---point defence has in the Playtest build) makes the game's targeting error
---every tick, which stops the match. Such a weapon is left idle instead, and
---reported. Put in at match start: importing the weapon code as this script
---loads would be before the game's lobby setup (see OnMatchStart below).
function GuardTargeting()
    local HostWeapon = Import("host/units/weaponsClasses/weaponsBaseClass.lua").HostWeapon
    local originalSelectTarget = HostWeapon.SelectTarget
    HostWeapon.SelectTarget = function(self, ...)
        local group = self.muzzleGroups and self.muzzleGroups[1]
        if not (group and group[1]) then
            Report("a " .. tostring(self.unit and self.unit.tpId) .. " weapon has no muzzle",
                "it's left idle, so it can't stop the match")
            return nil
        end
        return originalSelectTarget(self, ...)
    end
end

-- This script runs as the host loads, before the map and armies: the place
-- to stop commanders spawning. CreateArmies looks SpawnInitialUnits up on
-- the gameUtils module, so replacing it there reaches it.
local originalSpawnInitialUnits = GameUtils.SpawnInitialUnits
GameUtils.SpawnInitialUnits = function(...)
    if IsZoneControlMap() then return end
    return originalSpawnInitialUnits(...)
end

Events.OnMatchStart(function()
    if not IsZoneControlMap() then
        Warn("Zone Control: " .. tostring(GameInfo.MapInfo and GameInfo.MapInfo.name) .. " isn't a Zone Control map")
        Announce("Zone Control is on, but this isn't a Zone Control map, so this is a normal match.")
        return
    end
    -- Kill credit is asked for here, once the match has started, not as this
    -- script loads. Asked for early, Mod API 1.2.0 puts its damage hooks in at
    -- the start of the first tick, before the game's lobby setup (InitLobby
    -- runs inside that tick, hostMain.lua:60). Its hooks import the unit and
    -- weapon classes, and targeterCollider.lua then caches __TargeterColliders
    -- before targeterManager.lua has made it, so every unit with a weapon
    -- fails to build. Asked for now, the hooks go in straight away, after the
    -- setup and before any turret is built.
    if economy then Events.OnUnitKilled(OnUnitKilled) end
    GuardTargeting()
    WatchShopOrders()
    Guarded("setup", StartZoneControl)
    -- The engine only shows a name while it's drawn every tick. Names don't
    -- show in the Playtest build either way; this and client.lua are for when
    -- they do.
    Events.OnTick(function()
        for unit in pairs(named) do
            if not (unit.dead or unit.deleted) then unit:DrawCustomName(unit.zcName) end
        end
    end)
end)
