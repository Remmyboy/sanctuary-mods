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

Every release ships two zips. **Standalone** is everything — BepInEx, the mod
loader and the mod — for a clean install; extract it into the game's `engine`
folder. **ModManager** is just the mod, for an install that already has the
[Mod Manager](#modmanager); it appears under UI Mods and can be switched on and
off from there. Each mod builds to `<name>.dll`, and the project link is its
source.

| Project | Download | What it does |
| --- | --- | --- |
| [SanctuaryHud](SanctuaryHud/) | [**0.6.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/SanctuaryHud-0.6.0) | Economy strip + commander widget |
| [IdleEngineers](IdleEngineers/) | [**0.1.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/IdleEngineers-0.1.0) | Clickable idle-engineer panel |
| [EcoManager](EcoManager/) | [**0.3.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/EcoManager-0.3.0) | Alloy extractors by tier, plus upgrades in progress; assist starts an upgrade and holds it paused until the engineer arrives |
| [BuildHotkeys](BuildHotkeys/) | [**0.1.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/BuildHotkeys-0.1.0) | One hotkey per *role*, same key every faction, cycling by tier |
| [LadderReporter](LadderReporter/) | [**0.2.3**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/LadderReporter-0.2.3) | Reports ranked results; launches matchmade games |
| [ReplayManager](ReplayManager/) | [**0.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ReplayManager-0.2.0) | Watch the game's replays fog-free from any seat, with every economy |
| [CameraUtilities](CameraUtilities/) | [**0.1.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/CameraUtilities-0.1.1) | Switches off icons, range rings, order lines and the UI, and unlocks how far out units are drawn, for cinematics |
| [ModManager](ModManager/) | [**0.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.2.0) | Mods page in the front menu: mod toggles, settings, Lua overlays |
| [MapLocalFiles](MapLocalFiles/) | — | Lets Lua read files from the loaded map's folder |
| [ModLoader](ModLoader/) | [**1.2.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModLoader-1.2.0) | Loads and hot-reloads every mod above from `SanctuaryMods` |

[All releases](https://github.com/Remmyboy/sanctuary-mods/releases) · MapLocalFiles
has no release of its own yet; build it from source if you need it.

## SanctuaryHud

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `STALL −N/s` appears when the economy can't pay for what is
  queued. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
  receiver of Lua's `Engine.UI_SetEconomyValues`.

  The spend figure is what your queue is **asking for**, not what the economy
  managed to pay (`RequestedTotal` rather than `RequestedStalled`). The two are
  equal until you stall; during a stall actual spend is capped by income, so
  showing it would just mirror the income back at you (`+12 −12`) and hide the
  shortfall. Net stays on actual spend, since that is what really moves the
  store.
- **Commander widget** top-right: the game's own strategic icon with a health
  bar underneath; click to select the commander and move the camera to it,
  keeping roughly your current zoom.

Hotkeys: **F10** toggles the overlay, **F9** dumps the UI hierarchy to the log.

## IdleEngineers

One clickable row per tech tier of idle engineers (plus a COMMANDER row and an
ALL row) — clicking selects that group. Hidden entirely when nothing is idle;
draggable, and its position persists.

Idle state and unit identity come from the DOTS icon buffers rather than
Harmony hooks, because the icon FFI receivers are Burst-compiled and cannot be
patched. Selection and camera moves run through the client's own Lua via an
emitted call to `luaL_dostring` — client-side only, so still MP-safe. That
plumbing lives in [shared/HudCore.cs](shared/HudCore.cs), which is compiled
into each mod that needs it — so each DLL is fully standalone, at the cost of
each running its own copy of the once-a-second poll.

## EcoManager

A small **ALLOY** panel: one clickable row per extractor tier (T1/T2/T3, plus
an ALL row), and — only while something is actually upgrading — an
**UPGRADING** block underneath listing those by tier. Clicking any row selects
that group. Hidden until you have your first extractor; draggable, position
persists.

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

**Escape clears the build queue** of every selected factory, as it does in FAF,
rather than opening the pause menu. Each entry goes out exactly as a
right-click on its queue button would — the host request first, since it reads
the queue by index, then the local prediction. With nothing queued the key
falls through untouched, so escape still opens the menu; and because there is
no getter for panel visibility, only a setter, the mod mirrors the menu's state
by watching that setter (which the menu's own close button goes through too) so
escape still *closes* the menu rather than clearing a queue behind it.
`Cancel.ClearFactoryQueue` rebinds or blanks it.

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
underlined, the rest faded.

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
Steam session: at report time the mod mints a Steam web-API ticket and sends
it with the result, so a report is exactly as trustworthy as being signed in
to Steam in the running game. Only Steam lobbies with exactly two human
players are reported; skirmish, LAN, observers and team games are recognised
and left alone.

**Matchmaking.** The site pairs queued players, picks map, factions, slots
and host, and runs the countdown. While the game is open the mod heartbeats
(`POST /api/mm/heartbeat`, every 5 s, with a bearer token from one Steam
ticket) so the site knows who has the game in the main menu with the mod.
Nobody needs the mod to queue: the site only picks the automatic path when
*both* players are heartbeating, and falls back to today's manual hosting
otherwise. When a match reaches `launch`:

- the host's mod creates the lobby on the assigned map (`CreateLobby`),
  moves the UI to it, posts the session ID to the site, seats itself
  (faction, slot, ready), kicks anyone who isn't the assigned opponent, and
  starts the game as soon as the joiner is seated and ready;
- the joiner's mod sees the session ID on its next heartbeat and joins by ID
  through the same public entry point Steam "join game" uses
  (`InterfaceManager.JoinSessionFromInvite`), then seats itself.

Both bring the game window back if it's minimised or buried (a `user32`
restore, with a taskbar flash when Windows refuses focus), post progress
events, and mirror the site's timeouts locally so both sides converge if a
heartbeat is late. Any failure leaves the lobby, tells the player why in a
small overlay, and points at manual hosting. A matchmade game's result
report carries its `matchId` so the site can close the match.

Everything the site side needs is in [docs/matchmaking-site-plan.md](docs/matchmaking-site-plan.md).
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
`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary Shattered Sun\Replays\*.sanreplay`
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
the tab bar, the switch rows, the text fields (a settings slider's input box,
widened), the headings and the buttons are all the game's Beam UI widgets, so
it looks like the rest of the menu and follows any restyling the game does.
It lives in the menu canvas, so there is no in-match UI; UI mod toggles and
settings changes made in the menu apply immediately anyway.

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

**UI mods** — the DLLs, every mod in this repo — are listed with toggles: off
destroys the plugin component (its `OnDestroy` unpatches Harmony, so it is a
genuine unload) and on adds it back. They never enter the Lua hash, so they
are safe to flip any time, even mid-match, and the disabled set persists
across restarts.

Each loaded mod's settings follow its row — panel positions, the commander
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
shipped content behaves exactly as before.

## ModLoader

The one piece that lives inside the BepInEx tree (`BepInEx\plugins\ModLoader.dll`),
because BepInEx is what loads it. Everything else is a folder under
`engine\SanctuaryMods\`: the loader loads every `*.dll` it finds there at
start-up, watches them, and reloads any that change about a second after
the file is written (F6 forces a reload of everything; a deleted DLL has its
plugins torn down). A mod is installed by dropping its folder in and removed
by deleting it, with no restart either way, and `dotnet build` of a project
is the whole iteration loop while developing.

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
