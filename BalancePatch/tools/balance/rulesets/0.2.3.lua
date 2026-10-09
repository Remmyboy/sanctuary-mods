-- Every change the Balance Patch makes, by lobby section. Plain data, no
-- code: tools/preview.mjs reads this file too, to print before/after tables.
--
-- A change: why (shown in the preview), a selector (ids, idPattern, tags,
-- notTags; all must hold), then set = { path = value } and/or
-- scale = { path = factor } (round = true rounds the results), and
-- clear = { path, ... } removes fields, and expect = { path = value } is what the
-- game had when the change was made.
-- kind = "projectile" for projectile templates. Paths: see patch.lua.
--
-- Unit ids: ue/uc/ug = EDA/Chosen/Guardians, then l/a/n/s = land, air,
-- naval, structure. "Value" in the notes is alloys + energy / 10.

Sections = {
    {
        key = "fixes",
        changes = {
            {
                why = "The EDA T3 anti-air fighter's second launcher fired at land and sea. It now fires at aircraft like the first, which also puts its anti-air damage level with the other factions' T3 fighters.",
                ids = { "uea3201" },
                expect = { ["weapons.2.layerTargetLimits"] = { "Land", "WaterSurface" } },
                set = { ["weapons.2.layerTargetLimits"] = { "Air", "LandedAir" } },
            },
            {
                why = "Bombs lived 2 seconds at speed 20, about 40 range, while bombers release from up to 60 (T1) or 90 (T3): the EDA T3 bomber fired all the time and never hit anything. They now live long enough to land.",
                tags = { "BOMBER", "TECH3" },
                expect = { ["weapons.1.projectileLifetime"] = 2 },
                set = { ["weapons.1.projectileLifetime"] = 5 },
            },
            {
                why = "Likewise for T1 bombs.",
                tags = { "BOMBER", "TECH1" },
                notTags = { "EDA" },
                expect = { ["weapons.1.projectileLifetime"] = 2 },
                set = { ["weapons.1.projectileLifetime"] = 4 },
            },
            {
                why = "The Chosen T2 point defence (Redoubt) shot the ground: its model's barrel bone rests about 55 degrees nose-down, and it may only pitch up 45, so it could never raise the barrel to level. It never came on target (0% in tests at 20-47 range, other T2 point defences 48-94%). It may pitch up 100 now, rests level, and turns the model's real yaw bone (it named Turret01, which the model doesn't have).",
                ids = { "ucs2001" },
                expect = { ["weapons.1.aimControllers.1.yawBone"] = "Turret01", ["weapons.1.aimControllers.1.pitchMax"] = 45, ["weapons.1.aimControllers.1.defaultPitchAdjustment"] = 0 },
                set = {
                    ["weapons.1.aimControllers.1.yawBone"] = "Turret01_Yaw01",
                    ["weapons.1.aimControllers.1.pitchMax"] = 100,
                    ["weapons.1.aimControllers.1.defaultPitchAdjustment"] = 55,
                },
            },
            {
                why = "The Guardian TALEN gunship is built by T3 air factories at T3 cost but was tagged and labelled tier 1.",
                ids = { "uga3011" },
                expect = { ["general.displayName"] = "Tier 1: Gunship" },
                set = {
                    ["general.displayName"] = "Tier 3: Gunship",
                    tags = { "AIR", "ANTI_SURFACE", "BUILDABLE_BY_T3_FACTORY", "DIRECT_FIRE", "GUARD", "GUNSHIP", "MOBILE", "TECH3" },
                },
            },
        },
    },
    {
        -- Stage 1 of the 0.2 redesign (REDESIGN-PLAN.md): the economy as one section, so it is
        -- never half on. Numbers from tools/balance (sweep.mjs, proposed/stage1-levers.mjs)
        -- against tools/balance/targets.json; "simulated" in a note means that tool, not the game.
        key = "economy",
        changes = {
            {
                why = "The commander makes 3 alloys and 30 energy a second (was 5 and 50): about three extractors' worth instead of five, so income comes from taking the map. Its storage stays 500 alloys and 5000 energy, but an army starts with it full (was half, 250 and 2500), so the opening isn't starved.",
                tags = { "COMMAND" },
                expect = { ["economy.production"] = { alloys = 5, energy = 50 }, ["economy.storage"] = { alloys = 500, energy = 5000 } },
                set = { ["economy.production"] = { alloys = 3, energy = 30 } },
            },
            {
                why = "T2 extractors make 5 alloys a second (were 4): the upgrade (600 alloys) pays back in 150 s instead of 200 s, so small maps can grow by upgrading once their spots are taken.",
                idPattern = "^u.s2601$",
                expect = { ["economy.production.alloys"] = 4 },
                set = { ["economy.production.alloys"] = 5 },
            },
            {
                why = "T1 generators make 20 energy a second (were 10): power is half of what everything costs, and a generator now supports a factory and a bit more.",
                idPattern = "^u.s1611$",
                expect = { ["economy.production.energy"] = 10 },
                set = { ["economy.production.energy"] = 20 },
            },
            {
                why = "T1 generators had 10x the health per energy of T2 and T3 ones.",
                idPattern = "^u.s1611$",
                expect = { ["defence.health.max"] = 800 },
                set = { ["defence.health.max"] = 500, ["defence.health.value"] = 500 },
            },
            {
                why = "T2 generators give 1.5x the energy per cost of T1 ones (were equal): 600 a second.",
                idPattern = "^u.s2611$",
                expect = { ["economy.production.energy"] = 200 },
                set = { ["economy.production.energy"] = 600 },
            },
            {
                why = "T3 generators make 2500 energy a second (were 1000) and cost 2800 alloys and 28000 energy (were 5000 and 50000; build time 2800, was 5000): about 2.25x the energy per cost of T1 ones (were equal), in a smaller step that feeds two or three Alloy Furnaces.",
                idPattern = "^u.s3611$",
                expect = { ["economy.production.energy"] = 1000, ["economy.cost"] = { alloys = 5000, energy = 50000 }, ["economy.buildTime"] = 5000 },
                set = { ["economy.production.energy"] = 2500, ["economy.cost"] = { alloys = 2800, energy = 28000 }, ["economy.buildTime"] = 2800 },
            },
            {
                why = "T3 extractors make 15 alloys a second (were 10): the upgrade (2000 alloys) pays back in 200 s instead of 400 s, so T3 economy pays for itself inside a 20-30 minute game.",
                idPattern = "^u.s3601$",
                expect = { ["economy.production.alloys"] = 10 },
                set = { ["economy.production.alloys"] = 15 },
            },
            {
                why = "T1 factories cost 300 alloys and 3000 energy (were 150 and 1500), so a factory is a decision, not something to spam: in a simulated 10 minutes players end with 5-7 instead of 10-20.",
                idPattern = "^u.s151[123]$",
                expect = { ["economy.cost"] = { alloys = 150, energy = 1500 } },
                set = { ["economy.cost"] = { alloys = 300, energy = 3000 } },
            },
            {
                why = "Upgrading a factory to T2 costs 1500 alloys and 15000 energy (was 500 and 5000; same build time), so T2 has to be paid for without being out of reach.",
                idPattern = "^u.s251[123]$",
                expect = { ["economy.cost"] = { alloys = 500, energy = 5000 } },
                set = { ["economy.cost"] = { alloys = 1500, energy = 15000 } },
            },
            {
                why = "Upgrading a factory to T3 costs 4000 alloys and 40000 energy (was 2000 and 20000; same build time).",
                idPattern = "^u.s351[123]$",
                expect = { ["economy.cost"] = { alloys = 2000, energy = 20000 } },
                set = { ["economy.cost"] = { alloys = 4000, energy = 40000 } },
            },
            {
                why = "T3 tanks and the Guardian Nitro (T3 raider) cost 30% less, alloys and energy (same build time): a T3 tank was about 29 T1 tanks' worth of alloys, and T3 should be within reach in a 20-30 minute game.",
                ids = { "uel3001", "ucl3001", "ugl3001", "ugl3002" },
                round = true,
                scale = { ["economy.cost.alloys"] = 0.7, ["economy.cost.energy"] = 0.7 },
            },
            {
                why = "Land and naval units cost 6 energy per alloy (were 10), same alloys: land units are paid for with the map, aircraft with the base.",
                tags = { "MOBILE" },
                notTags = { "AIR", "COMMAND", "CONSTRUCTION", "COMBAT_ENGINEER" },
                round = true,
                scale = { ["economy.cost.energy"] = 0.6 },
            },
            {
                why = "Chosen aircraft cost half the energy of EDA and Guardian ones with the same stats. Now 20 energy per alloy like theirs.",
                tags = { "AIR", "CHOSEN" },
                round = true,
                scale = { ["economy.cost.energy"] = 2 },
            },
            {
                why = "T1 engineers had 750 health, five to ten times a T1 raider's, so raiding them barely worked.",
                tags = { "ENGINEER", "TECH1" },
                expect = { ["defence.health.max"] = 750 },
                set = { ["defence.health.max"] = 300, ["defence.health.value"] = 300 },
            },
            {
                why = "T2 engineers in proportion.",
                tags = { "ENGINEER", "TECH2" },
                expect = { ["defence.health.max"] = 1500 },
                set = { ["defence.health.max"] = 650, ["defence.health.value"] = 650 },
            },
            {
                why = "T3 engineers in proportion.",
                tags = { "ENGINEER", "TECH3" },
                expect = { ["defence.health.max"] = 3000 },
                set = { ["defence.health.max"] = 1300, ["defence.health.value"] = 1300 },
            },
        },
    },
    {
        key = "commanders",
        changes = {
            {
                why = "EDA commander: half the damage per missile, twice as often (same damage per second), so less of it is wasted on targets that are already dead.",
                ids = { "uel0000" },
                expect = { ["weapons.1.damage"] = 100, ["weapons.1.reloadTime"] = 2 },
                set = { ["weapons.1.damage"] = 50, ["weapons.1.reloadTime"] = 1 },
            },
            {
                why = "Guardian commander: the same.",
                ids = { "ugl0000" },
                expect = { ["weapons.1.damage"] = 100, ["weapons.1.reloadTime"] = 3 },
                set = { ["weapons.1.damage"] = 50, ["weapons.1.reloadTime"] = 1.5 },
            },
            {
                why = "The EDA and Guardian commanders' missile hung at speed 5 for about three seconds before flying, so anything moving walked out of its way (measured: 23-43% of its damage landed on moving T1 tanks, the Chosen commander's 72%). It now flies at once and steers at where its target is going.",
                kind = "projectile",
                ids = { "pei141" },
                expect = { ["movement.type"] = "TargetEntity", ["extraStages.1.speedMax"] = 5, ["extraStages.2.delay"] = 2.7 },
                set = {
                    ["movement.type"] = "LeadTargetEntity",
                    ["movement.speedMax"] = 15,
                    ["movement.acceleration"] = 20,
                    ["extraStages.1.speedMax"] = 15,
                    ["extraStages.1.rotationSpeed"] = 360,
                    ["extraStages.2.delay"] = 0.4,
                    ["extraStages.2.acceleration"] = 30,
                    ["extraStages.2.speedMax"] = 25,
                    ["extraStages.2.rotationSpeed"] = 360,
                },
            },
        },
    },
    {
        key = "artillery",
        changes = {
            {
                why = "Artillery aims where a moving target will be instead of where it was. Its spread still makes it miss sometimes.",
                where = { rangeRingType = "IndirectFire" },
                set = { ["weapons.*.aimControllers.*.leadTarget"] = true },
            },
            {
                why = "Guardian T1 artillery's single big shell lands most often once it leads (66% of its damage on crossing tanks, against Chosen's 31%), so less per shell.",
                ids = { "ugl1101" },
                expect = { ["weapons.1.damage"] = 360 },
                set = { ["weapons.1.damage"] = 220 },
            },
            {
                why = "Chosen T1 artillery lost every fight against its own value in T1 tanks once the others were tuned: a little more splash.",
                ids = { "ucl1101" },
                expect = { ["weapons.1.damageRadius"] = 1 },
                set = { ["weapons.1.damageRadius"] = 1.5 },
            },
            {
                why = "EDA T1 artillery's rocket salvo had almost no splash (0.5), so its spread shells mostly hit nothing (7% on crossing tanks).",
                ids = { "uel1101" },
                expect = { ["weapons.1.damageRadius"] = 0.5 },
                set = { ["weapons.1.damageRadius"] = 1 },
            },
        },
    },
    {
        key = "air",
        changes = {
            {
                why = "Bombers aim where a moving target will be. Without it the Chosen T1 bomber landed 77% of its bombs on still tanks and none on moving ones.",
                tags = { "BOMBER" },
                set = { ["weapons.*.aimControllers.*.leadTarget"] = true },
            },
            {
                why = "Anti-air weapons lead their targets. The T3 fighters, the T3 anti-air towers and others shot at where an aircraft was, so a moving plane was mostly missed: against weaving T1 bombers the T3 fighters went from 23-58% to 54-78%, the EDA T3 tower from 39% to 50%. Not the EDA T2 mobile anti-air (Komodo): leading, its shells fell from 71% to 1%.",
                where = { rangeRingType = "AntiAir" },
                notIds = { "uel2201" },
                set = { ["weapons.*.aimControllers.*.leadTarget"] = true },
            },
            {
                why = "The EDA T3 anti-air tower and the Behemoth's anti-air fired speed-10 shells that lived 5 seconds: 50 range out of 60. They now reach their full range.",
                ids = { "ues3201", "uel4002" },
                where = { rangeRingType = "AntiAir" },
                expect = { ["weapons.*.projectileLifetime"] = 5 },
                set = { ["weapons.*.projectileLifetime"] = 8 },
            },
            {
                why = "The Chosen T3 anti-air tower's missile turned at 10 degrees a second and lived 2 seconds (about 45 range out of 60), so it landed 9% on weaving T1 bombers. It turns like the EDA one (60) and reaches its range: 16-18%.",
                kind = "projectile",
                ids = { "pca341" },
                expect = { ["movement.rotationSpeed"] = 10 },
                set = { ["movement.rotationSpeed"] = 60 },
            },
            {
                why = "Likewise for its missile's lifetime.",
                ids = { "ucs3201" },
                where = { rangeRingType = "AntiAir" },
                expect = { ["weapons.*.projectileLifetime"] = 2 },
                set = { ["weapons.*.projectileLifetime"] = 4 },
            },
            {
                why = "The Guardian T3 anti-air tower aimed its slow, falling shells (speed 20) straight at its target and hit nothing: 0% on weaving T1 bombers, 3% on a hovering gunship. It fires faster shells and aims for their fall: 18% and 13%. Still the weakest T3 anti-air tower.",
                ids = { "ugs3201" },
                where = { rangeRingType = "AntiAir" },
                expect = { ["weapons.*.aimControllers.*.projectileSpeed"] = 20, ["weapons.*.aimControllers.*.solverType"] = "NoArc" },
                set = { ["weapons.*.aimControllers.*.projectileSpeed"] = 40, ["weapons.*.aimControllers.*.solverType"] = "LowArc" },
            },
            {
                why = "T1 fighters' bullets lived exactly long enough to reach their range (0.5 s at speed 50 for 25), so anything pulling away outran them.",
                tags = { "FIGHTER", "TECH1" },
                expect = { ["weapons.1.projectileLifetime"] = 0.5 },
                set = { ["weapons.*.projectileLifetime"] = 0.75 },
            },
            {
                why = "The Guardian T1 fighter (Aerofoil) was the slowest fighter (12 against 15) and lost every fight with the other T1 fighters without killing any: as fast as them, and more damage.",
                ids = { "uga1201" },
                expect = { ["movement.speed"] = 12, ["weapons.1.damage"] = 25 },
                set = { ["movement.speed"] = 15, ["weapons.*.damage"] = 40 },
            },
            {
                why = "The Guardian T3 fighter (Contrail) lost to both other T3 fighters even leading its shots: more damage per shot.",
                ids = { "uga3201" },
                expect = { ["weapons.1.damage"] = 500 },
                set = { ["weapons.1.damage"] = 650 },
            },
            {
                why = "Bombs with no splash do nothing when they land beside a target: T1 bombs get some (the EDA one already had plenty).",
                tags = { "BOMBER", "TECH1" },
                notTags = { "EDA" },
                expect = { ["weapons.1.damageRadius"] = 0 },
                set = { ["weapons.1.damageRadius"] = 3 },
            },
            {
                why = "And T3 bombs a little more.",
                tags = { "BOMBER", "TECH3" },
                expect = { ["weapons.1.damageRadius"] = 0 },
                set = { ["weapons.1.damageRadius"] = 4 },
            },
        },
    },
    {
        key = "defences",
        changes = {
            {
                why = "T1 point defences saw 15 but shot 25, so T1 tanks (range 20) could kill one from outside its sight. They now see their full range: still more than a tank's, less than T1 artillery's 30.",
                idPattern = "^u.s1001$",
                expect = { ["intel.visionRadius"] = 15 },
                set = { ["intel.visionRadius"] = 25 },
            },
            {
                why = "With nothing like walls to hide behind, a T1 point defence died to its own value in T1 tanks while killing half of them. More health makes it a real answer to T1 tanks and an early commander, while T1 artillery still outranges it.",
                idPattern = "^u.s1001$",
                expect = { ["defence.health.max"] = 1000 },
                set = { ["defence.health.max"] = 1900, ["defence.health.value"] = 1900 },
            },
            {
                why = "T2 point defences likewise (saw 25, shoot 50).",
                idPattern = "^u.s2001$",
                expect = { ["intel.visionRadius"] = 25 },
                set = { ["intel.visionRadius"] = 50 },
            },
            {
                why = "T3 point defences likewise (saw 30, shoot 70).",
                idPattern = "^u.s3001$",
                expect = { ["intel.visionRadius"] = 30 },
                set = { ["intel.visionRadius"] = 70 },
            },
        },
    },
    {
        key = "units",
        changes = {
            {
                why = "The Chosen Jager (T2 raider) beat every other T2 raider and its own value in T1 tanks with most of its force left. Less health and damage: T2 should beat its own value in T1, but not with most of its force left.",
                ids = { "ucl2002" },
                expect = { ["defence.health.max"] = 2173, ["weapons.1.damage"] = 50.4 },
                set = { ["defence.health.max"] = 2100, ["defence.health.value"] = 2100, ["weapons.1.damage"] = 47 },
            },
            {
                why = "T1 tanks: the Chosen Gladius won 10 against 10 Pumas or Gimlets every time with almost half left, being tougher and cheaper. It costs a little more and has a little less health.",
                ids = { "ucl1001" },
                round = true,
                expect = { ["defence.health.max"] = 307 },
                set = { ["defence.health.max"] = 300, ["defence.health.value"] = 300 },
                scale = { ["economy.cost.alloys"] = 1.07, ["economy.cost.energy"] = 1.07 },
            },
            {
                why = "The EDA Puma gets more health.",
                ids = { "uel1001" },
                expect = { ["defence.health.max"] = 275 },
                set = { ["defence.health.max"] = 320, ["defence.health.value"] = 320 },
            },
            {
                why = "The Guardian Gimlet landed the fewest shots on moving tanks (69% against 86-88%): more health and damage.",
                ids = { "ugl1001" },
                expect = { ["defence.health.max"] = 285, ["weapons.1.damage"] = 23.53 },
                set = { ["defence.health.max"] = 300, ["defence.health.value"] = 300, ["weapons.1.damage"] = 26 },
            },
            {
                why = "EDA T2 raider (Hyena): T2 raiders other than the Jager lost every fight against their own value in T1 tanks; T2 should win those. More health.",
                ids = { "uel3002" },
                expect = { ["defence.health.max"] = 1428 },
                set = { ["defence.health.max"] = 1800, ["defence.health.value"] = 1800 },
            },
            {
                why = "Guardian T2 raider (Torque): likewise, more damage.",
                ids = { "ugl2002" },
                expect = { ["weapons.1.damage"] = 51.39 },
                set = { ["weapons.1.damage"] = 80 },
            },
            {
                why = "EDA T1 artillery (Bison) cost 40 against 62 and 76 for the Chosen and Guardian ones, for the same job. Now about the Chosen cost.",
                ids = { "uel1101" },
                round = true,
                scale = { ["economy.cost.alloys"] = 1.55, ["economy.cost.energy"] = 1.55 },
            },
            {
                why = "T3 tanks move at 3.3 like T1 tanks (were 2.5-2.7), turn their hull at 90 degrees a second (was 45-60) and their guns at 180 (were 70), so they keep up with an army and track what they shoot at.",
                ids = { "uel3001", "ucl3001", "ugl3001" },
                set = { ["movement.speed"] = 3.3, ["movement.acceleration"] = 3.3, ["movement.rotationSpeed"] = 90, ["weapons.*.aimControllers.*.yawSpeed"] = 180, ["weapons.*.aimControllers.*.pitchSpeed"] = 90 },
            },
            {
                why = "The Guardian Nitro (T3 raider) moves at 4.5 (was 3.3), faster than T1 raiders (4), with its gun turning at 180 degrees a second (was 70), but has 1500 health (was 2250): one EDA T3 tank volley kills it, so it raids rather than fights T3 armies. Its range (24) stays well short of a T3 tank's (34).",
                ids = { "ugl3002" },
                expect = { ["defence.health.max"] = 2250 },
                set = { ["movement.speed"] = 4.5, ["movement.acceleration"] = 4.5, ["weapons.*.aimControllers.*.yawSpeed"] = 180, ["weapons.*.aimControllers.*.pitchSpeed"] = 90, ["defence.health.max"] = 1500, ["defence.health.value"] = 1500 },
            },
            {
                why = "T4 brawlers walk at 3.5 (were 2), turn at 70 degrees a second (were 40) and swing their main guns at 90 (were 45): the EDA Behemoth, the Chosen Ares and the Guardian beam bot fight at 30-35 range, so they have to be able to close in.",
                ids = { "uel4002", "ucl4001", "ugl4001" },
                expect = { ["movement.speed"] = 2 },
                set = { ["movement.speed"] = 3.5, ["movement.acceleration"] = 3.5, ["movement.rotationSpeed"] = 70 },
            },
            {
                why = "Ares main and side guns swing at 90 degrees a second (were 45).",
                ids = { "ucl4001" },
                set = { ["weapons.1.aimControllers.*.yawSpeed"] = 90, ["weapons.2.aimControllers.*.yawSpeed"] = 90, ["weapons.3.aimControllers.*.yawSpeed"] = 90 },
            },
            {
                why = "Behemoth and Guardian beam bot main guns swing at 90 degrees a second (were 45).",
                ids = { "uel4002", "ugl4001" },
                set = { ["weapons.1.aimControllers.*.yawSpeed"] = 90 },
            },
            {
                why = "The Chosen Djinn (hover, anti-air too) and the Athena mobile shield move at 3 (were 2) and turn at 60 (were 40): the shield has to keep up with the army under it.",
                ids = { "ucl4003", "ucl4401" },
                expect = { ["movement.speed"] = 2 },
                set = { ["movement.speed"] = 3, ["movement.acceleration"] = 3, ["movement.rotationSpeed"] = 60 },
            },
            {
                why = "Long-range and heaviest T4s move at 2.5 (were 2) and turn at 50 (were 40): the EDA Centaur railgun sniper, the Guardian Quasar artillery, the Chosen Tripod (100 range) and the Chosen big bot (100000 health, the most firepower). They stay the slowest, as snipers and artillery should.",
                ids = { "uel4001", "ugl4011", "ucl4002", "ucl4004" },
                expect = { ["movement.speed"] = 2 },
                set = { ["movement.speed"] = 2.5, ["movement.acceleration"] = 2.5, ["movement.rotationSpeed"] = 50 },
            },
        },
    },
}
