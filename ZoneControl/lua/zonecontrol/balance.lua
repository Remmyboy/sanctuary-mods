-- Zone Control: the map's layout and every number the rules use.
--
-- The rules follow johnie102's Zone Control for Forged Alliance (8P V2). The
-- numbers are the original's where Sanctuary's units allow, rescaled where they
-- don't: its units run from 145 to 11,000 health and 25 to 350 damage a second
-- between tiers, so spawned units get the original's health formula and a set
-- damage per level instead of their own.

-- Zone Control for FAF 8P V2, in the original map's coordinates (512 x 512).
Map = {
    size = 512,
    zoneSize = 46.5,
    offsetX = 1,        -- where the grid starts
    offsetZ = 2,
    cells = 11,         -- an 11 x 11 grid
    -- Only the zones in this diamond are played; the grid's corners are off the plateau.
    centre = 6,
    radius = 5,
    -- Each start position's zone, the base cell on the rim beside it where its
    -- shops stand, and which of the two base layouts it uses.
    players = {
        { home = { 5, 2 },  base = { 6, 1 },  layout = 1 },
        { home = { 3, 4 },  base = { 4, 3 },  layout = 2 },
        { home = { 2, 7 },  base = { 1, 6 },  layout = 1 },
        { home = { 4, 9 },  base = { 3, 8 },  layout = 2 },
        { home = { 7, 10 }, base = { 6, 11 }, layout = 1 },
        { home = { 9, 8 },  base = { 8, 9 },  layout = 2 },
        { home = { 10, 5 }, base = { 11, 6 }, layout = 1 },
        { home = { 8, 3 },  base = { 9, 4 },  layout = 2 },
    },
    -- Where things stand in a base cell, in metres from its corner.
    baseLayouts = {
        { info = { 23, 9 }, attack = { 9, 23 }, defence = { 35, 23 }, kamikaze = { 23, 37 }, artillery = { 35, 37 }, artillery2 = { 9, 37 } },
        { info = { 9, 9 }, attack = { 37, 9 }, defence = { 9, 37 }, kamikaze = { 37, 37 }, artillery = { 23, 37 }, artillery2 = { 23, 9 } },
    },
    upgrader = { 23, 23 },
}

-- ============================================================
-- Turrets
-- ============================================================

-- T2 point defences, as the original's. Neutral zones get EDA's railgun, a
-- beam: it hits the moment it fires, so it can't be dodged (the original's
-- neutral Cybran turret was a laser). A zone you take gets Guard's plasma
-- cannon with a wide splash, standing in for the original's Oblivion.
-- (Chosen's T2 point defence would suit better, but it's broken in the
-- Playtest build: its weapon ends up with no muzzle, and targeting errors
-- on it every tick, which stops the match.)
-- Both are set to take two hits to kill a starting unit (200 health).
Turrets = {
    neutral = "ues2001",        -- 400 damage every 2.5 s, instant
    player = "ugs2001",         -- 160 damage every 2 s, splash below

    neutralHealth = 600,        -- the original's
    neutralDamage = 0.25,       -- 100 a hit

    -- A player's turrets, by defence level d. The original: health 5000 + 1000 d
    -- (+ 70 d^2 once upgraded), regeneration 12 (d + 1), and more rate of fire
    -- and damage per level; here that last part is one damage multiplier.
    health = function(d) return 5000 + 1000 * d + 70 * d * d end,
    regen = function(d) return d > 0 and 12 * (d + 1) or 0 end,
    damage = function(d) return 1 + 0.25 * d end,   -- 160 a hit at level 0
}

-- Changes to unit templates, made as the game loads them (see
-- append/common/systems/templateLoader.lua), so everything built from a
-- template agrees: targeting, firing, range rings, movement.
--
-- The turrets: out of the box they reach 50 m but see only 25, so units
-- outside 25 m can shoot them unseen, and at 50 m neighbouring turrets
-- (46.5 m apart) would fight each other; the original capped its turrets at
-- 28 m for that reason. A weapon finds targets with a circle of its range
-- (unitTemplateLoader.lua:277). 30 m is a little past the units' own 15-20,
-- so attackers take a shot or two closing in; vision just past it means
-- nothing in range goes unseen.
--
-- The spawned units: the original sped its units up by 1.9, so they rush
-- from zone to zone. Here every level's unit moves at 5, and the heavier
-- ones accelerate and turn faster than their own (the Kodiak: speed 2.5,
-- acceleration 2, turning 45 degrees a second).
TemplateTuning = {
    ues2001 = { rangeMax = 30, visionRadius = 32 },
    ugs2001 = { rangeMax = 30, visionRadius = 32, damageRadius = 5 },
    uel1002 = { speed = 5 },
    uel1001 = { speed = 5, acceleration = 6, rotationSpeed = 120 },
    uel2002 = { speed = 5, acceleration = 6, rotationSpeed = 120 },
    uel3001 = { speed = 5, acceleration = 5, rotationSpeed = 100 },
}

-- ============================================================
-- Spawned units
-- ============================================================

-- Everyone gets EDA's units, as everyone got UEF's in the original, so no
-- faction's zones are worth more than another's. By level:
--   unit    what spawns (the original's in brackets)
--   dps     damage a second, whatever the unit's own is
--   every   spawns on every n-th spawn beat
--   health  the original's upgrade multiplier: health = 200 x this + 50 x attack
Levels = {
    [0] = { unit = "uel1002", dps = 25, every = 1, health = 1, kills = 0,    name = "Raiders" },                -- (Mech Marine)
    [1] = { unit = "uel1001", dps = 35, every = 1, health = 2, kills = 50,   name = "LEVEL 1: Pumas" },          -- (Striker)
    [2] = { unit = "uel2002", dps = 50, every = 2, health = 3, kills = 150,  name = "LEVEL 2: T2 Raiders" },     -- (Pillar; EDA has no T2 tank)
    [3] = { unit = "uel3001", dps = 80, every = 3, health = 6, kills = 400,  name = "LEVEL 3: Kodiaks" },        -- (Titan)
    [4] = { unit = "uel3001", dps = 80, every = 3, health = 7, kills = 800,  name = "LEVEL 4" },
    [5] = { unit = "uel3001", dps = 80, every = 3, health = 8, kills = 1000, name = "LEVEL 5" },
    [6] = { unit = "uel3001", dps = 80, every = 3, health = 9, kills = 1200, name = "LEVEL 6" },
    [7] = { unit = "uel3001", dps = 80, every = 3, health = 10, kills = 1400, name = "LEVEL 7" },
    [8] = { unit = "uel3001", dps = 80, every = 3, health = 10, kills = 1600, name = "LEVEL 8" },
    [9] = { unit = "uel3001", dps = 80, every = 3, health = 10, kills = 1800, name = "LEVEL 9" },
}
MaxLevel = 9

-- The "insanity" option's thresholds, the original's.
InsanityKills = { [1] = 1, [2] = 2, [3] = 3, [4] = 20, [5] = 40, [6] = 60, [7] = 100, [8] = 150, [9] = 200 }

Units = {
    baseHealth = 200,
    healthPerAttack = 50,
    damagePerAttack = 0.15,     -- the original added 5 damage a shot per level
}

-- What each level brings besides its units. Heroes appear at the zone nearest
-- your home that you still hold. The original's nuke launchers are artillery
-- here: invulnerable, in your base, in range of the whole map.
Heroes = {
    [4] = { unit = "ugl4001", name = "Guard T4 Bot" },               -- (Galactic Colossus)
    [5] = { unit = "ucl4004", name = "Chosen T4 Big Bot" },          -- (Monkeylord)
    [6] = { unit = "uel4002", name = "EDA T4 Mech" },                -- (Fatman)
    [7] = { artillery = "ugs3101", slot = "artillery", name = "Artillery" },         -- (Nuke Launcher)
    [8] = { artillery = "ucs3101", slot = "artillery2", name = "Heavy Artillery" },  -- (Bringer of Death)
    [9] = { reload = 0.5, name = "Faster artillery" },               -- (more nukes)
}

-- ============================================================
-- Money and shops
-- ============================================================

-- Money for a kill, by the victim's owner's level (0-1, 2, 3+). A turret's
-- kill is worth more. Killing your own units counts 1/8 of a kill, no money.
KillMoney = {
    unit = { 5, 10, 15 },
    turret = { 10, 15, 20 },
    ownKill = 1 / 8,
}

-- The shops stand in your base. Names drawn over units don't show in the
-- Playtest build, so each is a building you can tell apart, and the banner
-- and a message at the start say which sells what (the `looks` names).
-- Small ones only: anything bigger overlaps its neighbour.
Shops = {
    info = "ues1612",           -- Energy Storage: your money
    attack = "ues1001",         -- Point Defence, its gun off
    defence = "ues1701",        -- Radar (radar isn't in the game yet, so it does nothing)
    kamikaze = "ues1611",       -- Energy Generator
    looks = { attack = "gun", defence = "radar", kamikaze = "generator" },
    upgrader = "uel1002",
    upgraderSpeed = 8,
    reach = 10,                 -- a shop sells when the upgrader is sent within this of it
    attackCost = function(level) return (level + 1) * 50 end,
    defenceCost = function(level) return (level + 1) * 50 end,
    kamikazeCost = function(bought) return 100 + 50 * bought end,
}

-- The original's kamikaze: a Cybran siege bot with its guns off, twice as
-- fast, that blows up 3 seconds after it dies.
Kamikaze = {
    unit = "uel3001",
    health = function(attack) return 2000 + 1000 * attack end,
    blastDamage = function(attack) return 5000 + 1000 * attack end,
    blastRadius = 12,
    blastDelay = 3,
    speed = 2,
}

-- ============================================================
-- Capture
-- ============================================================

GracePeriod = 10            -- seconds at the start when nobody can lose
CaptureCount = 5            -- units that take a zone, if nobody has as many
RetakeCount = 10            -- the owner's units that take it back, with no enemies there

-- Spawned units appear this far from the zone's centre, clear of its turret,
-- then walk a little further out.
SpawnOffset = 6
RallyOffset = 12
