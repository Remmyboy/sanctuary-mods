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
| [LanLobbyUnlock](LanLobbyUnlock/) | `LanLobbyUnlock.dll` | Opens the menu when the entitlement API is dead |
| [MapLocalFiles](MapLocalFiles/) | `MapLocalFiles.dll` | Lets Lua read files from the loaded map's folder |
| [SanctuaryHudLoader](SanctuaryHudLoader/) | `SanctuaryHudLoader.dll` | Hot-reload host for all of the above |

## SanctuaryHud

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `STALL −N/s` appears when the economy can't pay for what is
  queued. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
  receiver of Lua's `Engine.UI_SetEconomyValues`.
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

Extractors are identified by their strategic icon, `structure1_t{n}_alloy_normal`,
which is uniform across all three factions, so the tier comes straight off the
icon. Upgrading state is the game's own upgrade adornment — the one
`ClientUnit:CheckShowUpgradingAdornment` drives from `IsUpgradeQueued()` — so it
lights and clears exactly in step with the icon the game itself draws. An
upgrading extractor is counted in both its tier row and the upgrading row,
since it is still live at its current tier until the upgrade completes.

Rows are labelled by tier rather than "extractor" for a reason: the Tier-3
Alloy Furnace (`ues3603` and faction equivalents, tagged `ALLOYS_PRODUCTION`
rather than `ALLOYS_EXTRACTION`) carries that same strategic icon, and nothing
on the render entity separates the two — so the T3 row means "tier-3 alloy
structures", furnaces included.

## ModManager

Its own window on **F8**, managing two kinds of mods:

**Lua mods** live in `engine\SanctuaryMods\<ModName>\`, each mirroring the
`LJ\lua` tree; only `*.lua` and `*.santp` are applied (later mods win
conflicts, and new files/folders are registered so Lua directory listings see
them). The manager overlays them into the game's in-memory `FilesCache` — the
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

**C# plugins** (every mod in this repo) are listed with toggles: off destroys
the plugin component — its `OnDestroy` unpatches Harmony, so it is a genuine
unload — and on adds it back. C# plugins never enter the Lua hash, so these
are safe to flip any time, even mid-match, and the disabled set persists
across restarts.

## LanLobbyUnlock

One mod is not presentation-side, so it is worth stating plainly.
`LanLobbyUnlock` lets the main menu open when the entitlement API is
unreachable.

`EM.UI.InterfaceManager.Start()` calls `SssApiClient.GetPermissions(steamId, …)`
against the developers' `PermissionCheck` endpoint. A request error and a
`HasMulti == false` response both land on `MainMenuInterface.OnPermissionDenied()`,
which raises a full-screen canvas whose only button is `Application.Quit()`. With
the demo's multiplayer backend closed that request just errors, which also shuts
off **Multiplayer LAN** — hosted locally by `TcpLobbyBackend`, needing no servers
at all. There is no config or launch flag for this: `InterfaceManager.TryAutoStart()`
would host over LAN with no menu, but nothing in the build calls it, and
Singleplayer is a stub that logs `"Not implemented yet!"`.

The patch flips `HasMulti` on this client and routes `OnPermissionDenied` to
`OnPermissionsPassed`. It grants no server access, and deliberately leaves
`HasCampaign` and `HasDev` as the API returned them — those gate unreleased
content rather than a dead server check. It exists so custom maps can be played
against AI offline. On builds where the entitlement check passes (the playtest),
the patch never fires. Unload it from the F8 manager before sharing a build.

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

Every mod deploys to `BepInEx\scripts`; the loader deploys to
`BepInEx\plugins` and hot-reloads each scripts DLL independently about a
second after its rebuild (F6 forces a reload of everything, and a deleted DLL
has its plugins torn down). So `dotnet build` — of one project or the whole
`SanctuaryMods.sln` — is the entire iteration loop, no game restart. The
loader rewrites each assembly's identity per load because Mono caches
byte-loaded assemblies, and attaches plugins to BepInEx's hidden manager
object because this game destroys unknown root GameObjects.

## Setup

1. Install the .NET SDK (8+).
2. Install [BepInEx 5.4.x win-x64](https://github.com/BepInEx/BepInEx/releases)
   into the game's `engine` folder (the one with `Sanctuary.exe`) — extract so
   `winhttp.dll` and `BepInEx\` sit next to the exe. Run the game once to let
   BepInEx generate its folders.
3. `dotnet build SanctuaryMods.sln` — the projects reference game assemblies
   from the install (override with `-p:GamePath=...`, default is the playtest)
   and copy each built mod into the game automatically.
4. Launch the game; check `BepInEx\LogOutput.log` for the load lines, and
   press **F8** in the menu for the mod manager.

## Removal

Delete `engine\winhttp.dll` and the `engine\BepInEx\` folder (and
`engine\SanctuaryMods\` if you used Lua mods). Steam's "verify integrity"
never sees any of them — they are not game files.
