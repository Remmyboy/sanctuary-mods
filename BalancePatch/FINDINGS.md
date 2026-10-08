# Findings for the Sanctuary developers

Core issues found while making the Balance Patch (game 0.0.1.20, build
25474094): bugs in the game's data or units, not balance opinions. Each was
measured in game with scripted host-side tests (spawned units, the game's own
orders, damage counted in `HostUnit:TakeDamage`, weapon state read from
`HostWeapon`). "Worked around in the mod" means the Balance Patch fixes it
with data; the rest need the game.

## One broken weapon stops all retargeting

`HostWeapon:SelectTarget` starts with `self.muzzleGroups[1][1].id`. A weapon
whose muzzle bones aren't in its model has no muzzle groups, so this throws:
the Guardian T1 and T3 bombers (uga1001, uga3001), the Chosen T3 bomber
(uca3001), and the two units with no model (uel2002, uga3011, below). The
error is thrown inside `TargeterManager.UpdateTargeting`'s loop, which ends
the update for every weapon queued after it. The queue is only cleared at
the end, so the broken weapon stays in it and throws again every tick. While
one of these units has an enemy in range, weapons behind it in the queue
never pick a new target. One vanilla test session (19 one-minute anti-air
tests with Guardian T1 bombers as the targets) logged 11,966 of these errors
(`weaponsBaseClass.lua:305: attempt to index a nil value`). Worked around in
the mod: a weapon with no muzzles picks no target.

## Bombers that never deal damage

Tested with attack-move and attack-unit orders against T1 tanks (which can't
shoot back at aircraft) and against T1 generators, for 40-90 s. In the same
test 4 Chosen T1 bombers (uca1001) killed 6 tanks in 33 s.

| Bomber | Result | Cause |
| --- | --- | --- |
| Guardian T1 Inertia (uga1001) | never fires | the model has `Turret01` but not `Turret01_Muzzle01` or `Turret01_Pitch01`, and the weapon's muzzle list is empty |
| Guardian T3 Impulse (uga3001) | never fires | the same: no `Turret01_Muzzle01`, `Turret01_Pitch01` or `Turret01_Yaw01`, empty muzzle list |
| Chosen T3 Meteor (uca3001) | never fires | the same (marked `NO_MODEL` in `availableUnits.lua`) |
| EDA T1 Vulture (uea1001) | aims rarely; never hits | its aim was on target on 3 of 400 ticks: the bay has `yawMin/yawMax = 0`, `projectileSpeed = 0.0001`, `aimTolerance = 2` at 60 range, and the model has no `Turret01_Pitch01` or `Turret01_Yaw01`. With the aim opened up (tolerance 180, speed 20, range 12) it releases every reload, but its bombs still never damage anything, with its own bomb (pxd003) or the working pxd002 |
| EDA T3 Condor (uea3001) | fires constantly, never hits | `projectileLifetime = 2` at projectile speed 20 is about 40 range, but the weapon fires from up to 90. **Worked around in the mod** (lifetime 5): one Condor then killed 10 T1 tanks in 7-65 s |

All bombers have the same short `projectileLifetime` against their range
(T1: 2 s, range 60). The mod lengthens it for all of them.

`common/units/availableUnits.lua` already marks most of these
`BONE_MISSMATCH` or `NO_MODEL`, but that list only steers the AI: players can
still build them.

## Units with no model that players can build

The EDA T2 raider (uel2002, "EDAT2FastUnit") and the Guardian TALEN gunship
(uga3011) have no skeleton at all (`Engine.GetSkeletonBones` returns 0
bones), so none of their weapon bones exist. Both are tagged
`BUILDABLE_BY_T2/T3_FACTORY`, so players can build them (`availableUnits.lua`
marks them `NO_MODEL` and only the AI skips them). In 30 s against three T1
tanks neither came on target once and both dealt 0 damage; the Guardian T2
raider and the EDA T3 gunship landed 93% in the same test. Data can't fix
this.

## Chosen T2 point defence shoots the ground

The Chosen T2 point defence (Redoubt, ucs2001) has two problems:

- Its barrel bone rests about 55 degrees nose-down. The model's
  `Turret01_Pitch01` has a local rotation of (0.326, 0.326, -0.627, 0.627)
  where the T3 point defence has a clean (0, 0, -0.707, 0.707), so its muzzle
  points 55 degrees below level at rest. The aim controller corrects for the
  muzzle's real direction (`TxBothSystem_RotateAimControllerBones`), but only
  within the pitch limits, and `pitchMax = 45` can't lift it to level. It
  aims at the ground and never reports on target.
- Its `yawBone = "Turret01"` isn't in the model, which has `Turret01_Yaw01`.

Its aim never came on target in 400 ticks and it landed 0% at 20, 35 and 47
range; the Guardian and EDA T2 point defences landed 48% and 94% in the same
test. A scan of every unit's aim controllers against its skeleton found no
other direct-fire turret that can't reach level. **Worked around in the mod**
(`pitchMax = 100`, `defaultPitchAdjustment = 55`, `yawBone =
"Turret01_Yaw01"`): on target 292 of 301 ticks, 90% at 20-47 range, and it
beats 15 T1 tanks 2-0 like the Guardian one.

## Aircraft fly inside shield bubbles

Gunships hover at `preferredAltitude = 8.5` and bombers fly at 10. A T3
shield's bubble (radius 20, sunk 5) reaches 15 up, so both fly inside it.
A shield is a hollow sphere collider, so it only stops a shot whose path
crosses its surface (`ProcessRayCollisionEvent`, `HostBeam:Fire`), and area
damage only checks a line from the impact to each unit (`IsShieldBlocking`):
from inside, nothing crosses. Against a generator under a powered EDA T3
shield (40 s, shield never dropped), two EDA T3 gunships landed 90% of their
damage, three Chosen T2 gunships (beams) 91%, three Chosen T1 bombers 32%.
T2 gunships under a T2 shield (top at 9) were blocked.

Flying them higher (18) kept them out of the bubble, but anti-air then hit
them much less (EDA T1 fighters on T2 gunships: 74% to 24%), so the mod
doesn't change heights. **Worked around in the mod** with a hit rule: an
aircraft's projectile that starts inside an enemy bubble and lands inside
it, or its beam fired from inside one, hits the shield. The same three tests
then landed 0%, and the shield took the damage (the two T3 gunships broke it
at 38 s). Land units are unchanged: they stop at weapon range at the edge of
the bubble, and their damage through it was the same as vanilla (15%). A
real fix might be shields that keep enemy aircraft out.

## Data that looks like a mistake

- **EDA T3 anti-air fighter (uea3201)**: its second weapon has
  `layerTargetLimits = { "Land", "WaterSurface" }`, so one of its two
  launchers shoots the ground. The other factions' T3 fighters have both on
  air. Worked around in the mod. (It is marked `BONE_MISSMATCH` too, but it
  fires and hits fine.)
- **Guardian TALEN gunship (uga3011)**: built by T3 air factories at T3 cost
  and stats, but tagged `TECH1` and named "Tier 1: Gunship". Worked around in
  the mod (it still can't fire: it has no model, above).
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
- **T3 anti-air towers that barely hit** (one tower against three weaving
  Chosen T1 bombers at 35, 60 s): the Guardian one (ugs3201) 0%, the Chosen
  one (ucs3201) 9%, the EDA one 39%. The Guardian tower aims its slow
  (speed 20) shells straight (`solverType = "NoArc"`) though they fall, and
  missed even a hovering gunship (3%). The Chosen missile (pca341) turns at
  10 degrees a second (the EDA one at 60) and lives 2 s, about 45 of its 60
  range. The EDA T2 mobile anti-air's shells fall from 71% to 1% if they lead,
  so not every weapon gains from leading. Partly worked around in the mod:
  Guardian 18%, Chosen 16-18%, EDA 44-50%.
- **Most bombs have no splash** (`damageRadius = 0`), and a projectile without
  splash that hits the ground does nothing (`collisionUpdate.lua`), so a bomb
  landing beside its target is wasted.

## Economy

- Generators are exactly linear across tiers: T1, T2 and T3 give the same
  energy per cost and per build time, and T1 has ten times the health per
  energy. With nothing gained per tier, teching power is only a space and
  click saving.
