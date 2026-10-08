# Findings for the Sanctuary developers

Core issues found while making the Balance Patch (game 0.0.1.20, build
25474094): bugs in the game's data or units, not balance opinions. Each was
measured in game with scripted host-side tests (spawned units, the game's own
orders, damage counted in `HostUnit:TakeDamage`, weapon state read from
`HostWeapon`). "Worked around in the mod" means the Balance Patch fixes it
with data; the rest need the game.

## Bombers that never deal damage

Tested with attack-move and attack-unit orders against T1 tanks (which can't
shoot back at aircraft) and against T1 generators, for 40-90 s. In the same
test 4 Chosen T1 bombers (uca1001) killed 6 tanks in 33 s.

| Bomber | Result | Cause |
| --- | --- | --- |
| Guardian T1 Inertia (uga1001) | never fires | the model has none of the bones the weapon names (`Turret01_Muzzle01`, `Turret01_Pitch01`, `Turret01`), and the weapon's muzzle list is empty |
| Guardian T3 Impulse (uga3001) | never fires | the same: no `Turret01_Muzzle01`, `Turret01_Pitch01` or `Turret01_Yaw01`, empty muzzle list |
| Chosen T3 Meteor (uca3001) | never fires | the same (marked `NO_MODEL` in `availableUnits.lua`) |
| EDA T1 Vulture (uea1001) | aims rarely; never hits | its aim was on target on 3 of 400 ticks: the bay has `yawMin/yawMax = 0`, `projectileSpeed = 0.0001`, `aimTolerance = 2` at 60 range, and the model has no `Turret01_Pitch01` or `Turret01_Yaw01`. With the aim opened up (tolerance 180, speed 20, range 12) it releases every reload, but its bombs still never damage anything, with its own bomb (pxd003) or the working pxd002 |
| EDA T3 Condor (uea3001) | fires constantly, never hits | `projectileLifetime = 2` at projectile speed 20 is about 40 range, but the weapon fires from up to 90. **Worked around in the mod** (lifetime 5): one Condor then killed 10 T1 tanks in 7-65 s |

All bombers have the same short `projectileLifetime` against their range
(T1: 2 s, range 60). The mod lengthens it for all of them.

`common/units/availableUnits.lua` already marks most of these
`BONE_MISSMATCH` or `NO_MODEL`, but that list only steers the AI: players can
still build them.

## Data that looks like a mistake

- **EDA T3 anti-air fighter (uea3201)**: its second weapon has
  `layerTargetLimits = { "Land", "WaterSurface" }`, so one of its two
  launchers shoots the ground. The other factions' T3 fighters have both on
  air. Worked around in the mod. (It is marked `BONE_MISSMATCH` too, but it
  fires and hits fine.)
- **Guardian TALEN gunship (uga3011)**: built by T3 air factories at T3 cost
  and stats, but tagged `TECH1` and named "Tier 1: Gunship". Worked around in
  the mod.
- **Chosen aircraft cost 10 energy per alloy**, EDA and Guardian aircraft 20,
  with the same stats, so Chosen air is about a third cheaper. Worked around
  in the mod.
- **EDA and Guardian commander missile (pei141)**: its first stage drops to
  speed 5 at 0.3 s and the next only speeds up at 2.7 s, so the missile
  loiters for about three seconds. On moving T1 tanks it landed 23-43% of its
  damage, against the Chosen commander's 72%. Worked around in the mod (fast
  stages, `LeadTargetEntity`): 83-88%.
- **Nothing that lobs leads its target**: every indirect-fire weapon and every
  bomber has `leadTarget = false`, so against a target moving in a straight
  line all artillery landed 0%. The aim solver already supports leading high
  arcs (`UpdateAimControllersJob`); with `leadTarget = true` T1 artillery
  landed 7-66%. Worked around in the mod.
- **Most bombs have no splash** (`damageRadius = 0`), and a projectile without
  splash that hits the ground does nothing (`collisionUpdate.lua`), so a bomb
  landing beside its target is wasted.

## Economy

- Generators are exactly linear across tiers: T1, T2 and T3 give the same
  energy per cost and per build time, and T1 has ten times the health per
  energy. With nothing gained per tier, teching power is only a space and
  click saving.
