# Findings for the Sanctuary developers

Issues found while making the Balance Patch (game 0.0.1.20, build 25474094).
Each was measured in game with scripted unit tests unless it says otherwise.
"Fixed in the mod" means the patch works around it with data; the rest need
the game itself.

## Units that never deal damage

- **EDA T1 bomber (uea1001), EDA T3 bomber (uea3001), Guardian T1 bomber
  (uga1001), Guardian T3 bomber (uga3001)** dealt no damage at all in 40 to
  60 s against stationary T1 tanks, with a scout giving vision. The Chosen T1
  bomber (uca1001) in the same test landed 77% of its damage.
  `common/units/availableUnits.lua` marks all four `BONE_MISSMATCH`, so
  their weapon bones are probably missing from the model. They are still in
  the build menus for players (AvailableUnits only steers the AI).

## Data that looks like a mistake

- **EDA T3 anti-air fighter (uea3201)**: its second weapon has
  `layerTargetLimits = { "Land", "WaterSurface" }`, so it shoots the ground.
  The other factions' T3 fighters have both weapons on air. Fixed in the mod.
- **Guardian TALEN gunship (uga3011)**: built by T3 air factories at T3 cost
  and stats, but tagged `TECH1` and named "Tier 1: Gunship". Fixed in the mod.
- **Chosen aircraft cost 10 energy per alloy**, EDA and Guardian aircraft 20,
  with identical stats, so Chosen air is about a third cheaper. Fixed in the
  mod.
- **Every point defence and anti-air structure sees half its range or less**:
  T1 point defence vision 15, range 25; T2 25 / 50; T3 30 / 70; T1 anti-air
  15 / 40. T1 tanks (vision 20, range 20) killed a lone T1 point defence
  without losing a tank, because it could not see them. Point defences fixed
  in the mod.
- **The EDA and Guardian commanders' missile (pei141)** slows to speed 5 at
  0.3 s and only speeds up at 2.7 s, so it loiters for about three seconds.
  Against moving T1 tanks it landed 23-43% of its damage, against the Chosen
  commander's 72%; much of the rest hit nothing, or hit targets already dead.
  Fixed in the mod (faster stages, `LeadTargetEntity`).
- **Artillery never leads**: every indirect-fire weapon has
  `leadTarget = false`. Against tanks crossing in a straight line, every
  artillery unit landed 0%. With `leadTarget = true` (the aim solver already
  supports it for high arcs) T1 artillery landed 7-66%. Fixed in the mod.
- **Bombers never lead either**, and most bombs have no splash, so a bomb that
  lands beside its target does nothing (`collisionUpdate.lua` only applies
  splash with `damageRadius > 0`). The Chosen T1 bomber landed 77% on still
  tanks and 0% on moving ones; with lead and a 3 splash radius, 84-104%.
  Fixed in the mod.

## Economy

- Generators and extractors are exactly linear across tiers: T1, T2 and T3
  generators give the same energy per cost and per build time, and T1 has ten
  times the health per energy. Not a bug, but it makes teching up pointless.
