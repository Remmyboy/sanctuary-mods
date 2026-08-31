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
