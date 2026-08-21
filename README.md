# SanctuaryHud

Client-side HUD mod for _Sanctuary: Shattered Sun_ (demo, `engine` build), as a
BepInEx plugin. Presentation-side only: it never touches the game's Lua tree
(which the multiplayer lobby hashes — `ComputeLuaHash` over `*.lua`/`*.santp`
under `engine\LJ\lua\`) and never touches the simulation (which is hash-checked
per tick between players), so a modded client stays lobby-compatible with
unmodded players.

## Features

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `STALL −N/s` appears when the economy can't pay for what is
  queued. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
  receiver of Lua's `Engine.UI_SetEconomyValues`.
- **Idle panel**: one clickable row per tech tier of idle engineers (plus a
  COMMANDER row and an ALL row) — clicking selects that group. Hidden entirely
  when nothing is idle; draggable, and its position persists.
- **Commander widget** top-right: the game's own strategic icon with a health
  bar underneath; click to select the commander and move the camera to it,
  keeping roughly your current zoom.

Idle state and unit identity come from the DOTS icon buffers rather than
Harmony hooks, because the icon FFI receivers are Burst-compiled and cannot be
patched. Selection and camera moves run through the client's own Lua via an
emitted call to `luaL_dostring` — client-side only, so still MP-safe.

Hotkeys: **F10** toggles the overlay, **F9** dumps the UI hierarchy to the log.
Settings (overlay position, commander jump zoom) live in
`BepInEx\config\com.sanctuarydb.hud.cfg`.

## Development

Two projects: `SanctuaryHud` is the mod itself, deployed to `BepInEx\scripts`;
`SanctuaryHudLoader` is a small hot-reload host deployed to `BepInEx\plugins`.
The loader watches the scripts folder and reloads the mod about a second after
each build (F6 forces it), so `dotnet build` is the whole iteration loop — no
game restart. It rewrites the assembly identity per load because Mono caches
byte-loaded assemblies, and attaches plugins to BepInEx's hidden manager object
because this game destroys unknown root GameObjects.

## Setup

1. Install the .NET SDK (8+).
2. Install [BepInEx 5.4.x win-x64](https://github.com/BepInEx/BepInEx/releases)
   into the game's `engine` folder (the one with `Sanctuary.exe`) — extract so
   `winhttp.dll` and `BepInEx\` sit next to the exe. Run the game once to let
   BepInEx generate its folders.
3. `dotnet build` — the csproj references game assemblies from the install
   (override the location with `-p:GamePath=...`) and copies the built plugin
   into `BepInEx\plugins\` automatically.
4. Launch the game; check `BepInEx\LogOutput.log` for the load line.

## Removal

Delete `engine\winhttp.dll` and the `engine\BepInEx\` folder. Steam's
"verify integrity" also never sees them — they are not game files.
