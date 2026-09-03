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

| Project | DLL | What it does |
| --- | --- | --- |
| [SanctuaryHud](SanctuaryHud/) | `SanctuaryHud.dll` | Economy strip + commander widget |
| [IdleEngineers](IdleEngineers/) | `IdleEngineers.dll` | Clickable idle-engineer panel |
| [EcoManager](EcoManager/) | `EcoManager.dll` | Alloy extractors by tier, plus upgrades in progress |
| [LadderReporter](LadderReporter/) | `LadderReporter.dll` | Reports ranked results; launches matchmade games |
| [Replays](Replays/) | `Replays.dll` | Records matches; plays them back fog-free from any seat |
| [ModManager](ModManager/) | `ModManager.dll` | F8 window: Lua mod overlays + plugin toggles |
| [MapLocalFiles](MapLocalFiles/) | `MapLocalFiles.dll` | Lets Lua read files from the loaded map's folder |
| [SanctuaryHudLoader](SanctuaryHudLoader/) | `SanctuaryHudLoader.dll` | Hot-reload host for all of the above |

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

## Replays

Records every match to a file and plays it back inside the game with the
fog lifted, from any player's seat, with every army's economy on screen.
Press **F7** in the main menu for the browser; during playback F7 shows and
hides the control panel. Replays land in `Documents\Sanctuary Replays` as
`202609031240_There_Is_Time_Remmy_vs_Skoub.sanrep` — date to the minute,
map, then the seats grouped by team (folder and key are configurable from
the F8 window).

**Why it works.** Only the host simulates. Every client receives one packet
per sim tick (ten a second) holding the host's ordered command stream — unit
spawns, health, move orders, the Lua-level custom commands — and the host
appends the *same bytes* to every connection: there is no per-client
filtering. Fog is a client-side test of each unit's intel bitmask against the
focused army, and the economy totals of every army are broadcast and simply
dropped by the client when they aren't the focused army's. So any client's
inbound stream is a full-information record of the match, and the game
already knows how to turn that stream into a picture.

**Recording** is a Harmony prefix on the client's message parser
(`NetworkManager.HandleMessage`), writing each host packet behind a small
JSON header (map, players, game version, Lua hash). It starts on the launch
message and stops when the client is torn down, on the host machine as much
as on a joiner's (the host's own client is a socket client too). Nothing in
the Lua tree or the simulation is touched, so recording is lobby-safe. While
a match is open the file is a raw `.part`, flushed every couple of seconds;
on leaving it is compressed in the background. A game that crashed still
leaves a playable `.part`.

The game Brotli-compresses each packet field on its own, which leaves a
second compressor nothing to find, so the recorder decodes the fields with
the game's own `NetworkUtils.DecompressData` and stores them plain; one
long-window Brotli pass over the whole file then sees how alike consecutive
ticks are. A seven-minute 1v1 is about 0.7 MB (2.7× smaller than gzip over
the wire bytes). Maps that spawn tens of thousands of props at start add a
fixed per-map cost that does not compress well. The format is
`"SANREP02" jsonLen json encoding` then frames of
`kind simTick len bytes`; kind 0 is decoded fields, kind 1 the packet as it
came off the socket (the fallback if decoding ever fails); `encoding` is 0
for a raw part and 1 once everything after it is one Brotli stream.
Format-1 files (gzipped wire packets) still load. `BrotliStream` is reached
by reflection because the .NET Framework reference assembly this compiles
against lacks it and MSBuild prefers that one by version.

**Playback** creates a client context with no socket — the game's own send
path returns early without a server connection — points the map loader at the
recorded map, and starts the client exactly as a joiner would. Once the
loading screen reports it is waiting for players, a feeder pushes recorded
packets into the same receive buffer the network layer fills (decoded frames
are re-encoded at the fastest Brotli setting first, so the game's
deserialiser is used unchanged), pacing them by a clock the panel controls.
The client's own rate manager does the rest: it only steps a tick once that
tick's packet is buffered, so pausing is feeding nothing and fast-forward is
feeding ahead (the client catches up as fast as it can simulate). Rewind has
no snapshots to use, so it goes through the game's own quit path (scene
reload), relaunches the client, and fast-forwards to the target; speed,
pause state and the viewed army are put back afterwards. The recorded
per-tick hash is the same one the game uses for its desync check, so a
replay that diverges is detectable.

**The panel** has the clock, play/pause, a log-scale speed slider (0.25× to
8×), ±1 minute, a FOG toggle for the post-process overlay, a TIMELINE toggle
that hides the total length and the seek bar for watching without knowing
when the game ends, and one row per army: the name button (in the army's own
colour) switches to that army's view, then alloy and energy as a storage bar,
net / in / out per second, and the amount used so far in the game. ALL is the
game's own all-armies observer mode. The browser lists replays newest first
with map, length and players, offers each human's seat, and flags a replay
recorded on a different game build.

**Seats and fog.** The player whose view you open from is just the client ID
the packets are read as: the host's `InitClient` message for that seat is in
every packet, so any human's seat is available. Once in, the view buttons
call the client's own `SetFocusArmy` — `-1` shows every army — and the fog
post-process is switched with the focus (or by hand). The client is marked
an observer so clicks can't issue orders into the void. Every army's economy
comes from a runtime wrapper around the client's `UpdateEconomyTotals`
receiver that keeps all armies' totals and sums income and spend per tick
for the whole-game figures; the receiver's format table is a `local` in
`commands.lua`, reached as an upvalue of the original function (with a
literal copy as fallback).

**Caveats.** A replay is tied to the game build and the map files it was
recorded with; a patch that changes a command format will break old files.
Playback is a normal client, so the other mods in this repo behave as they
would in a match and follow the focused army.

## ModManager

Its own window on **F8**, managing two kinds of mods:

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
mismatches, so everyone in a lobby provably runs the same Lua. The F8 window
shows the live hash for comparing with friends. Two caveats: `.santp` files
are loaded but **not** hashed (template mods must be coordinated manually or
they desync mid-game), and toggling is blocked while in a lobby or match. A
sample mod, `SanctuaryMods\ExamplePinkArmy`, turns army slot 1 hot pink as a
smoke test (safe to delete).

**UI mods** — the DLLs, every mod in this repo — are listed with toggles: off
destroys the plugin component (its `OnDestroy` unpatches Harmony, so it is a
genuine unload) and on adds it back. They never enter the Lua hash, so they
are safe to flip any time, even mid-match, and the disabled set persists
across restarts.

Each loaded mod also has a **settings** button that expands its own settings
inline — panel positions, the commander zoom factor, `AssistStartsUpgrade`,
hotkeys, anything a mod binds. The list is read from the mod's BepInEx
`ConfigFile`, so a mod's settings appear here simply by being bound, with no
work in the manager. Booleans get a checkbox; everything else is edited as
text and committed through the entry's own serializer (the same one that
writes the config file), so floats, enums and `KeyCode`s all work and a
half-typed value just doesn't take until it parses. Hovering a setting shows
its description along the bottom of the window, and each mod has a "Reset to
defaults". Changes save to `BepInEx\config\<guid>.cfg` immediately.

## MapLocalFiles

Lets Lua's `Engine.GetFileContent` see files inside the loaded map's folder,
so a converted map can carry its own decal blueprints under `map/...`. The
game's `EM.Lua.FilesCache` is built once at startup and never includes map
folders; this patches a lazy fallback on the miss path only, serving `map/`
paths from the loaded map's folder on disk. The hit path is untouched, so
shipped content behaves exactly as before.

## Development

Layout: one folder per mod, each a tiny csproj — the shared settings (game
references, target framework, deploy step) live in
[Directory.Build.props](Directory.Build.props) /
[Directory.Build.targets](Directory.Build.targets), and the shared runtime
plumbing in [shared/](shared/) is compiled into the mods that reference it.

Every mod deploys to its own folder under `engine\SanctuaryMods\` — outside
the BepInEx tree, alongside the Lua mods, so one folder is the whole of a mod
whether it ships a DLL, Lua files, or both. The loader is the exception: it
deploys to `BepInEx\plugins`, because BepInEx is what loads *it*. It then
hot-reloads each mod DLL independently about a second after its rebuild (F6
forces a reload of everything, and a deleted DLL has its plugins torn down).
So `dotnet build` — of one project or the whole `SanctuaryMods.sln` — is the
entire iteration loop, no game restart. The loader rewrites each assembly's
identity per load because Mono caches byte-loaded assemblies, and attaches
plugins to BepInEx's hidden manager object because this game destroys unknown
root GameObjects.

## Setup

1. Install the .NET SDK (8+).
2. Install [BepInEx 5.4.x win-x64](https://github.com/BepInEx/BepInEx/releases)
   into the game's `engine` folder (the one with `Sanctuary.exe`) — extract so
   `winhttp.dll` and `BepInEx\` sit next to the exe. Run the game once to let
   BepInEx generate its folders.
3. `dotnet build SanctuaryMods.sln` — the projects reference game assemblies
   from the install (override with `-p:GamePath=...`, default is the playtest)
   and copy each built mod into `engine\SanctuaryMods\` automatically.
4. Launch the game; check `BepInEx\LogOutput.log` for the load lines, and
   press **F8** in the menu for the mod manager.

## Removal

Delete `engine\winhttp.dll` and the `engine\BepInEx\` folder (and
`engine\SanctuaryMods\` if you used Lua mods). Steam's "verify integrity"
never sees any of them — they are not game files.
