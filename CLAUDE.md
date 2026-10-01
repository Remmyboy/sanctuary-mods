# Sanctuary mods

BepInEx mods for Sanctuary: Shattered Sun (Playtest, Steam app 4511930). One
folder and one DLL per mod; `shared/` is source compiled into each mod, not a
shared DLL. `ModLoader` (BepInEx\plugins) hot-loads everything under the game's
`SanctuaryMods\`; `ModApi` is the framework (lobby protocol, gameplay mods, mod
options, Lua bridge) and ships inside the ModManager release. Lua-only gameplay
mods (ZoneControl) are a `mod.json` plus `lua/`. Modding guides for third
parties: `docs/writing-mods.md`, `docs/your-first-mod.md`, `templates/`,
`examples/`.

## Tools (tools/)

| Need | Use |
| --- | --- |
| Drive the running game: Lua, screenshots, UI tree, replays, skirmish vs AI | `pwsh tools/probe.ps1` - see the **in-game-test** skill |
| Read the game's C# and Lua for the installed build | `pwsh tools/game-ref.ps1 path`, then Grep `cs\Trebuchet`, `lua\`, `api.txt` |
| After a game patch: what moved, and which mod names broke | `pwsh tools/game-ref.ps1 snapshot`, `diff`, `check` - the **game-patch** skill |
| Syntax-check every Lua chunk (files and C# strings) with the game's LuaJIT | `pwsh tools/lua-check.ps1 [paths]` |
| What the game and mods logged, minus noise; wait for a log line | `pwsh tools/gamelog.ps1 [-Mod X] [-Previous] [-Wait regex]` |
| Which build of each mod is in the game, from which worktree | `pwsh tools/gamelog.ps1 -Deployed` |
| Uncommitted or unmerged work across all worktrees | `pwsh tools/worktrees.ps1 [-Detail]` |
| Past Claude sessions on this repo | `node tools/history.mjs list` / `search <regex>` / `show <id>` |
| Crop and enlarge a screenshot | `pwsh tools/crop.ps1 <png> x y w h -Scale 3` |
| Release zips and GitHub releases | `tools/pack-release.ps1` - the **release-mod** skill |

Prefer these over one-off scripts. If you build a throwaway tool twice, make
it one of these instead.

## The game is the user's

- The user plays while sessions run, and other sessions drive the game too.
  Run `Get-Process Sanctuary` right before every write into the game folder;
  if it is running and the user hasn't said it's a throwaway test, ask.
- A `dotnet build` deploys to the game folder, and the loader hot-loads it.
  While the game runs, Directory.Build.targets skips the deploy with a warning;
  `-p:LiveDeploy=true` deploys on purpose. `-p:DeployPath=<scratch dir>` builds
  without touching the game.
- Never stop the game by image name (`taskkill /IM`, `Stop-Process -Name`):
  that kills whatever the user or another session is running. Use the probe's
  `quit`, or `Stop-Process -Id` on a process you started.
- Never SendKeys or synthesise input into the game; use the probe.

## Paths

- Game: `C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine`
  (`Sanctuary_Data\Managed` for DLLs, `LJ\lua` for the game's Lua,
  `Sanctuary_Data\Plugins\x86_64\lua51.dll` is its LuaJIT).
- Logs: `BepInEx\LogOutput.log` (plugin loggers; overwritten each launch) and
  `%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Player.log`
  (Unity: exceptions from mod code land only here; `Player-prev.log` is the
  launch before). The `...\Sanctuary Shattered Sun\` folder beside it is a
  stale demo log. Crashes: `%TEMP%\Enhearten Media PTY\Sanctuary\Crashes`.
- Replays: `LocalLow\Enhearten Media PTY\Sanctuary\Replays`.
- Game reference snapshots: `%LOCALAPPDATA%\SanctuaryRef\<buildid>` (never commit them).

## Shell environment (Windows)

- `python` / `python3` / `py` are Python 3.11 with pip (uv-managed; the Store
  aliases are off since 2026-10-01). Node, dotnet, ilspycmd, gh and ffmpeg are
  installed. No zip, jq, strings or ImageMagick:
  use PowerShell `System.IO.Compression`, `ConvertFrom-Json`, node.
- Edit files with the Edit/Write tools, not sed/perl/heredocs: most C# files
  are CRLF (multi-line regexes silently miss), bash heredocs break on `'`,
  sed eats backslashes in Windows paths, and `printf` turns `\U` into an
  escape.
- PowerShell variables are case-insensitive (`$w` is `$W`); `R` and `Cls` are
  aliases; `Remove-Item` under Program Files is blocked by the harness; long
  `sleep`s are blocked - use Monitor or an until-loop. Prefer absolute paths:
  a `cd` persists between Bash calls.
- `grep -c` exits 1 on zero matches, which breaks `&&` chains.

## Working agreements

- Measure, don't assert: game rules, geometry and "the game exposes X" claims
  get checked in the game's code or in game first, and numbers Claude computed
  itself are labelled as such.
- Report what was and wasn't verified in game; "it compiles" is not "it works".
- Merge main into a branch rather than forcing; other sessions push to main
  mid-task, so fetch right before pushing.
