# Balance Patch 0.2: top-down redesign plan

Draft, 2026-10-09. Plan only: no changes to `changes.lua` yet. Game build
25474094 (0.0.1.20), patch 0.1.1 (main 619b55d).

Labels: **[code]** means a game rule read in the game's Lua, with file and
line (paths under `%LOCALAPPDATA%\SanctuaryRef\25474094\lua\`). **[data]**
means read from templates or map files. **[calc]** means a number I worked
out myself from those. **[FAF]**, **[BAR]** and **[ZK]** mean read from
those games' source on GitHub (links at the end). Nothing in this document
has been checked in game.

---

## 0. Summary

**What's wrong, in one line each:**

1. **Vanilla:** the commander makes as much as 5 extractors, so taking
   territory barely matters early and a T2 rush can run on commander
   income alone.
2. **0.1.0 / 0.1.1:** the income moved onto the map, but each spot's yield
   doubled. Sanctuary's ladder maps have about twice as many spots per
   player as FAF's classic 1v1 maps, so the income ceiling on a medium map
   roughly doubled. Units also got cheaper against income (a T1 tank is
   19.5 extractor-seconds, FAF's is 28).
3. **The spot count varies a lot by map:** a player has 9 spots on Tropical
   256 and 58 on White Desert. Any single per-spot yield either starves the
   small maps or blows up the big ones, unless the upgrade tiers give small
   maps a way to grow vertically.
4. **Energy is half of Sanctuary's economy**, not a side resource as in
   FAF: units cost 10 energy per alloy, against FAF's ~5. Power stalls are
   the usual early failure; the 2026-09-12 replay review found exactly that.
5. **The combat tiers don't hold up a long T2.** T2 land is thin (raiders,
   AA, one support unit per faction, no main tank). Vanilla T3 tanks have
   about half the square-law value per cost of T1 tanks.

**What I propose:** design pillars and pacing targets (§2–3, with
questions for you), then one balance tool (§4) that runs the real patch
engine through the game's LuaJIT. It simulates build orders on real map
spot data, rates units per value, and is calibrated against your replays.
The new balance then goes in three stages (§5), each predicted by the tool
before you play it.

**First step:** see §8. Build the LuaJIT dump and the replay summariser,
then reproduce the "100 units" game from the 2026-10-08 replays before
trusting the sim with anything else.

---

## 1. The game as shipped: the rules this builds on

### Economy rules [code]

| Rule | Where |
| --- | --- |
| Builder drain per second = cost × Σ(build power × adjacency discount) ÷ buildTime, per resource | `host/systems/resourceEntity.lua:176-191` (AddBuilder), `:244-263` (RecalculateBuildDrain) |
| Stall works like FA: each resource's satisfaction = min((stored + income) ÷ request, 1); a build runs at the lowest satisfaction of the resources it uses | `host/systems/economy.lua:133-140, 160-170, 190-194` |
| Extractors, generators and the commander are "generation" (never throttled). A unit with production *and* upkeep (the Alloy Furnace) is "production", throttled by satisfaction | `host/units/unitsClasses/unitsBaseClass.lua:612-650`, `economy.lua:143-158` |
| Overflow above storage is lost | `economy.lua:196-200` |
| Start: the commander adds its storage and gives half of it (250 A / 2500 E). `economy.initial` is ignored | `host/units/unitsClasses/unitsDefault.lua:1394-1403` |
| Commander death destroys the whole army (assassination) | `unitsDefault.lua:1409-1420` |
| Adjacency discounts stack additively with **no floor**: discount = 1 + Σ extra. Each adjacent extractor gives factories and engineering stations −10% alloys; each T1/T2/T3 generator gives −2.5/−10/−15% energy. Seven adjacent T3 generators would make a factory's energy cost negative (not tested in game) | `host/systems/buffs.lua:36-58, 85-95`, `host/systems/adjacencyBuffs.lua:3-50` |
| Adjacency data exists for T1/T2/T3 *Alloy Fabricators*, but only a T3 Alloy Furnace unit exists: a hint that the devs planned cheaper converters | `adjacencyBuffs.lua:93-113` |
| Wrecks: half the unit's alloys, no energy, harvest time = build time; only from units at least 50% built and not overkilled. Generated from the *patched* template | `common/systems/templateLoader.lua:450-458, 262-291`; `unitsBaseClass.lua:891-907` |
| Reclaim exists (a host order class) | `host/managers/orders/orderClasses/hostReclaimOrder.lua` |
| No energy spots: the energy resource is commented out, so alloys are the only territorial resource | `common/resourceSpot.lua` |
| No tech gate: a T1 factory upgrades straight to T2, T2 to T3. The tech centres are DEMO_UI_ONLY. Naval factories are DEMO_UI_ONLY too, so naval is out of scope | [data] tags |
| A per-army income-and-build multiplier with a per-minute ramp exists. It scales generation and construction, is set through `lobbyOptions.economySettings`, and is used only by the AI debug options today. A possible match-length lever | `economy.lua:13-19, 114-123, 160-170`; `common/gameUtils.lua:319`; `AI/_LobbyOptions.lua:51` |
| Upgrades charge the target's full cost and build time, with no rebate. This is the unit-db's reading (`src/lib/economy.ts`, citing `unitsDefault.lua:188`); **I haven't re-read it** | — |
| Tick rate 10 | `host/generated/lua/constants.lua:6` |

### The AI [code]

The stock AIs (`AI/mods/AI-Sanctuary-Rush`, `AI-T1Rush`, `AI-T2Rush`) mostly
react to storage levels and trends (`MoreThanEcoStorageLevel`,
`MoreThanEcoTrend`), so they adapt to new numbers by themselves. The
absolute knobs that need checking are:

- energy-to-alloy income ratios of 13 to 20 (`LessThanEnergyToResourceRatioIncome`;
  BalancePatch scales these by 0.7);
- `MoreThanEcoIncome {30,-1}`;
- `MoreThanEcoStorageAmount {150,1500}`;
- `MoreThanAlloyExtractorFairShareCount {40}`.

### Map spots [data]

Alloy markers are in each `.sanmap` (`markers.Alloys.transforms`):

| 1v1 ladder map | Spots | Per player | Within 80 of spawn |
| --- | --- | --- | --- |
| ~TEAM-1v1_Tropical_256_92536 / _47940 | 18 | 9 | 5 |
| There Is Time | 21 | 10.5 | 6 |
| ~TEAM-1v1_Desert_512_23678 | 36 | 18 | 4 |
| ~TEAM-1v1_Tropical_512_11446 | 46 | 23 | 6 |
| ~TEAM-1v1_Desert_512_89065, Forest_512_28589 | 52 | 26 | 6-7 |
| Two Step Shuffle (4 spawns, played 1v1) | 52 | 26 | 7-8 |
| White Desert (4 spawns, played 1v1) | 116 | 58 | 8-11 |
| *FAF classics in the install: Winter Duel, Williamson's Bridge, Theta Passage, Canis River* | 18-28 | 9-14 | 5-9 |

The generated 512 maps give each player 18–26 spots, about twice FAF's
classic 1v1 maps. These are marker counts: I haven't checked that every
spot can be reached.

### Numbers: vanilla, 0.1.1, FAF

| | Vanilla | 0.1.1 | FAF (UEF) |
| --- | --- | --- | --- |
| Commander income | 5 A, 50 E | 3 A, 40 E | 1 M, 20 E |
| Commander build power / start bank | 5 / 250 A, 2500 E | same | 10 / storage 650 M, 3900 E |
| T1 extractor | 50 A + 500 E → 1 A/s | 50 A + 400 E → 2 A/s | 36 M + 360 E → 2 M/s (upkeep 2 E/s) |
| T2 / T3 extractor yield | 4 / 10 | 6 / 16 | 6 / 18 |
| T1 / T2 / T3 generator | 10 / 200 / 1000 E/s | 15 / 400 / 2700 | 20 / 500 / 2500 |
| T1 factory | 150 A, BP 10 | same | 240 M, BP 20 |
| T1 engineer | 75 A + 750 E, BP 5 | same cost, HP 750 → 300 | 52 M + 260 E, BP 5 |
| T1 tank | ~30 A + 300 E, bt 120 | ~39 A + 233 E | Striker 56 M + 266 E, bt 300 |

### Derived ratios [calc]

| Ratio | Vanilla | 0.1.1 | FAF |
| --- | --- | --- | --- |
| Commander income, in T1 extractors | 5 | 1.5 | 0.5 |
| T1 extractor payback, alloys only | 50 s | 25 s | 18 s |
| Extractor plus the generator share to spend its output on land units, alloys only | 100 s | 45 s | 40 s |
| T2 extractor upgrade payback (full price ÷ extra yield) | 200 s | 150 s | 225 s |
| T3 extractor upgrade payback | 333 s | 200 s | 383 s |
| Generator energy per second, per alloy of cost, T1 / T2 / T3 | 0.2 / 0.2 / 0.2 | 0.3 / 0.4 / 0.54 | 0.27 / 0.42 / 0.77 |
| Energy per alloy in units | 10 (air 20 for EDA/Guardians) | land 6, air 20 | Striker 4.75, Pillar 5, Titan 10.9 |
| T1 tank, in extractor-seconds | 31 | 19.5 | 28 |
| T1 factory on T1 tanks: drain, and extractors to feed it | 2.6 A/s, 2.6 | 3.25 A/s, 1.6 | 3.7 M/s, 1.9 |
| Build-power drain per BP on T1 tanks / on structures | 0.25 / 1.0 A/s | same | 0.19 / 0.6 M/s |
| Highest T1 income per player (all spots + commander): Tropical 256 / medium (23 spots) / White Desert | 14 / 28 / 63 | 21 / 49 / 119 | — |

One reading of this table: 0.1.1's extractor payback is close to FAF's.
What differs is how many spots there are and how cheap units are against
income, so the ceiling, not the speed of the loop, is what blew up. On the
spots alone, vanilla's 1 alloy per spot is about right for this map pool;
vanilla's problems were the commander and cheap tech.

### Units per value [calc, from sanctuary-unit-db's DPS derivation of vanilla]

Square-law strength per value² (HP × DPS ÷ value², value = A + E/10):

| Units | Strength per value² |
| --- | --- |
| T1 tanks | 1660–2230 |
| T2 raiders | 1310 (Torque) – 3570 (Jager) |
| T3 tanks | 735–1170 |
| T1 artillery | 3430–12170 (EDA Bison) |

The commander has 14–16k HP and 90–100 DPS at range 22: the HP of about
50 T1 tanks and the DPS of 4. Vanilla T3 needs its range (34 against 20)
to beat its value in T1.

---

## 2. Design pillars (proposal: for you to accept, change or cut)

Each pillar says what it borrows and what in Sanctuary differs.

**P1. Territory is the income.** Alloys come from spots. The commander is a
starter motor, not an economy.
- *Borrowed:* FAF's ACU makes half an extractor's worth, so every spot
  matters.
- *Differs:* Sanctuary starts you at half the commander's storage, and the
  commander builds at 5, not 10. So the opening has to be paid for by a
  bigger start bank, not by commander income. That bank can come from
  commander storage (the half rule then gives it natively, with no gift
  append) rather than from 0.1.1's one-off gift.

**P2. Energy is the base's resource and the tech resource.**
- Energy has no spots, so it is what you invest in at home. Land is
  alloy-heavy (spots, map control); air, shields and tech upgrades are
  energy-heavy.
- Adjacency (generators around factories, extractors beside forward
  factories) rewards compact, deliberate bases.
- *Borrowed:* BAR treats energy as convertible into metal, and FAF has
  fabricators: the late game gets a converter valve (the Alloy Furnace).
- *Differs:* FAF energy is cheap. Here it stays a real constraint, but a
  predictable one: no stall traps in the first two minutes.

**P3. Growth is capped by the map and by build power, not by a runaway
reinvestment loop.**
- Set a payback target per tier, and an "income at full T1 expansion"
  target per map class.
- On small maps the T2/T3 extractor upgrades are how you keep growing
  (vertical); on big maps it's taking more spots (horizontal).
- *Borrowed:* FAF's payback ladder (18 s / 225 s / 383 s).
- *Differs:* twice the spots per player, and a much wider range of spot
  counts between maps.

**P4. Tech is a priced choice, not a gate.**
- There's no HQ, so T2 timing is set by the factory upgrade's price and
  time and by how fast T2 eco pays back.
- Each tier needs a job its units do better than the tier below.
- *Differs from FAF:* there's no HQ/support-factory split, and the T2 land
  roster is thin.

**P5. Losses cost time.**
- A lost engineer and extractor should cost about one to two minutes of
  that extractor's output; a lost T2 extractor several minutes.
- Raids pay; defence (T1 point defence at spots) is a real answer.
- *Borrowed:* FAF's engineer-sniping economy (engineers have 150 HP there).

**P6. The commander anchors the early game and loses the late one.**
- It's a builder with a gun that matters for the first five minutes and is
  irrelevant to a T2 army.
- Its death is the loss condition, so it must never be safe to push it
  unescorted after about 8 minutes.

**P7. Factions differ in method, not in economy maths.**
- The economy ratios are the same for all three; the identity lives in
  units and specials.
- Proposed, from what each faction actually has [data]:
  - **EDA, industrial attrition:** factories that store energy
    (1000/2000/5000), the toughest T3 tank, rockets, artillery.
  - **Chosen, precision and drones:** beams, drone carriers, the T3
    sniper, cheaper fighters through stats rather than a cost bug.
  - **Guardians, harassment and forward building:** a T1 gunship, the
    airfield, the T3 raider, a combat engineer with BP 40, the grenade
    bot.

---

## 3. Pacing targets (agreed 2026-10-09) and what the replays show

### Your answers

- **Length by match type:** 256 maps 5–15 min; 512 maps 20–30 min; larger
  maps are decided by strategy, with no length target.
- **Army size:** about 60–80 combat units each at 10:00.
- **Maps:** balance around the medium-to-lower end. Spot-rich maps are
  allowed to be fast-expanding games.
- **T2:** no rush. Nobody really goes T2 before ~7:00, because they would be
  overrun: the factory upgrade's cost plus T2 unit cost does that, not a gate.
- **Tiers:** every tier has its day. Skipping T2 must not pay.
- **Commander:** income may drop, but it must be a whole number per second
  (1, 2, 5; not 1.5). A bigger starting bank is welcome. It must beat early
  raids; its current combat strength is fine; its death explosion stays.
- **Energy:** half the economy is fine, open to ideas.
- **Reclaim:** leave as is (wrecks vanish after 3 minutes).
- **Alloy Furnace:** fine as the late-game valve.
- **Factions:** the P7 identities are accepted.
- **AI:** not a concern for now (others are working on it).

### Measured: your three replays of 2026-10-09 [replay]

All three were 1v1s on 4-spawn 2v2 maps (Frozen 256: 18 spots each; Frozen
512: 32 each). Read offline by BalanceCal (devtools); units are counted once
complete. Its typed counts reproduce the recorded income, e.g. 15 extractors
× 2 + commander 3 = 33 A/s. Per player:

| Game | Ruleset | Length | Income at 5 / 10 min | Factories at 10 | Combat units alive at 10 (built, lost) | First T2 factory |
| --- | --- | --- | --- | --- | --- | --- |
| Frozen 512 | 0.1.1 default (2 A/s) | 14.0 | 33 / 47 and 33 / 61 | 14 and 19 | **163 (292, 129)** and **112 (276, 164)** | 9:00 (one player) |
| Frozen 256 | 0.1.1 default (2 A/s) | 17.6 | 23 / 35 and 23 / 35 | 9 and 10 | 67 (96, 29) and 80 (113, 33) | 16:00+ |
| Frozen 256 | `startbank` (1 A/s, 400/4000 start) | 10.7 | 17 / 17 and 14 / 20 | 9 and 6 | 68 (102, 34) and 73 (110, 37) | none |

What this says (my reading):

- **The 1-alloy game on 256 already hits both targets:** 10.7 minutes, and
  ~70 units alive at 10:00.
- **The 2-alloy game on 256 built no more units than the 1-alloy one,**
  with nearly twice the income. Its players sat energy-stalled (satisfaction
  0.46–0.85 in minutes 2–4) and floated alloys at the 1500 cap. Energy, not
  alloys, set the army.
- **Only the 512 game exploded:** 32 spots each at 2 A/s gave 45–65 A/s by
  9:00, 14–19 factories, and almost 300 units built per player by 10:00.
  It ended at 14 minutes, half the 512 target.
- T2 barely appears in any game: spamming T1 off cheap factories is what
  everyone did.

### FAF's maths at 10:00 [calc; FAF play assumed, not measured]

Units alive ≈ (start bank + income so far) × share spent on army × share
still alive ÷ unit cost.

- **Income:** the converted FAF 1v1 maps in the install give 9–14 spots per
  player. Assume ~10 extractors by 5:00, all ~12–13 by 7:00, and two T2
  upgrades by 10:00 (my assumption of typical play). That's ~19 mass/s at
  5:00 and ~33 at 10:00, about 11,300 mass mined, plus the 650 start.
- **Army share:** 45%, which gives ~5,400 mass.
- **Unit cost:** at 56 mass a Striker, ~95 T1-tank-equivalents built.
- **Alive:** 60–70% of those, which is **~60–65 alive**.

Your three games' army shares were 25–58% and their alive shares 40–75%,
so the same sum covers them. FAF lands inside your 60–80; real FAF games
would have fewer, larger units, because some of that mass is T2 by 10:00.

### Targets per match type

These are proposals checked against the measurements above; the sim (§4)
tests them before any game. Per player in a 1v1.

| | 256 (5–15 min) | 512 (20–30 min) |
| --- | --- | --- |
| Income at 5 / 10 / 15 / 20 min (A/s) | 12–16 / 18–24 / 25–35 / — | 12–16 / 25–30 / 40–50 / 55–70 |
| Combat units alive at 10:00 | 60–80 | 60–80 |
| Units built by 10:00 | ~100–130 | ~110–140 |
| Factories at 10:00 | 4–7 | 5–8 |
| First T2 factory | ≥ 7:00 | ≥ 7:00, common by 9–11 |
| T2 "has its day" | from ~8 min, decisive by 12 | 10–18 min |
| First T3 | rare | 16–20 min |
| Energy stall | under 10% of build time after 2:00, in a sensible build | same |

Timing and other targets still stand from the first draft: first contact
1:30–2:00, first army fight 3:00–4:00, commander in fights 3:00–7:00,
raid cost (engineer + extractor ≈ 60–90 s of that extractor's output).

**The single biggest lever** on reaching 512's length without starving 256:
the 512 game made 2× the income at 10:00 because it had 1.8× the spots. A
lower yield per spot (vanilla's 1) plus T2 extractors that pay well (so
small maps grow upwards) narrows that gap. Dearer units then set the
count. The sim sweeps both.

---

## 4. Tooling

### What exists (surveyed 2026-10-09)

| Thing | State | Verdict |
| --- | --- | --- |
| `ecosim.mjs`, `sim7.mjs`, `search.mjs`, `early*.mjs` (session 65270adc's scratchpad, still on disk) | A ~40-line FA-stall loop: scripted queues, "walk N" for travel, its own JS template parser. No spots, adjacency, combat or army value | Keep the stall loop's logic (it matches `economy.lua`); drop the files |
| `tools/buildorders/` (session 05df99fd, 2026-09-12): `.sanmap` spot and spawn extraction, a travel-aware sim, annealing search over plan strings, scoring presets | **Never committed; its worktree is deleted. Gone** | Rebuild the good parts. Spot extraction is ~20 lines (done for this plan) |
| Replay reader (dev-only, in sanctuary-devtools) | Per-minute economy numbers from replays | **Superseded 2026-10-09 by BalanceCal** (dev-only): per-minute income, extractors, factories, engineers and units built per player, for calibration. Stays private |
| BalanceLab (devtools) | Duels, accuracy, inspect, AI-vs-AI `watch` with eco snapshots | The ground truth for unit maths, and AI checks |
| sanctuary-unit-db (origin/main) | `scripts/extract.js` derives DPS faithfully (muzzle groups, salvo wraparound, beams, mirroring `SetUpWeapons`). Also `calc.ts` (build time and drain), `economy.ts` (resource roles, upgrade pricing), `ChaseSim` (kiting), and it imports `balancepatch.json` | Reuse the DPS derivation, with a parity test against the site's `units.json`. The site stays the public face |
| `tools/GameRef` + `tools/lua-check.ps1` | Already P/Invokes the game's `lua51.dll` | Add a `luarun` subcommand to run code, not just compile it |
| FAF / BAR / Zero-K community tools | No maintained FAF build-order or economy simulator found. FAF's unit db is a viewer. BAR's public `balance_algorithm` repo is team matchmaking. Zero-K's overdrive is in `unit_mex_overdrive.lua` | Nothing to port. Borrow numbers and ideas, read from source |

### The tool: `BalancePatch/tools/balance/`

#### A. `dump.lua`, run with `GameRef luarun`

- Loads every `.santp` as Lua (they *are* Lua), stubs `Import`, `Warn` and
  `Options`, and runs the real `balancepatch/patch.lua` with a chosen
  `changes.lua` and option set.
- Applies the game's wreck rule, reads `adjacencyBuffs.lua`'s data with
  stubbed `Tags`/`Buffs`, and writes JSON.
- Variants: `vanilla`, `main` (changes.lua at a git ref), `proposed`
  (working copy), or any file.
- There is then one patch engine, not a Lua one plus a JS re-implementation.
  `preview.mjs` moves onto this JSON and loses its parser.

#### B. `balance.mjs` (Node)

- **`eco`:** every eco structure and upgrade step, with cost, output,
  alloy-only payback and value payback; generator energy per cost by tier;
  build-power cost per tier; storage.
- **`units`:** DPS (ported from the unit-db, with a parity test), HP,
  range, speed and cost per role and tier.
  - Square-law strength per value², and a range-aware duel: the side with
    more range gets free volleys while the gap closes at the slower side's
    speed.
  - A role × tier matchup matrix.
  - Faction parity: the same slot across factions, flagged at more than
    10% apart.
  - Accuracy factors from BalanceLab, per weapon class against moving
    targets.
- **`sim`:** the economy and build-order simulator.
  - 0.1 s ticks, with the game's rules: drain, satisfaction stall,
    generation against production, overflow, the half-storage start,
    adjacency (counts as plan parameters, values from the dump), and
    upgrades at full price.
  - Real spots and spawns from the `.sanmap` files, with engineer travel
    at template speed.
  - Plans are strings, as in the lost tool. A greedy policy (keep power
    ahead of spend, nearest free spot next, a factory when banking,
    upgrade an extractor when its payback beats the horizon), plus
    annealing for goals such as "army value at T", "fastest T2" and
    "income at T".
  - Output per minute: income, spots, factories, BP, units by tier, army
    value, stall %, bank.
- **`diff`:** runs everything for vanilla / main / proposed and prints the
  predicted change. **`check`** scores a variant against `targets.json`
  (the §3 table), pass or fail per row and map class.

#### C. Calibration (`C:\code\sanctuary-devtools\BalanceCal`, private)

- Replays → per-minute JSON (income, spots, factories, units by tier, army
  value, losses) through the existing extractor. Only these summaries
  cross into the public tool.
- **Case 1:** the five 2026-10-08 evening games on the reworked economy
  (Tropical 256 ×3, Tropical 47940, Two Step Shuffle). That's where
  "100 units by 7 min" came from. The sim on the same map and patch has to
  reproduce the unit and factory counts within ~15%, or the model is
  missing something.
- **Case 2:** the vanilla game from the 2026-09-12 review (income table
  above).
- **Case 3:** AI-vs-AI `watch` runs in BalanceLab per variant.
- The fitted human-play parameters (idle factory share, walk overheads,
  reaction delays) are then held fixed across variants, so the diffs
  compare balance and not play.
- **Duels:** BalanceLab duel results against the tool's predictions, to
  fit the accuracy factors.

### Changes to the patch mechanism

- **Sections that only work together:** the economy, unit costs and
  engineers become one "Economy" section (one toggle), because they are
  one model and half of it on is a third economy nobody tested. The
  optional `startbank` and `costs15` sections are dropped.
- **Planned layout:** `fixes`, `economy`, `units` (cost structure by role
  and tier), `factions`, and the per-unit tuning sections (`commanders`,
  `artillery`, `air`, `defences`).
- **A non-template change kind** for adjacency values (a floor on the
  discount, or new extras): a data table in `changes.lua`, applied by an
  append that re-registers the buffs. `Buffs.RegisterBuff` overwrites by
  name (`buffs.lua:30-32`); whether a re-register after load reaches
  units is **not verified**.
- `requires = {...}` on a section, if one has to depend on another.
- **The AI ratio scale** in `lua/append/AI/AIFunctions.lua` becomes
  derived: the tool computes the AI's average energy per alloy of spend
  under the variant and writes the factor.

---

## 5. Staged rollout

Every stage has the same loop:

1. Targets.
2. Tool sweeps.
3. Pick a variant.
4. `diff` and `check` report the predicted change.
5. A BalanceLab AI-vs-AI run (does the AI still build a sane economy:
   stall %, banked resources, extractor count by minute).
6. One real game by you, with its replay calibrated back into the tool.

### Stage 1: Economy (the levers, in order)

1. **Commander:** income, storage (which also sets the start bank), build
   power.
2. **T1 extractor:** yield and cost against the "income at full T1"
   target per map class.
3. **T2/T3 extractors:** upgrade cost and yield to hit the payback ladder.
4. **Generators:** the energy-per-cost ladder; T1 health.
5. **Build power:** engineer and factory cost per BP, factory price.
6. **Tech transitions:** T2/T3 factory upgrade cost and time.
7. **Storage.**
8. **Adjacency:** a floor on the discount, and maybe stronger extractor
   adjacency as a forward-factory incentive.
9. **Converter:** the Alloy Furnace's ratio.

- *Predicted:* the per-minute table against §3 on three map classes, first
  T2 time, units at 5/7/10.
- *You test:* one medium 1v1 (a human opponent if you can, AI otherwise).
  If that passes, one game each on Tropical 256 and White Desert.

### Stage 2: Unit cost structure by role and tier

- A cost curve per role: value per (HP × DPS), adjusted for range and
  speed, per tier.
- Target: a tier beats its own value in the tier below, keeping 20–40% of
  its value (your "beats without crushing").
- Energy per alloy per domain (land 6, air 20 or as you answer Q7). Raider,
  artillery and AA coefficients.
- *Predicted:* the matchup matrix. *Verified* with BalanceLab duels per
  pair.
- *You test:* a game focused on T1→T2.

### Stage 3: Per-unit and per-faction tuning

- Parity diffs, faction identity knobs, the commander, and 0.1.x's unit
  changes revisited (Jager, T1 tanks, raiders, artillery, Guardian
  fighters).
- *Verified* with lab duels. *You test:* one game per faction pairing you
  care about.

The `fixes` section carries over as is: the targeting guard, the shield
rule, the Redoubt aim, the EDA T3 fighter's launcher, the TALEN tags, bomb
lifetimes, and artillery, bomber and AA leading (Komodo excluded). I found
no reason to drop any.

---

## 6. Your earlier decisions, revisited

| Decision | Verdict |
| --- | --- |
| Tiered eco, extractors included | **Fits**, and becomes P3. The tiers are what let 9-spot maps keep pace with 26-spot maps, so they're tuned on payback targets, not "better than before" |
| Less commander income to slow the T2 rush | **Fits P1, but the lever moves.** Commander income sets the opening; the T2 rush is better controlled by the T2 factory's price and time (one number) and by T2 eco payback. Pair less income with a bigger start bank through storage |
| Land alloy-heavy, air energy-heavy | **Fits strongly**: it becomes P2, with territory feeding land and the base feeding air. Structures and tech get a ratio too |
| T1 PD outranged by artillery but strong against T1 tanks, vision = range | **Fits P5** (holding spots). Keep it; check its cost against the extractors it protects |
| T2 beats its cost in T1 without crushing it | **Fits**, now quantified: wins keeping 20–40% of its value, range included. Needs the T2 roster question (Q5) answered |
| Chosen half-price air is a bug | **Fits P7**: identities come from stats, not discounts. Keep the fix |
| Cheaper, weaker engineers | **Half fits.** Weaker yes (P5). Their cost now *is* the build-power price, a core eco lever, so it's set in stage 1, not by analogy |
| Jager nerf, commander missiles, arty/AA fixes | Unit-level; they carry into stage 3 and are re-checked there |
| `startbank` / `costs15` toggles | **Drop**: superseded |

---

## 7. What was and wasn't checked

**Checked:**
- Every [code] row was read in the game's Lua at the cited lines.
- Template numbers are from the installed templates.
- Spot counts are from the `.sanmap` marker lists.
- FAF numbers are from FAF's develop branch blueprints; BAR's from its
  unitdefs; Zero-K's overdrive is `sqrt(energy) × 0.25` extra metal per
  extractor (`unit_mex_overdrive.lua:265-267`).

**Not checked:**
- Anything in game.
- That upgrades charge full price (taken from the unit-db).
- Negative adjacency costs.
- That every marker is a reachable spot.
- FAF's starting bank (I only read ACU storage).
- The "100 units in 7 minutes" game: not yet reproduced from its replay.
- All the [calc] rows are my arithmetic.

---

## 8. First concrete step

1. ~~Dump and parity test~~ **Done 2026-10-09:** `GameRef luarun` plus
   `tools/balance/dump.lua`. 357 templates, 0 differences from `preview.mjs`.
2. ~~Build `BalanceCal`~~ **Done 2026-10-09** on your three Frozen replays
   (§3). The five 2026-10-08 replays in `Replays\` can be added the same way.
3. ~~Your answers~~ **Done** (§3). They are `tools/balance/targets.json`.
4. ~~The sim, calibrated~~ **Done 2026-10-09:** `tools/balance/` (README
   there). It has two fitted player styles; teching is assumed, not fitted.

### Baseline: the existing rulesets against the targets [sim]

From `balance.mjs diff vanilla 0.1.1 0.1.1-startbank 0.1.1-costs15`
(mean over the reference maps, both spawns, all factions):

| | vanilla | 0.1.1 | 0.1.1 + startbank | 0.1.1 + costs15 |
| --- | --- | --- | --- | --- |
| 256, units built by 10:00 (target 100–130) | 93–140 | 108–191 | 72–112 | 70–95 |
| 512, income at 10:00 (target 25–30) | 27–29 | 44–52 | 29–30 | 45–47 |
| 512, units built by 10:00 (target 110–140) | 75–155 | 81–213 | 80–152 | 67–117 |
| 512, income at 15:00 (target 40–50) | 32–35 | 57 | 45–53 | 59–61 |
| Fastest T2 factory (target ≥ 7:00) | 1:48 | 1:58 | 1:58 | 2:42 |

Ranges run from the expand style to the spam style. Read in the sim:

- 0.1.1 overshoots, as played.
- 1 alloy per spot (startbank) lands closest at 10:00.
- Vanilla's weak T2 extractors leave its 15-minute income short.
- **Nothing stops a ~2:00 T2 factory.** That is the first economy fix.

### Stage 1: economy (2026-10-09, simulated; not yet played)

The sweep (`sweep.mjs proposed/stage1-levers.mjs`, 1500 evaluations, then all
factions) chose these, now in the mod as 0.2.0's `economy` section:

- **Commander:** 3 A and 30 E/s; storage 1500/15000, so the start is 750/7500.
- **Extractors:** 1 / 5 / 10 A/s. T2 upgrades pay back in 150 s, T3 in 400 s.
- **Generators:** 20 / 600 / 4500 E/s.
- **Land units:** same alloys, 6 energy per alloy.
- **Factories:** T1 300 A. The T2 upgrade costs 2500 A, same build time. T3
  6000 A (unswept).
- Engineers' health cut, and the Chosen air fix, as in 0.1.1.

| Target | vanilla | 0.1.1 | 0.2.0 |
| --- | --- | --- | --- |
| Fastest T2 factory (≥ 4:00) | 1:48 | 1:58 | 5:55 |
| Spam units alive at that moment (≥ 25) | 0 | 0 | 20–24 |
| 256: units built by 10:00, average player (100–130) | 117 | 149 | 102 |
| 512: income at 10:00 (25–30) | 27.8 | 48.0 | 27.8 |
| 512: units built by 10:00 (110–140) | 115 | 147 | 112 |
| 256: income at 10:00 (18–24) | 17.7 | 28.2 | 16.3 |
| 512: income at 15:00 (40–50) | 33.0 | 56.7 | 36.0 |

The last two rows miss, and both depend on when players tech, which the sim
assumes (8:00) rather than knows. Real players teched later: in the 17-minute
256 game both held every spot from ~7:00 and never upgraded an extractor.

**0.2.1 (unreleased, 2026-10-09):** commander storage back to 500/5000, and an
army starts with it full (append to `unitsDefault.lua`, `economy` option); the
user's call. Simulated against 0.2.0 (`diff 0.2.0 0.2.1`): fastest T2 5:55 →
6:37, about 6 fewer units alive at 10:00 on both classes, income unchanged.

**The Forge, user vs AI (2026-10-09, 0.2.0, 2048 map, 67 spots, 2 of 8
spawns):** 12.5 minutes, no T2 factory and one T2 extractor (at 11:00). The user
took 36 extractors by 10:00 and 41 by 13:00 (T1 extractors pay back in 50 s,
so every free spot beats any upgrade), built 12 T1 factories and had 116 units
alive at 10:00 (131 built). Income 39 at 10:00, 49 at 13:00. The AI stalled on
alloys from 5:00 to 7:00 and never recovered. The sim on the same spawns built
a similar number of units (124 by 10:00) but took about 21 spots, not 36.

**0.2.2 (2026-10-09, the user's call after the T3 discussion):** T3
extractors make 15 A/s (upgrade payback 200 s, was 400 s). T3 generators make
2500 E/s and cost 2800 A / 28000 E with build time 2800 (were 4500 E/s for
5000 A / 50000 E, bt 5000): about the same energy per cost, in a smaller step.
The sim can't judge either: its fitted players never build a T3 extractor or
generator (they still have T1 spots to upgrade at 30:00), so `diff 0.2.1
0.2.2` is identical. Needs replays with T3 in them.

Also in 0.2.2 (the user's call): the T2 factory upgrade costs 1500 / 15000 (was
2500) and T3 4000 / 40000 (was 6000). T3 tanks (Kodiak, Glaive, Auger) and the
Guardian Nitro cost 30% less (alloys and energy, same build time); other T3
units keep their cost, and sniper bots stay slow on purpose. T3 tanks move at
3.3 (were 2.5-2.7), hull turn 90, gun yaw 180 / pitch 90 deg/s. The Nitro moves
at 4.5 (was 3.3), gun yaw 180, health 1500 (was 2250: one Kodiak volley, 3 x
697, kills it); range stays 24 against the tanks' 34. Simulated against 0.2.1: the
fastest T2 factory moves from 6:37 to 4:21, facing 7-8 spam units (target 25),
so the rush target now misses on both classes; 512 income at 15:00 rises from
39 to 45. A longer T2 build time doesn't restore it (bt 1600-3200 at 1500 A:
3:51-4:26): the rush is gated by cost, not time.

**0.2.3 (unreleased, 2026-10-09, the user's calls):** wrecks reclaim 3x as
fast (ReadPropTemplate append divides a wreck's harvestTime, the unit's build
time, by 3; same total) and last 360 s instead of 180 (wreckageClass append
swaps the delete timer HostWreckage:__init starts). T4 walkers sped up by role
(were all 2): brawlers 3.5 with hull 70 and main-gun yaw 90 (Behemoth, Ares,
Guardian beam bot); Djinn and Athena 3 (hull 60); Centaur, Quasar, Tripod and
the Chosen big bot 2.5 (hull 50). Not modelled by the sim; not played.

**0.2.4 (unreleased, 2026-10-09):** T4s as game enders for long 512 games,
spammable as the game goes on (the user's brief: 10000 alloys minimum, 2-3x
the old price, slightly more efficient per alloy than T3). Priced 10000-25000
(energy 6 per alloy); health and damage raised so health and damage per alloy
are 1.1x the average T3 tank's after the 0.2.2 discount (15.8 HP and 0.49
DPS per alloy), 1.0x for the 60-100 range Djinn and Chosen big bot; the
Quasar against T3 artillery, health kept. DPS counts muzzles per group and
salvos (weaponsBaseClass: each salvo fires every muzzle of the current group),
beams per tick; my arithmetic, not measured. The Centaur (railgun sniper) gets
range 80 (was 40) at 0.9x damage per alloy. Athena (mobile shield, 18000 A,
40k shield) untouched pending a decision. T1 factories 200 A / 2000 E (were 300 in
0.2.0-0.2.3; the user's call). Simulated against 0.2.3: units built by 10:00
72 -> 85 (256) and 104 -> 117 (512, now in target), alive 40 -> 47 and 58 ->
65, factories 5.0 -> 5.6 and 6.8 -> 7.9; fastest T2 4:21 -> 4:04.

**0.2.5 (unreleased, 2026-10-09, the user's calls):** T2 radar upkeep 250
E/s (was 150); T3 radar 2800 A / 28000 E (was 700 / 7000), build time kept, upkeep 1400 E/s
(was 350). EDA T3 radar range 750 (was 450), Chosen 850 (was 550); Guardian
stays 1000.

**0.2.6 (unreleased, 2026-10-10, the user's calls):** T4 health and damage
cut to 0.8x of 0.2.4's (one T4 keeps all its damage until it dies, so equal
stats per alloy played far stronger). Anti-air rebuilt to hit most of the time
(the user: "AA should hit the majority of the time"). From the game's code:
projectiles hit by a zero-width ray each tick against the target's box
(collisionInfo.collisionSize, full size), so a projectile's own size never
counts; NoArc shells fall at aimGravity (default 1/s^2) uncorrected; homing
missiles chase the current position at rotationSpeed. Changes: the three AA
beams that 0.1.0 set to lead stop leading (bug); AA aim speed x1.5, x3 for
the slowest eight and x6 for the Guardian T2 tower, spread removed on those;
AA missiles speedMax x1.5, turn 120; AA-only shells don't fall; aircraft
hitboxes x1.5. An offline shot model (scratchpad aa-sim.mjs; my model of the
game's hit code, aircraft flight is mine; it matched the 0.1 lab's ranking but
not its absolute numbers) puts the average hit rate over 24 AA weapons and 5
aircraft at 82% straight / 75% micro / 71% weaving (0.2.5: 38 / 27 / 28), the
worst weapon 59% under micro. Effective AA damage is about 2-3x 0.2.5's; air
numbers may need retuning once played.

**0.2.7 (unreleased, 2026-10-10, from the user's games):** T4s were still
too strong (the Chosen big bot beat its cost in T3 tanks easily). 0.2.4's
per-alloy maths ignored splash (radius 3-4 hits several bunched T3 tanks),
range (60-100 against the tanks' 34) and counted T4 anti-air as ground damage.
Re-scored with my estimates (splash x1 / 1.5 / 2 / 2.5 for radius <2 / 2 / 3 /
4+, range x1.25 at 60, x1.5 at 80+) as health x ground damage per alloy against
the average T3 tank: big bot 1.49, Ares 1.28, Centaur 1.30, Tripod 1.21, Djinn
1.02, Behemoth 0.75. Health 0.8x for all; ground damage 0.5x (big bot), 0.6x
(Ares, Centaur, Tripod), 0.75x (Djinn), 0.9x (others), putting all at 0.54-0.62
(Quasar 0.45). The user then set the target at 0.8 split between health and
damage: each T4's health and ground damage x sqrt(0.8 / score), 1.13-1.34x
(the single-target beam bot and Behemoth about 1.2x, the Quasar 1.34x, the
splashing big bot, Ares and Djinn 1.14-1.16x). All now score 0.80. T4 speeds 3 / 2.75 / 2.25 (none above a T3 tank's 3.3).
Guardian T2 anti-air (tower and mobile) half damage: their shells splash 5
and 10 against the Chosen 2 and 3 (the user first said T1, then corrected it). Nitro: 3000
health (was 1500), damage x1.25: 12 health and 0.75 damage a second per alloy,
against T3 tanks' 16-18 / 0.43-0.55 and T2 raiders' 13-19 / 0.61-0.83.

Point defences (0.2.7): scored against same-tier tanks, ours were T1 0.95-1.17,
T2 0.18-0.28, T3 0.13. FAF's (from its blueprints, my arithmetic with salvo
and rack timing): T1 1.68, T2 0.37-0.62, T3 Ravager 0.22. T2 raised to 0.65
(health and damage together); the Chosen T3 to 7000 health (the user's cap)
and 2.24x damage, about 0.3.

**Playtest asks:** a 1v1 on a 512 map (and a 256 if there's time). Watch:

- when you first upgrade an extractor and a factory, and why;
- whether 3 alloys from the commander plus 750 in the bank feels right
  through the first two minutes;
- whether factories at 300 hold back spam without making the opening drag.

---

## Sources

- FAF blueprints (develop): `https://raw.githubusercontent.com/FAForever/fa/develop/units/<ID>/<ID>_unit.bp` for UEL0001, UEB1103, UEB1202, UEB1302, UEB1101, UEB1201, UEB1301, UEB0101, UEB0201, UEB0301, UEL0105, UEL0201, UEL0202, UEL0303, UEB1104, UEB1303
- Zero-K overdrive: <https://github.com/ZeroK-RTS/Zero-K/blob/master/LuaRules/Gadgets/unit_mex_overdrive.lua>
- BAR unitdefs: <https://github.com/beyond-all-reason/Beyond-All-Reason/tree/master/units/ArmBuildings/LandEconomy> (armmex, armmoho, armsolar, armmakr: a maker converts 70 E/s at 0.01429 → 1 M/s)
- BAR balance_algorithm (team matchmaking, not unit balance): <https://github.com/beyond-all-reason/balance_algorithm>
