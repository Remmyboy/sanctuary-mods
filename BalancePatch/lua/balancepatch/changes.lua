-- Every change the Balance Patch makes, by lobby section. Plain data, no
-- code: tools/preview.mjs reads this file too, to print before/after tables.
--
-- A change: why (shown in the preview), a selector (ids, idPattern, tags,
-- notTags; all must hold), then set = { path = value } and/or
-- scale = { path = factor } (round = true rounds the results), and
-- expect = { path = value } for what the game had when the change was made.
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
        key = "economy",
        changes = {
            {
                why = "Less commander income, so going straight to T2 off the commander alone takes longer and expanding matters more.",
                tags = { "COMMAND" },
                expect = { ["economy.production"] = { alloys = 5, energy = 50 } },
                set = { ["economy.production"] = { alloys = 2, energy = 20 } },
            },
            {
                why = "T1 extractors make up the commander's lost alloy income, but only where you have taken the map.",
                idPattern = "^u.s1601$",
                expect = { ["economy.production.alloys"] = 1 },
                set = { ["economy.production.alloys"] = 2 },
            },
            {
                why = "T2 extractors pay back their upgrade faster than before (240 s against 400 s) instead of being the worse deal.",
                idPattern = "^u.s2601$",
                expect = { ["economy.production.alloys"] = 4 },
                set = { ["economy.production.alloys"] = 7 },
            },
            {
                why = "T3 extractors likewise (upgrade pays back in about 360 s, was 670 s).",
                idPattern = "^u.s3601$",
                expect = { ["economy.production.alloys"] = 10 },
                set = { ["economy.production.alloys"] = 18 },
            },
            {
                why = "T2 generators give 1.5x the energy per cost of T1 (were exactly equal).",
                idPattern = "^u.s2611$",
                expect = { ["economy.production.energy"] = 200 },
                set = { ["economy.production.energy"] = 300 },
            },
            {
                why = "T3 generators give 2x the energy per cost of T1 (were exactly equal).",
                idPattern = "^u.s3611$",
                expect = { ["economy.production.energy"] = 1000 },
                set = { ["economy.production.energy"] = 2000 },
            },
            {
                why = "T1 generators had 10x the health per energy of T2 and T3 ones.",
                idPattern = "^u.s1611$",
                expect = { ["defence.health.max"] = 800 },
                set = { ["defence.health.max"] = 500, ["defence.health.value"] = 500 },
            },
        },
    },
    {
        key = "costs",
        changes = {
            {
                why = "Land and naval units cost more alloys and less energy (energy per alloy 10 -> 6) for the same total value.",
                tags = { "MOBILE" },
                notTags = { "AIR", "COMMAND", "CONSTRUCTION", "COMBAT_ENGINEER" },
                round = true,
                scale = { ["economy.cost.alloys"] = 1.25, ["economy.cost.energy"] = 0.75 },
            },
            {
                why = "Chosen aircraft cost half the energy of EDA and Guardian ones with the same stats. Now 20 energy per alloy like theirs.",
                tags = { "AIR", "CHOSEN" },
                round = true,
                scale = { ["economy.cost.energy"] = 2 },
            },
        },
    },
    {
        key = "engineers",
        changes = {
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
                set = { ["weapons.1.damageRadius"] = 1.25 },
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
                set = { ["defence.health.max"] = 1800, ["defence.health.value"] = 1800 },
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
                why = "The Chosen Jager (T2 raider) beat every other T2 raider and its own value in T1 tanks with most of its force left. Less health and damage.",
                ids = { "ucl2002" },
                expect = { ["defence.health.max"] = 2173, ["weapons.1.damage"] = 50.4 },
                set = { ["defence.health.max"] = 1900, ["defence.health.value"] = 1900, ["weapons.1.damage"] = 45 },
            },
            {
                why = "T1 tanks: the Chosen Gladius won 10 against 10 Pumas or Gimlets every time with almost half left, being tougher and cheaper. It costs a little more.",
                ids = { "ucl1001" },
                round = true,
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
                why = "EDA T2 raider (Hyena): T2 raiders other than the Jager lost every fight against their own value in T1 tanks. More health.",
                ids = { "uel3002" },
                expect = { ["defence.health.max"] = 1428 },
                set = { ["defence.health.max"] = 1550, ["defence.health.value"] = 1550 },
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
        },
    },
}
