---
name: game-patch
description: Check the mods against a new Sanctuary game build - snapshot the game, diff it against the last build, and find every name the mods use that moved (Engine functions, Lua files and members, reflected C# members, enum values). Use when the game updated, a patch dropped, Steam updated Sanctuary, mods broke after an update, or before releasing against a new build. Also the way to read the game's own code.
---

# After a game patch

The game's build id is in `steamapps\appmanifest_4511930.acf`. Snapshots live
outside the repo (it is the developers' code) in
`%LOCALAPPDATA%\SanctuaryRef\<buildid>\`: `cs\Trebuchet` and
`cs\Michsky.UI.Beam` (decompiled, one file per type), `lua\` (the game's Lua,
client + common + host + AI), `api.txt` (every type and member of the game's
assemblies, one line each, enums with values) and `api-engine.txt` (Unity,
BepInEx and the rest, for lookups).

## The sweep

```powershell
pwsh tools/game-ref.ps1 snapshot     # the installed build, if not done yet (~2 min)
pwsh tools/game-ref.ps1 diff         # previous snapshot -> this one
pwsh tools/game-ref.ps1 check        # the mods' source against this build
pwsh tools/lua-check.ps1             # every Lua chunk still compiles
dotnet build SanctuaryMods.sln "-p:DeployPath=<scratchpad>\deploy"   # compile against the new DLLs
```

`check` reports, with file:line, and marks with `<<` what the previous build
still had (a rename or removal by the patch):

- `lua-engine`: an `Engine.X` in neither engineFunctions.lua, or a host-only
  one called from client Lua (C# strings run in the client VM). Inside pcall
  these fail silently - 0.0.1.20's SetSimulationSpeed -> SetReplaySpeed did.
- `lua-import` / `lua-member`: an `Import('path')` into the game's tree that is
  gone, or a name read off a module that no longer appears in that file.
- `reflection` / `reflection-type`: AccessTools / FieldRefAccess /
  GetField("x") / HarmonyPatch / GetType("T") names the game no longer has.
- `enum-value`: a game enum member the mods use whose integer changed.
  Released DLLs carry the old int (0.0.1.20 shifted UIPanelType, so open chat
  read as "menu open"). Rebuild and re-release every mod that uses it.

`diff` lists removed/changed enum values, types and members, added types,
and the Lua files that changed (then `git diff --no-index` the two `lua`
folders for the content).

`check` is a list of suspects, not a verdict: confirm each in the snapshot's
code, fix, rebuild everything, test the paths that moved in game (the
in-game-test skill), and release the mods whose behaviour depended on them
(the release-mod skill; `-BuiltFor` names the new build).

## Reading the game's code any time

```powershell
$ref = pwsh tools/game-ref.ps1 path
```

Then Grep under `$ref\cs\Trebuchet` (namespaces are folders) and `$ref\lua`.
`api.txt` answers "which type has a member called X" in one grep, faster than
the decompile. For a type not in the snapshot assemblies,
`ilspycmd -t <Full.Type> <dll>`.
