# Balance tool

Judges a ruleset (vanilla, the Balance Patch, or a proposed `changes.lua`) without
starting the game: templates as the game would load them, an economy simulation by
the game's rules, and a check against the pacing targets in
[`../../REDESIGN-PLAN.md`](../../REDESIGN-PLAN.md).

    node BalancePatch/tools/balance/balance.mjs eco 0.1.1
    node BalancePatch/tools/balance/balance.mjs sim 0.1.1 --map "~TEAM-1v1_Desert_512_89065" --style spam
    node BalancePatch/tools/balance/balance.mjs rush vanilla --map "~TEAM-1v1_Tropical_256_92536"
    node BalancePatch/tools/balance/balance.mjs diff vanilla 0.1.1 0.1.1-startbank

Needs the game installed, and .NET 8 (the dump runs through `tools/GameRef`, built
on first use).

## Pieces

| File | What it does |
| --- | --- |
| `dump.lua` | Loads every unit and projectile template with the game's own LuaJIT (`GameRef luarun`), applies the patch with the mod's real `patch.lua`, adds wrecks and the adjacency buffs as the game builds them, and writes JSON. Cached per ruleset in `.cache/`. |
| `parity.mjs` | Checks a dump against `preview.mjs --json`: every template it changed matches field for field, every other one is untouched. 357 templates, 0 differences on 2026-10-09. |
| `maps.mjs` | Alloy spots and spawns from the installed `.sanmap` files, and which spots a player holds in a 1v1 (nearer to its spawn than to the enemy's). |
| `sim.mjs` | One player's economy over time at the game's tick rate: build drain, the satisfaction stall, unthrottled generation, storage, the half-storage start, adjacency, upgrades at full price, walking at template speed. The rules and their file/line are listed at the top. |
| `calibrate.mjs` | Fits the stand-in player's behaviour (reaction time, power margin, expansion, engineer and factory habits) to real games. |
| `players.json` | The fitted player styles: `spam` (early factories) and `expand` (expand first). Rewritten by `balance.mjs calibrate`. |
| `variants.json` | Named rulesets: a `changes.lua`, lobby options, and rules a template can't show (`startShare`). |
| `targets.json` | The agreed pacing targets per map class (256, 512), with the reference maps. |

## What the numbers are

Everything `sim`, `check` and `diff` print is a **simulation**, not the game:

- No combat. Units are built, not fought; "alive" is built × the style's survival
  at 10:00 measured in the calibration games (spam 0.48, expand 0.68).
- No raids, reclaim, storage structures, pathing (straight lines × a path factor),
  air or navy.
- The player is a fitted stand-in. Its expansion, power and factory habits come
  from six players in three games of 2026-10-09; its teching (T2 extractors from
  8:00, a T2 factory from 9:00) is **assumed**, because those games had almost no T2.
  Numbers past 10:00 lean on that assumption.
- `rush` only times the fastest T2 factory. Whether the rusher would be overrun
  needs combat, which the sim doesn't do.

Calibration on 2026-10-09 (fitted to replays with a dev-only reader): at 10:00 the
sim was within ~15% of the real players' income and extractor counts. Units
built were within ~30% (spam reads low, one expand game reads high).

## Adding a ruleset

Copy `changes.lua` somewhere (for example `proposed/changes.lua`), add it to
`variants.json`, and run `diff` against the others. A section's lobby option is on
unless `options` turns it off.
