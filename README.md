# Sanctuary Mods

A monorepo of client-side BepInEx mods for _Sanctuary: Shattered Sun_ (demo
and playtest, `engine` build). One project per mod, each building to its own
DLL that hot-reloads independently — so any mod can be shared, loaded or
unloaded on its own.

The UI mods are presentation-side only: they never touch the game's Lua tree
(which the multiplayer lobby hashes — `ComputeLuaHash` over `*.lua` under
`engine\LJ\lua\`) and never touch the simulation (which is hash-checked per
tick between players), so a modded client stays lobby-compatible with unmodded
players. The exceptions are called out in their own sections below.

Lobby-compatible is not the same as safe. Every DLL here, like any BepInEx
plugin, is a full-trust client plugin: it runs inside the game process with
the permissions of the Windows account playing, and an unchanged Lua hash is
no check against cheating or harmful code. Nothing in the game or the loader
enforces good behaviour, so install DLL mods only from a source you trust.

Every release ships two zips. **Standalone** is everything — BepInEx, the mod
loader and the mod — for a clean install; extract it into the game's `engine`
folder. **ModManager** is just the mod, for an install that already has the
[Mod Manager](#modmanager); it appears under UI Mods and can be switched on and
off from there. Each mod builds to `<name>.dll`, and the project link is its
source.

| Project | Download | What it does |
| --- | --- | --- |
| [SanctuaryHud](SanctuaryHud/) | [**0.9.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/SanctuaryHud-0.9.0) | Economy strip in the game's own style, optionally replacing the built-in panel, buttons included; commander widget and alerts; reclaim values and build countdowns over the map |
| [IdleEngineers](IdleEngineers/) | [**0.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/IdleEngineers-0.2.0) | Clickable idle-engineer panel, with idle factories by type and tier underneath |
| [EcoManager](EcoManager/) | [**0.3.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/EcoManager-0.3.0) | Alloy extractors by tier, plus upgrades in progress; assist starts an upgrade and holds it paused until the engineer arrives |
| [BuildHotkeys](BuildHotkeys/) | [**0.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/BuildHotkeys-0.2.0) | One hotkey per *role*, same key every faction, cycling by tier |
| [LadderReporter](LadderReporter/) | [**0.3.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/LadderReporter-0.3.1) | Reports ranked results; launches matchmade games |
| [ReplayManager](ReplayManager/) | [**0.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ReplayManager-0.2.0) | Watch the game's replays fog-free from any seat, with every economy |
| [CameraUtilities](CameraUtilities/) | [**0.1.2**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/CameraUtilities-0.1.2) | Switches off icons, range rings, order lines and the UI, and unlocks how far out units are drawn, for cinematics |
| [ModManager](ModManager/) | [**0.5.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.5.0) | Mods page in the menu and on F8 in a match: mod toggles, settings (switches, sliders, text), Lua overlays |
| [MapLocalFiles](MapLocalFiles/) | — | Lets Lua read files from the loaded map's folder |
| [ModLoader](ModLoader/) | [**1.3.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModLoader-1.3.0) | Loads and hot-reloads every mod above from `SanctuaryMods` |

[All releases](https://github.com/Remmyboy/sanctuary-mods/releases) · MapLocalFiles
has no release of its own yet; build it from source if you need it.

## SanctuaryHud

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `STALL −N/s` appears when a resource can't pay for what is
  queued. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
  receiver of Lua's `Engine.UI_SetEconomyValues`.

  While a resource is stalling, its spend figure is what your queue is
  **asking for** (`RequestedTotal`); otherwise it is what the economy actually
  paid (`RequestedStalled`), as on the game's own panel. During a stall actual
  spend is capped by income, so showing it would just mirror the income back
  at you (`+12 −12`) and hide the shortfall. A resource only counts as
  stalling when its own store can't cover a tick of demand, which is the test
  the host's `economy.lua` throttles on: construction draws alloy and energy
  together and is slowed by whichever runs short, so an energy stall cuts
  alloy spending as well, and a full alloy store must not show a stall
  because of it. Net stays on actual spend, since that is what really moves
  the store.

  The rates are smoothed every frame on real elapsed time towards the latest
  update, with a fixed quarter-second time constant, so the strip trails the
  game's own panel by the same small amount at any frame rate, and income and
  net trail it by the same amount as each other. Storage is never smoothed.
  The strip stays up through a pause: the game's economy panel being visible
  counts as "in a match" even while the stream is silent.
- **Looks**: the strip takes the game's typeface (Rajdhani, off its
  TextMeshPro asset when the build keeps the source font; Bahnschrift on
  Windows otherwise) and the alloy/energy tints off the game's own panel, on
  the same near-black blue with an accent-blue hairline as the front menu.
  Numbers abbreviate exactly as the game's readouts do (`1.2K` above 999).
- **Hide the game's own bars** (`Overlay · HideGameEconomyBars`, off by
  default, in the Mod Manager's settings): switches off the built-in alloy
  and energy readouts so the strip is the only economy display. The buttons
  that share that panel (menu and pause among them) move into the middle of
  the strip, between alloy and energy, drawn with the game's own icons and
  its version line under them; hovering one names it. A click there is sent
  to the game's hidden original as a pointer click, so each does exactly what
  the game's button does, and an invisible uGUI target under that part of
  the strip keeps the click from also landing on the map. It all comes back
  whenever the overlay is hidden with **F10**, and when the mod unloads.
- **Commander widget** top-right: the game's own strategic icon with a health
  bar underneath; click to select the commander and move the camera to it,
  keeping roughly your current zoom.
- **Reclaim values** over the map while **Left Alt** is held (`Reclaim ·
  HoldKey`; `None` keeps them up permanently): the alloys left in every
  wreck and harvestable prop the client knows about, drawn at the spot.
  Values closer together on screen than `ClusterPixels` (110 at 1080p) are
  summed into one figure at their value-weighted centre, so zoomed out a
  battlefield reads as one number the way FAF's overlay groups it, rather
  than a smear of digits. Energy shows as a smaller amber `E` line only where
  it is the point. `MinValue` hides trivia. The prop table
  (`__Entities.Props`) holds wrecks and map props alike with their template's
  `economy.harvest`, and the host streams `reclaimProgress` as they are
  eaten, so what is left is `harvest × (1 − progress / harvestTime)`. A
  decayed wreck stays in that table with no render entity behind it, so each
  is checked with `Engine.IsValidLocalID` before it counts. Positions are
  cached Lua-side per prop; only the values are re-read, once a second.
- **Build countdowns** (`BuildEta · Enabled`): under your structures still
  under construction, upgrades included, a `m:ss` time-to-finish and a thin
  progress bar. The rate is measured from successive progress samples
  (half-second poll, filtered), so it reflects whatever is actually assisting;
  before anything has been measured the template's own build time stands in.
  The colour says what is happening to it: normal while something is
  building it, dark orange while your alloy or energy is stalling (a stall
  throttles every build), and red once nothing is building it and it hasn't
  moved for a few seconds, an abandoned site; the estimate stays either way,
  since there is no better number. What is building a site comes from the
  units whose build routine targets it (`isBuilding` / `buildTarget`, which
  the host reports to every client). An upgrade whose upgrader is paused and
  that nothing is building is left out altogether; an engineer assisting it
  builds straight through the pause, and then it shows. A game pause (the
  economy stream going quiet) freezes the clocks instead. Labels are placed
  soonest-first, one that
  would overlap another is skipped, and `MaxLabels` (12) caps them, so a
  busy base shows the handful nearest completion rather than a wall.
- **Alerts** (`Alerts · …`): toasts top-centre under the strip, with a short
  generated tone (no audio assets) at `Volume`. *Commander under attack* on
  any health loss, re-sounding at most every eight seconds while it goes on;
  *commander critical* once below `CriticalFraction` (35%), re-armed after
  repair; *structure complete* when a countdown finishes; *player
  disconnected* (`PlayerDisconnected`), a white toast naming them when the
  game reports a player dropping out, which it otherwise shows as small text
  top right, and one when your own connection to the host is lost. That
  comes from a postfix on `LogPanelUI.AddLogText`, the method both notices go
  through; the game's own line stays, several such toasts can stand at once,
  and a player quitting reads the same as one disconnecting, since the game
  sends the same message for both. Clicking a commander
  toast jumps to it like the widget does. Health is re-read four times a
  second off the commander's cached sim id, since the once-a-second ECS poll
  would make the alert a second late. All of it is for the active player
  only: "own" means exactly one focused army, so a replay's all-armies view
  (every army focused) and a seatless observer (none) get no commander
  tracking, countdowns or alerts, rather than both sides' at once; watching
  a replay from one seat gets that player's.

  Which completions get a toast is one switch each under `CompleteToasts`,
  by role (factory, radar, extractor, energy, defence, tech centre,
  strategic, other) and separately for a fresh build and for an upgrade to
  the next tier, plus `AnyTier4`. Defaults: factory and radar upgrades,
  tech centres, strategic weapons and anything tier 4; not a new extractor
  or generator. The role comes off the template's tags, the tier off
  `general.techNumber`, and "upgrade" from the `upgrader` link the game keeps
  on the new-tier entity while the upgrade runs (remembered, since the game
  clears it the moment it lands). The built-in tones are synthesised: under
  attack is a falling 520→390 Hz pair, critical three 330 Hz pulses,
  complete a rising 660→880 Hz pair. Voice packs replace them: a subfolder
  of `SanctuaryMods\SanctuaryHud\sounds\` holding `commander-under-attack`,
  `commander-critical`, `structure-complete`, `structure-upgraded` and
  `player-disconnected` WAVs (a line a pack lacks gets the tone)
  (any PCM or float WAV, any rate or channel count), chosen with
  `Alerts · VoicePack`, a left/right chooser on the Mods page listing the
  packs found at load plus `tones` for the built-in sounds. Three ship:
  `machine` (a female-voiced synthetic combat AI, the default),
  `announcer` (a military radio read) and `caretaker` (Sanctuary's own
  intelligence, calm and vast), in that order, with any pack a player adds
  after them; a loose WAV in the sounds folder itself is the fallback for a
  line a pack lacks.
  `Alerts · Volume` is 0 to 100 like the game's own audio sliders, and
  shows as one on the Mods page. The game's audio runs through Wwise and a Unity AudioSource
  plays nothing in this build, so the mod goes round the engine: it
  renders each sound at the configured volume to a 16-bit WAV in
  `BepInEx\cache\SanctuaryHud\` and plays that through the Windows
  waveform API (`winmm` PlaySound), which the system mixer handles like
  any other app's audio. Files under the project's `sounds` folder deploy
  with the build and ship in the release zips. Sound is off by default
  (`Alerts · Sound`); the toasts show regardless.

Hotkeys: **F10** toggles the overlay, **F9** dumps the UI hierarchy to the log.
Everything the HUD draws steps aside while the game's pause menu or a
front-end screen (settings, the Mods page) is open over the match, as the
game's own HUD does: IMGUI would otherwise draw on top of them.

## IdleEngineers

One clickable row per tech tier of idle engineers (plus a COM row for the
commander and an ALL row) — clicking selects that group. Each row shows the
unit's own build-menu art beside its label. Hidden entirely when nothing is
idle; only as wide as what it is showing; draggable, and its position persists.

**Idle factories** sit underneath (`Factories · Enabled`, on by default): a
heading per type (LAND, AIR, NAVAL FACTORIES) with one row per tier beneath
it, and an ALL row once more than one type is idle. Clicking a heading selects
every idle factory of that type, which is usually what you want before
queueing; clicking a tier row selects just those. Switching the setting off
hides the section at once and stops the lookup behind it.

A factory counts as idle exactly when the game draws its idle adornment:
finished, no order, nothing in its queue — the same rule the game applies to
engineers, re-checked whenever the order or the queue changes. So a factory
that is upgrading, or paused with something queued, is not idle. The type is
read from the client's tag tables (`LAND_FACTORY`, `AIR_FACTORY`,
`NAVAL_FACTORY`) rather than the strategic icon, because the T3 naval
factories ship with the air symbol and would otherwise file under AIR; the
tier is the template's `general.techNumber`. Factories are picked out in the
same once-a-second sweep over your own army's units that finds EcoManager's
extractors, and only while the setting is on.

Idle state and unit identity come from the DOTS icon buffers rather than
Harmony hooks, because the icon FFI receivers are Burst-compiled and cannot be
patched. Selection and camera moves run through the client's own Lua via an
emitted call to `luaL_dostring` — client-side only, so still lobby-compatible. That
plumbing lives in [shared/HudCore.cs](shared/HudCore.cs), which is compiled
into each mod that needs it — so each DLL is fully standalone, at the cost of
each running its own copy of the once-a-second poll.

The row art is *not* the strategic icon the poll identifies units by: that is
the abstract map symbol, shared across factions and tiers, so it could never
tell a T2 engineer from a T3 one. The build buttons draw a per-template
`.sansprite` instead, loaded through the game's own pipeline into a registry
keyed by `AssetID`. `SanctuaryUI.Utils.TryGetLoadedSprite` is the public way
in, and `EM.Core.AssetID` wraps exactly the `uint` Lua reports as
`general.foregroundIconID.index`, so the army sweep also keeps one
representative template per row and hands its icon id to that lookup. Sprites
are windows into a packed atlas, so each is drawn through its own
`textureRect`. Art and label are both kept: the art is what you recognise, the
label is what makes the tier certain. Art the game hasn't loaded yet is retried
each second, and if the lookup is missing altogether the rows are just
labelled.

## EcoManager

A small **ALLOY** panel: one clickable row per extractor tier (T1/T2/T3, plus
an ALL row), and — only while something is actually upgrading — an
**UPGRADING** block underneath listing those by tier. Clicking any row selects
that group. Rows carry the extractor's build-menu art beside the tier label,
by the same route as the [idle rows](#idleengineers), and the panel is only as
wide as its rows. Hidden until you have your first extractor; draggable,
position persists.

The tier comes off the strategic icon, `structure1_t{n}_alloy_normal`, which is
uniform across all three factions. Upgrading state is the game's own upgrade
adornment — the one `ClientUnit:CheckShowUpgradingAdornment` drives from
`IsUpgradeQueued()` — so it lights and clears exactly in step with the icon the
game itself draws. An upgrading extractor is counted in both its tier row and
the upgrading row, since it is still live at its current tier until the
upgrade completes.

That icon alone is *not* enough to identify an extractor, though: alloy
storages (`ues1602`) and the Tier-3 alloy furnace (`ues3603`) carry the very
same icon, and the render entity holds no template id. So the client's Lua
supplies identity instead — every template is filed into `Tags[tag][tpId]` as
it loads, making `Tags.ALLOYS_EXTRACTION` exactly the set of extractor
template ids, and `Armies[focused].units` exactly our own units. One query per
poll turns that into a set of LocalIDs, which the panel matches against. It
doubles as the ownership filter, so the alloy rows never depend on the
army-colour match the idle rows use.

### Assist starts the upgrade

Ordering an engineer to assist a structure with an empty build queue does
nothing today — the engineer walks over and stands there — so the obvious
gesture for "help this extractor along" is a dead end. With
`AssistStartsUpgrade` (default on) an assist ordered onto one of your own
finished extractors queues its upgrade first, then issues the assist exactly
as the game would, so the engineer arrives to real work and keeps its order.
It is scoped to extractors on purpose: factories upgrade too, and silently
spending that much because someone assisted one would be a nasty surprise.

This is the one thing in the repo that *acts* rather than displays. It goes
through the game's own client path — the same `ModifyBuildQueue` prediction
and `UpdateQueueAmount` command the construction panel sends when you click
the upgrade button — so the host validates and replicates it like any other
order, and no files change, so the lobby hash is untouched. What it costs you
is that an assist click now spends alloy.

### Paused until the engineer arrives

Sending five engineers to five extractors starts five upgrades at once, and the
economy goes flat while every one of them crawls. With `AssistPausesUpgrade`
(default on) each upgrade started this way is **paused as soon as it starts**
and released when its engineer actually turns up — so the cost is spread over
the walk instead of landing all at once, and an engineer that gets killed on
the way never spends anything at all.

The pause has to lag the queueing by about a second (`AssistPauseSeconds`): the
upgrade is not registered as in progress on the frame it is requested, and
pausing before then does nothing. Each entry waits out that delay, checks the
upgrade really took, and is dropped if it did not. Arrival is the engineer
getting within its own `construction.range` plus `AssistPauseRadius` of the
extractor, measured in the ground plane so a slope cannot hide it. A cancelled
upgrade releases the pause on its way out, so nothing is ever left stopped with
no explanation — and neither is unloading the mod.

Pausing goes through `RequestUnitsToggle`, which takes explicit unit ids, so
none of this disturbs your selection. The watch runs five times a second from
the C# side, because a second's granularity would be visible on both halves.

One thing to know: if the engineer never arrives — killed, or re-tasked — the
extractor stays paused with the upgrade queued. That is the safe failure (it
costs nothing), but it is yours to unpause.

The hook is a runtime wrapper around the client's `IssueAssistOrder`:
`inputActions.lua` binds the key to
`Import("client/inputEventsFunctions.lua").IssueAssistOrder()`, resolved at
press time, so replacing that field intercepts every assist without editing a
file. It is removed again when the mod is unloaded or the setting is switched
off, and each match's fresh Lua state reinstalls it.

### Counting

That query counts only completed extractors. An upgrading extractor builds
its replacement as a second entity, present from the moment the upgrade
starts and already wearing the higher tier's icon — so a T1 mid-upgrade would
otherwise read as a finished T2. The T1 stays until the upgrade lands and is
the one carrying the upgrade adornment, so it is what fills the UPGRADING row.

## BuildHotkeys

One hotkey per **role** rather than per panel slot, so the same key means the
same thing whichever faction you are playing, and pressing it again walks down
the tiers.

The game's own construction hotkeys are nine fixed letters resolved by tag
category, first displayed match wins (`constructionPanelHotkeys.lua`). That
leaves two gaps: you cannot reach a *specific* unit — the T1 and T3 tank are
both `Tags.TANK`, so only one of them has a key — and whole categories have no
key at all. Shields, artillery, air and naval factories, tech centres, walls
and storage all render their button with `?` on it.

**A role is a tag expression**, not a list of template ids, which is what makes
it faction-agnostic for free. `PointDefence` is
`DEFENCE * ANTI_SURFACE * STRUCTURE`; that resolves to `ues1001` for EDA,
`ucs1001` for Chosen and `ugs1001` for Guard without naming any of them. The
three factions share 77 of their ~99 roles, and the T1–T3 core — factories,
point defence, anti-air, radar, energy, extractors, tech centres — is
essentially universal, so one table covers everyone.

**Tier cycling falls out of the templates.** Each one carries its own gating
(`BUILDABLE_BY_T2_ENGINEER` and friends), so `GetBuildableTags` already returns
exactly what the selected builder can make. Intersect that with the role, sort
by tech tier descending, and the first press gives you the best you can
currently build. Where a faction lacks a tier the cycle is simply shorter:
only Chosen has a T3 point defence, so **X** gives them T3 → T2 → T1 and
everyone else T2 → T1. No per-faction configuration anywhere.

Cycling continues while the previous press is still uncommitted — its template
sitting on the cursor — which has no time limit, since you may be lining a
placement up. Placing it, cancelling, or changing selection starts the cycle
over. A factory never enters build mode, so repeat presses there queue another
of the same rather than walking the cycle; FAF instead resets its cycle on a
timer, which is what makes its factories cycle too, and `Cycle.Seconds` (0 by
default, 1.1 to match FAF) turns that on here.

**Escape stops every selected factory**, as it does in FAF, rather than opening
the pause menu. It sends the Stop button's own order rather than editing the
queue: emptying the queue alone leaves a factory that is assisting another one
still slaved to it, and it pulls the next item straight back off that factory's
queue. The host's stop drops the assist along with the queue and the item in
hand. Only your own factories are stopped — a tank sharing the selection keeps
its orders. With no selected factory queued or assisting the key falls through
untouched, so escape still opens the menu; and because there is no getter for
panel visibility, only a setter, the mod mirrors the menu's state by watching
that setter (which the menu's own close button goes through too) so escape
still *closes* the menu rather than stopping a factory behind it.
`Cancel.ClearFactoryQueue` rebinds or blanks it.

`Menu.PauseMenuKey` moves the pause menu off escape — to **F11**, say; F1 is
taken by the game's debug menu — so it never opens by accident when escape had
no factory to stop. The game's own toggle moves to the new key as it is, and
escape keeps only the closing half: an open menu still shuts on escape.

Holding **Shift** queues five, as the stock hotkeys do. Holding **Alt** walks
the cycle backwards, as it does in FAF — and a *fresh* Alt press opens at the
far end, which makes it the direct route to the cheapest option: the T1 factory
you mean to upgrade later, rather than the T3 one the forward cycle opens on.

**Several roles can share a key**, merging into one cycle ranked by tier first
and role order second. That does two jobs at once. Where the roles cannot
coexist it reads as "first one that applies": **R** is the land factory's tank
and the naval factory's warship, and no factory builds both. Where they can
coexist it reads as a round-robin: **W** is land, air, then naval factory off
repeated presses, each at its best tier, before the cycle drops a tier and
comes round again — one key for the whole decision instead of three. Splitting
them back onto separate keys is just a config edit.

Note the ordering that falls out of this for a high-tier engineer: W gives the
T3 land factory before the T1 one, so placing a cheap T1 factory to upgrade
later takes several presses. If that reads wrong in play, the fix is a
per-role "prefer lowest tier" flag rather than a change to the cycle.

Roles stop at T3 on purpose. The experimentals above are faction-specific
one-offs that want their own keys — without the cap, Guard's T4 Experimental
Generator (`ugs4621`, tagged `ENERGY_PRODUCTION`) would sit at the top of the
energy cycle and a tap of **D** would try to start one.

**The unit keys follow Zulan's**, the Forged Alliance hotbuild layout that FAF
later absorbed: mnemonic, and the same letter reused across domains, because a
factory only ever builds one of them.

| Key | Land factory | Air factory | Naval factory |
| --- | --- | --- | --- |
| **E** | engineer | | |
| **S** | scout | scout | submarine |
| **T** | tank | transport | |
| **B** | raider | bomber | battleship |
| **F** | | fighter | frigate |
| **D** | | | destroyer |
| **G** | | gunship | |
| **O** | | torpedo bomber | |
| **R** | mobile artillery | | |
| **N** | mobile anti-air | | |
| **V** | sniper | | |

That reuse is the same "roles sharing a key" merge described above, resolved by
whichever factory is selected. Two departures: Zulan's gives each warship class
its own key rather than walking a line of tiers, so frigate/destroyer/battleship
are split rather than cycled; and **V** for the sniper is ours, since Zulan's has
no sniper and its V is a mobile shield with no shared-tier equivalent here. Its
cruiser, carrier, missile launcher, amphibious tank and stealth field have no
counterpart in Sanctuary's shared roles either, so those keys are simply unused.

Structure keys are not Zulan's — its structure layout is not something we could
verify — and keep the stock construction letters where they already fit
(W/E/S/D/X/C/R).

Every role's key is a config entry, so they are all rebindable from the F8 mod
manager in the game's own hotkey format (`G`, `Ctrl-G`, `Ctrl-Alt-G`); blank
unbinds. Where a unit key lands on an order key — **G** is Repair, **F**
attack-move, **V** reclaim — nothing is lost: the role only claims the press
when a factory is selected and has something to build, and orders like repair
and reclaim mean nothing to a factory. Any other selection falls straight
through to the order, because Construction runs at a higher group priority and
returning `false` lets the event carry on. `M` is left unbound, so the stock
"upgrade structure" hotkey still works.

**The construction buttons relabel themselves.** Each one draws its hotkey in
the corner, filled from `constructionPanelHotkeys.GetHotkeyForTemplate` —
`constructionPanel` holds the module table rather than the function and looks
the field up per button, so replacing it relabels every button with the key
that actually builds it. Modifiers compress to one character (`^S` for
`Ctrl-S`) to fit where a single letter went. Anything no role claims keeps the
stock answer, which is usually the `?` it shows today.

**An overlay shows what you just picked.** A cycle is otherwise invisible
until you place something — you cannot tell whether W→W landed on the air
factory or on a lower-tier land one. So each press publishes its whole cycle
and a strip appears: every option in that key's
cycle left to right in the order further presses reach them, drawn with
the same art the build menu uses — each unit's icon over the domain plate
behind it (`backgroundIconID`, keyed on `iconUIType`: land, air, water,
amphibious), so the strip reads like a slice of the panel rather than floating
cut-outs, and land/air/naval separate at a glance. The live one is lit and
underlined, the rest faded. A factory shows only its pick unless
`Cycle.Seconds` is set: without a cycle window a repeat press queues another of
the same, so the rest of the list would advertise options pressing again cannot
reach.

A long cycle shows **one tech tier at a time** rather than all of it: a T3
engineer's factory key is nine entries once naval factories are in, which would
span the screen at this icon size. A small `T3` / `T2` label then names the
band you are in — the key itself gets no column, since you just pressed it —
and the next entry leans half into view past the right edge, which says "there
is more" without asking anyone to read a count. No peek on the final band is
itself the signal that the cycle ends there, and with the whole cycle on screen
the tier label goes too, since it would read as applying to a strip that spans
tiers. Banding is by tier rather than by a fixed block of
three, because a block would straddle two tiers whenever a faction lacks a
domain at one of them — Chosen's T3 point defence, which nobody else has, is
enough to shift every block after it. A cycle that already fits is shown whole:
point defence is one entry per tier, and banding that would leave a single icon
on screen. `Overlay.MaxShown` (default 3) caps a band.

`Overlay.ShowNames` adds a caption underneath naming the entry
you are on ("Tier 1: Land Factory") — off by default, since the art usually
carries it and the name is only needed to separate two tiers that share a
sprite.

Those icons are *not* the strategic icon atlas the commander widget samples;
they are per-template `.sansprite` assets loaded through the game's own
pipeline into a registry keyed by `AssetID`. `SanctuaryUI.Utils`
`.TryGetLoadedSprite` is the way in, and `EM.Core.AssetID` wraps exactly the
`uint` that Lua reports as `general.foregroundIconID.index`, so the panel
passes that index across and resolves the real `Sprite` — drawn through its
`textureRect`, since each one is a window into a packed atlas. If the lookup
ever goes missing the overlay just lists names.

The strip fades a couple of seconds after the last press,
and is drawn rather than built from `GUI.Window`, so it can never swallow a
click meant for the battlefield under it. `Overlay.Show`, `Overlay.Seconds`,
`Overlay.IconSize` (40), `Overlay.MaxShown` and `Overlay.PosY` (860, just clear
of the build panel) control it.

Nothing here edits a Lua file, so `ComputeLuaHash` is untouched and a modded
client still joins unmodded lobbies. The binding is a runtime insert into
`inputSystem.lua`'s `LoadedActionMap` (which `CallAction` reads live on every
event, so it takes effect immediately and is restored on unload), and the build
goes through `constructionPanel.lua`'s own `ConstructionClickFunction` — the
same observer check, the same local prediction and the same host-validated
command a button click sends. Returning `false` when nothing matched lets the
key fall through to whatever it normally does, and because chat disables every
action group but `MouseControls`, typing already suppresses these for free.

## LadderReporter

Reports ranked 1v1 results to the [SanctuaryDB ladder](https://www.sanctuarydb.net/ladder)
automatically, and — new in 0.2 — lets the ladder launch a matchmade game
with no lobby interaction from either player.

**Reporting.** The host computes each army's win condition in Lua and
broadcasts every change to every client; a runtime wrapper around the
client's `WinConditionUpdate` sees the result. Identity is the game's own
Steam session: at report time the mod mints a Steam web-API ticket, sends it
with the result, and cancels it once the request is over. The ticket proves
which Steam account sent the report, not that the result in it is true, so
the ladder doesn't take it on trust: a player's own loss applies at once, a
claimed win applies when the opponent's client agrees or after a 15-minute
window, and reports that contradict each other freeze the match as disputed.
Only Steam lobbies with exactly two human players on opposing teams and no AI
are reported; skirmish, LAN, AI and team games are recognised and left alone,
as is a game you are only watching. Spectators in a ladder game don't stop it
reporting.

**Matchmaking.** The site pairs queued players, picks map, factions, slots
and host, and runs the countdown. The mod never polls the site: it listens
on `127.0.0.1:27555` (loopback only, `Matchmaking.LocalPort`), and the
SanctuaryDB page in the player's browser asks it every couple of seconds
what the game is doing (`GET /status`), relays that inside the polls the
page already makes, and hands the match object over when there is one
(`POST /match`). Only requests from `https://www.sanctuarydb.net` (plus
`Matchmaking.DevOrigins` for a dev server) are answered, so no other web
page can push a match into the game; a game left open in the menu costs the
site nothing. A pushed match is validated before anything acts on it (ids,
Steam IDs, slots, and a map that has to be a relative `Maps/.../*.sanmap`
path inside the game), and a malformed one is answered with a 400. Nobody
needs the mod to queue: the site only picks the
automatic path when *both* players' games are seen in the main menu with
the mod, and falls back to today's manual hosting otherwise. The mod's own
calls to the site (session id, progress events, the result) carry a bearer
token from one Steam ticket, minted when the first match arrives. When a
match reaches `launch`:

- the host's mod creates the lobby on the assigned map (`CreateLobby`),
  moves the UI to it, posts the session ID to the site, seats itself
  (faction, slot, ready), kicks anyone who isn't the assigned opponent, and
  starts the game as soon as the joiner is seated and ready;
- the joiner's mod sees the session ID on the page's next push and joins by
  ID through the same public entry point Steam "join game" uses
  (`InterfaceManager.JoinSessionFromInvite`), then seats itself.

Both bring the game window back if it's minimised or buried (a `user32`
restore, with a taskbar flash when Windows refuses focus), post progress
events, and mirror the site's timeouts locally so both sides converge if a
push is late. Any failure leaves the lobby, tells the player why in a
small overlay, and points at manual hosting. A matchmade game's result
report carries its `matchId` so the site can close the match.

Everything the site side needs is in [docs/matchmaking-site-plan.md](docs/matchmaking-site-plan.md)
and the site repo's `docs/local-bridge.md`.
To test the launch flow without the site, point `Matchmaking.MockFile` (F8
window) at a copy of [docs/matchmaking-mock-host.json](docs/matchmaking-mock-host.json)
or [matchmaking-mock-joiner.json](docs/matchmaking-mock-joiner.json); `"me"`
stands in for your own Steam ID, and the joiner's file takes the session ID
the host logs.

## ReplayManager

Makes the game's own replays watchable properly: any player's point of view
or every army at once, the fog lifted, every army's economy with whole-game
totals, and a transport with pause, speed, seek and rewind. Since the
playtest update of 2026-09-04 the game records every match to
`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Replays\*.sanreplay`
and plays them from the main menu's replay list; the panel appears whenever
one is playing, and **F7** shows and hides it.

**What the game does.** Its replay is the host-to-client packet stream,
written by the client as it arrives, behind a small header (map, game
version + Lua hash, recording client). Playback goes through
`ReplayClientSockets`, a fake socket that synthesises the launch messages
and then reads recorded packets into the client's receive buffer, paced by
the sim speed, at most 32 ticks ahead. The client only steps a tick once its
packet is buffered, so the socket's feed rate is the playback rate. This is
the same design the mod used before (see [archive/](Replays/archive/)), so
the mod now only drives the game's socket:

- **pause** is a Harmony prefix on the socket's `Receive` that feeds nothing
  once the launch messages are through;
- **speed** is the client's own `Engine.SetSimulationSpeed` (0.1× to 16×),
  which is what the socket paces by;
- **position** is frames read (a postfix on `TryReadFrame`) minus frames
  still queued; **length** is a scan of the file's frame headers;
- **fast-forward** runs at 16× until the target tick; **rewind** leaves
  through the game's quit path (scene reload) and calls the game's own
  `StartReplayPlayback` on the same file again, then fast-forwards. There
  are no snapshots to seek with, so going back costs a restart.

**Seats, fog, economy.** The recording's `InitClient` message only seats the
client that recorded it, so the view buttons call the client's own
`SetFocusArmy` (`-1` is the game's all-armies observer mode) and the fog
post-process is switched with the focus (or by hand). The client is marked
an observer so clicks can't issue orders into the void. Every army's economy
comes from a wrapper on `UpdateEconomyTotals.Receive` in the game's command
registry, which keeps all armies' totals and sums income and spend per tick;
the registry hands the receiver an already-decoded payload, so the wrapper
is a few lines. Player names come from the recorded lobby: a wrapper on the
`ReceiveDataClient` global captures `InitClient`'s roster. Both hooks are
installed from a postfix on `ClientLuaInterface.Startup`, before the first
packet is applied, with the half-second poll as fallback.

**The panel** has the clock, play/pause, a log-scale speed slider, ±1
minute, a FOG toggle, a TIMELINE toggle that hides the total length and the
seek bar for watching without knowing when the game ends, QUIT, and one row
per army: the name button (in the army's own colour) switches to that army's
view, then alloy and energy as a storage bar, net / in / out per second, and
the amount used so far in the game. ALL shows every army.

**Caveats.** A replay is tied to the game build and Lua hash it was recorded
with; the game's own list greys out mismatches. Playback is a normal client,
so the other mods in this repo behave as they would in a match and follow
the focused army. A recorded change of sim speed by the host would override
the speed slider for a moment; the panel re-applies its value.

## CameraUtilities

Takes the game's overlays off the picture, one at a time, for recording
cinematics — or just for a cleaner view. Every switch is in two places: the
mod's settings on the **Mods** page, and a small in-match panel on **F4**,
because during a shot you don't want to leave the game to change one. The
config entries are the single source of truth, so a change either way persists
and both views agree.

- **Strategic icons** — three ways: always, never, or only above a camera
  height. The last is the interesting one: icons are what you want at
  strategic zoom and exactly what you don't want in a close shot, so
  `WHEN FAR` keeps them until you dive in and drops them below the threshold
  (100 world units by default — the panel shows the live camera height next to
  the setting so you can pick one against the shot you're framing). This is
  the rule the game's own `rendering.lua` carries a TODO for.
- **Intel ranges** — the vision, fog-of-war, radar, sonar, omni and
  counter-intel rings.
- **Attack ranges** — direct, indirect, anti-air, anti-naval and counter.
- **Build ranges** — build and assist, so a screen full of engineers stops
  drawing circles.
- **Order lines** — the lines and markers drawn for whatever is selected:
  move, build, attack, assist, reclaim, and the whole-army view of them the
  append key brings up.
- **Planned buildings** — the outlines of buildings an engineer or commander
  has queued but not started. They come back on their own the moment
  construction begins.
- **Alloy spots** — the marker on a deposit with no extractor on it yet.
  Switching it back off hands the decision to the game's own
  `RecalculateRendering` rather than forcing every marker on, so a spot that
  gained an extractor meanwhile stays hidden, as it should.
- **Health bars** — every health and progress bar.
- **Game UI** — the whole HUD. This mod's own panel is Unity IMGUI rather than
  the game's UI, so it stays up and F4 still gets everything back.
- **Unit draw distance** — how far the camera can get before units stop being
  drawn at all. The game stops drawing anything mobile past 100 world units
  and structures past 160, which is why a zoomed-out battle is nothing but
  strategic icons; set a larger figure and the models keep going. This is the
  one setting here that isn't live — see below.

Presentation-side only: these are all client rendering flags the client's own
Lua already drives, so no simulation state is touched, no hashed file changes,
and a client running this is lobby-compatible with unmodded players.

**How it holds.** The flags have to be re-asserted — the game rewrites the icon
flag every render update, and units spawn with their rings on — so rather than
pushing a chunk across the FFI at frame rate, one chunk installs a small agent
in the client VM and C# pushes the wanted state only when it changes. The agent
wraps `rendering.RenderUpdate` (a field on the module table `clientMain` calls
through, so replacing it intercepts every frame without editing a file) and
runs after the game's own call:

- **icons** are the two engine calls above, per frame, so they follow the
  camera without lagging it;
- **range rings** are a per-unit, per-material flag the game itself never
  writes — it only moves the per-unit master, on intel and selection changes,
  which ANDs with ours — so a sweep four times a second is enough to catch
  units as they appear, and it skips any unit already at the wanted mask;
- **planned buildings** are ordinary client units with no build progress —
  placement ghosts — so the same sweep switches their renderer off. Only ever
  ones that are ours and visible right now, and only ever back on for ones the
  mod itself hid: writing the renderer on for a unit the game had hidden would
  reveal it;
- **health bars** go through the *global* bar scale, where 0 means don't
  render, because the per-unit master is rewritten every tick by
  `ClientUnit:UpdateProgressBars`. That scale is the one thing here that
  outlives a match while the agent holding it does not, so the sweep
  reconciles against the live value rather than against a remembered "already
  hidden", and never reads a zero back as the scale to restore — a match that
  inherited the zero from the last one would otherwise record it as the
  game's own value and keep the bars off for good (0.1.1 fixed exactly that);
- **the UI HUD** has a toggle and no getter, so the agent keeps its own belief
  of the state and only toggles on a change.

Order lines are the odd one out, because they aren't a flag: the order manager
tears down last tick's line and marker prefabs and rebuilds them from scratch
every tick, drawing either every army's orders (while the append key is held)
or the selected units' own. Skipping its draw would leave the previous tick's
prefabs on screen forever, so instead the mod wraps `DebugDraw` and lets the
game's own draw run with the all-armies view off and nothing selected — it
clears, finds nothing to draw, and stops. `SetOrderDraw` is wrapped alongside
purely to remember what the append key last asked for, so it can be handed
straight back.

**Draw distance is the exception**, and the only part of this mod that is a
Harmony patch rather than Lua. Every renderable entity carries an LOD component
holding up to six levels, each with a render distance; the culling system picks
the first level the camera is still inside, and past the last one the entity
simply isn't drawn. Units and structures are given exactly one level — 100 for
anything mobile, 160 for anything that isn't — so past that they are gone and
only the icon is left. Nothing exposes those numbers at runtime: the culling
system is a Burst job, and Lua has no setter for them. They exist in a
patchable, managed form in exactly one place, the point where a match's Lua
templates are turned into render prefabs, so that is where the mod raises them.

Two things follow. The distances are baked into the prefabs as a match loads,
so a change only takes effect when the next match or replay starts — the panel
says "next match" when what you've set isn't what the current one got. And only
single-level chains are touched: that's what a unit or structure has, and its
one distance is purely a cull distance with nothing cheaper to fall back to.
Props keep a real chain with an impostor on the end, so raising theirs would
hold full-detail meshes on screen at range; they're left as the game built them.
Worth knowing that units have no cheaper level either, so a wide shot of a big
battle now draws every model at full detail.

Unloading the mod, or switching it off on the Mods page, takes the wrapper back
off and puts every flag back. A fresh match brings a fresh Lua state, so the
agent reinstalls itself; a hot reload of the DLL finds it already there and
re-wraps without stacking (the version global carries a hash of the install
chunk, so an edit to it forces a reinstall rather than leaving the last build's
agent running).

## ModManager

A **Mods** entry in the front menu's sidebar (the cube icon, just below
Settings; **F8** opens it too) leading to a full page with two tabs, UI Mods
and Lua Mods. The page is the game's own Settings screen, cloned and refilled:
the tab bar, the switch rows, the sliders (UI Scale's row), the left/right
selectors (Window Mode's row), the text fields (a slider's input box,
widened), the headings and the buttons are all the game's Beam UI widgets, so
it looks like the rest of the menu and follows any restyling the game does.

Each UI mod's settings are listed in the order the mod bound them, so an
author's grouping is what the player reads: sections in first-appearance
order under their own heading, entries as bound within each. A bool is a
switch, a setting with an `AcceptableValueRange` a slider, one with an
`AcceptableValueList` a selector, anything else a text box. Key names show
as words (`HideGameEconomyBars` reads "Hide game economy bars"; `Eta`,
`Ui`, `Url` and `Pos` are expanded). A section that is nothing but hotkeys
(KeyCode settings, or strings whose description says "hotkey") is laid out
two to a row, which is what keeps BuildHotkeys' structure and unit lists
short.
**F8** also opens the same page full-screen during a match (over the menu
background, the way the pause menu's Settings does); closing it returns to
the game. UI mod toggles and settings changes apply immediately; Lua mod
toggles are locked until you leave the match.

It manages two kinds of mods:

**Lua mods** are a mod folder's `*.lua`/`*.santp` files, laid out mirroring the
`LJ\lua` tree (later mods win conflicts, and new files/folders are registered
so Lua directory listings see them). The manager overlays them into the
game's in-memory `FilesCache` — the
single source both the lobby hash and every match's Lua VMs read from — so
mods toggled in the main menu apply at the next match launch, no restart
needed, and nothing on disk is modified. Enabled mods persist in config and
re-apply on startup.

Multiplayer safety falls out of the game's own design: the lobby host compares
`ComputeLuaHash` of the *cache* (not the disk) against each joiner and refuses
mismatches, so everyone in a lobby provably runs the same Lua. The Lua Mods
tab shows the live hash for comparing with friends. Two caveats: `.santp` files
are loaded but **not** hashed (template mods must be coordinated manually or
they desync mid-game), and toggling is blocked while in a lobby or match. A
sample mod, `SanctuaryMods\ExamplePinkArmy`, turns army slot 1 hot pink as a
smoke test (safe to delete).

**UI mods** — the DLLs, every mod in this repo — each get a section headed
by the mod's name with its on/off switch inline. Sections start folded, one
row per mod; clicking a header unfolds that mod's settings beneath it. Off
destroys the plugin component (its `OnDestroy` unpatches Harmony) and on
creates it again. That is a component teardown, not an unload: .NET can't
unload an assembly from a running game, so the mod's code stays in memory
until the game exits, along with anything its `OnDestroy` doesn't undo. The
switched-off set persists across restarts, and with ModLoader 1.3 a
switched-off mod is never started at all: the loader checks the list before
it creates the plugin, so none of its code runs. A mod whose DLL is deleted
leaves the list. UI mods never enter the Lua hash, so they are safe to flip
any time, even mid-match. A mod held back since start-up has no settings to
show until it is switched on, because a mod binds its settings as it starts.

Each loaded mod's settings sit in its section — panel positions, the commander
zoom factor, `AssistStartsUpgrade`, hotkeys, anything a mod binds. The list
is read from the mod's BepInEx `ConfigFile`, so a mod's settings appear here
simply by being bound, with no work in the manager. Booleans get the game's
on/off switch; everything else is edited in a text field and committed
through the entry's own serializer (the same one that writes the config
file), so floats, enums and `KeyCode`s all work and a half-typed value just
doesn't take until it parses (it snaps back to the last good value when the
field loses focus). Each mod has a "Reset to defaults". Changes save to
`BepInEx\config\<guid>.cfg` immediately.

## MapLocalFiles

Lets Lua's `Engine.GetFileContent` see files inside the loaded map's folder,
so a converted map can carry its own decal blueprints under `map/...`. The
game's `EM.Lua.FilesCache` is built once at startup and never includes map
folders; this patches a lazy fallback on the miss path only, serving `map/`
paths from the loaded map's folder on disk. The hit path is untouched, so
shipped content behaves exactly as before. Served files live in native memory
that is freed when a different map loads or the mod unloads, and a file over
8 MB is refused.

## ModLoader

The one piece that lives inside the BepInEx tree (`BepInEx\plugins\ModLoader.dll`),
because BepInEx is what loads it. Everything else is a folder under
`engine\SanctuaryMods\`: the loader loads every `*.dll` it finds there at
start-up, watches them, and reloads any that change about a second after
the file is written (F6 forces a reload of everything; a deleted DLL has its
plugins torn down). A mod is installed by dropping its folder in and removed
by deleting it, with no restart either way, and `dotnet build` of a project
is the whole iteration loop while developing.

Reloading is not unloading, though. Mono can't remove a single assembly from
the running game, so each reload destroys the old plugin components and loads
a fresh copy of the assembly beside the old one. Every copy stays in memory
until the game exits, along with its static state and anything its
`OnDestroy` failed to undo, such as static event handlers or background
threads. The log counts the copies on each reload; after a long run of
rebuilds, restart the game.

Since 1.3 it is also the registry the [Mod Manager](#modmanager) reads. A
plugin switched off on the Mods page is held back before it is created, so
its `Awake` never runs, and the manager starts and stops plugins through the
loader, which refuses one whose DLL has since been deleted. An older Mod
Manager can't list a held-back plugin, so the loader only holds plugins back
when the manager installed says it can.

Two things it has to do that a plain BepInEx plugin would not: it rewrites
each assembly's identity per load, because Mono returns the cached assembly
for a byte-load with an identical name and a rebuild would silently keep
running the old code; and it attaches the plugins it loads to BepInEx's own
hidden manager object rather than a GameObject of its own, because Sanctuary
destroys foreign root GameObjects after start-up (the same reason BepInEx
needs `HideManagerGameObject = true` here, see Setup).

## Development

Layout: one folder per mod, each a tiny csproj — the shared settings (game
references, target framework, deploy step) live in
[Directory.Build.props](Directory.Build.props) /
[Directory.Build.targets](Directory.Build.targets), and the shared runtime
plumbing in [shared/](shared/) is compiled into the mods that reference it.

Every mod deploys to its own folder under `engine\SanctuaryMods\` — outside
the BepInEx tree, alongside the Lua mods, so one folder is the whole of a mod
whether it ships a DLL, Lua files, or both. The loader is the exception: it
deploys to `BepInEx\plugins`, because BepInEx is what loads *it* (see
[ModLoader](#modloader)). So `dotnet build` — of one project or the whole
`SanctuaryMods.sln` — is the entire iteration loop, no game restart.

### Cutting a release

[tools/pack-release.ps1](tools/pack-release.ps1) builds a mod's two zips and,
with `-Publish`, creates the GitHub release:

```powershell
pwsh -NoProfile -File tools/pack-release.ps1 -Mod EcoManager -Body body.txt
```

Version and display name come from the mod's `[BepInPlugin]` attribute, so that
attribute is the only place a version is written and a release cannot disagree
with what the game reports. `-Body` is plain text that lands in the `README.txt`
of both zips verbatim. Output goes to `release/` (gitignored), staged through a
temp folder so packing never disturbs a running game.

The BepInEx tree is copied from your own install by allowlist — never
`BepInEx\config\` wholesale, which holds your per-mod settings — plus the
vendored [tools/BepInEx.cfg](tools/BepInEx.cfg), which carries the
`HideManagerGameObject = true` that Sanctuary requires. Both zips are then
checked for the paths they must contain and for stray config before the script
will publish, and publishing refuses if the tag already exists.

The full checklist, including what *not* to re-release, is in
[.claude/skills/release-mod](.claude/skills/release-mod/SKILL.md).

## Setup

1. Install the .NET SDK (8+).
2. Install [BepInEx 5.4.x win-x64](https://github.com/BepInEx/BepInEx/releases)
   into the game's `engine` folder (the one with `Sanctuary.exe`) — extract so
   `winhttp.dll` and `BepInEx\` sit next to the exe. Run the game once to let
   BepInEx generate its folders.
   Then set `HideManagerGameObject = true` under `[Chainloader]` in
   `BepInEx\config\BepInEx.cfg`: Sanctuary destroys foreign root GameObjects
   after start-up, and without it the BepInEx manager object (and every
   plugin on it) dies right after `Awake`, so plugins load but never update.
   The release zips ship this setting.
3. `dotnet build SanctuaryMods.sln` — the projects reference game assemblies
   from the install (override with `-p:GamePath=...`, default is the playtest)
   and copy each built mod into `engine\SanctuaryMods\` automatically.
4. Launch the game; check `BepInEx\LogOutput.log` for the load lines, and
   open **Mods** from the menu's sidebar (or press **F8**).

## Removal

Delete `engine\winhttp.dll` and the `engine\BepInEx\` folder (and
`engine\SanctuaryMods\` if you used Lua mods). Steam's "verify integrity"
never sees any of them — they are not game files.
