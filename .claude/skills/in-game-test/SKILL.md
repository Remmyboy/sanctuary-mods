---
name: in-game-test
description: Verify a mod change in the running Sanctuary game with nobody at the keyboard - the persistent probe plugin (tools/probe.ps1) runs Lua, screenshots, UI dumps, replays and skirmishes vs AI. Use when asked to test in game, check something live, reproduce a bug in a match, time something in game, or look at what the UI actually shows. Never use SendKeys or a throwaway probe.
---

# Testing in the game

Order of cheapness, use the first that answers the question:

1. **Offline** - `pwsh tools/lua-check.ps1` (syntax of every Lua chunk, with
   the game's own LuaJIT), `pwsh tools/game-ref.ps1 check` (names the mods use
   still exist), and the game's code in `pwsh tools/game-ref.ps1 path` (grep
   `cs\Trebuchet` and `lua\`). Many "does the game do X" questions end here.
2. **In game through the probe** - below.
3. **Ask the user** to look - for feel, taste and anything visual the probe's
   screenshot can't settle.

Verified end to end on 2026-10-01: install, launch, menu checks, `skirmish
white desert`, in-match Lua (values, errors, syntax errors), `pfield`,
`waitlua`, `tree`, `shot`, `leave`, `quit`, uninstall.

## Before touching the game

- `Get-Process Sanctuary` right before **every** write into the game folder.
  If it runs and the user hasn't said the match is a throwaway test, ask. The
  user plays while sessions run, and other sessions drive the game too:
  `pwsh tools/gamelog.ps1` shows whose probe is loaded and what is happening.
- Builds don't deploy while the game runs (Directory.Build.targets warns
  instead). `-p:LiveDeploy=true` hot-loads deliberately.
- Never stop the game by image name. Close it with the probe's `quit`, or
  `Stop-Process -Id` on a process you started yourself.

## The probe

`tools/Probe` is a dev-only plugin (never released). It polls
`%LOCALAPPDATA%\SanctuaryProbe\in\` and answers each batch in `out\`;
`tools/probe.ps1` does the round trip and prints the answer.

```powershell
pwsh tools/probe.ps1 install          # build + deploy (refuses while the game runs; -Force after asking)
pwsh tools/probe.ps1 launch           # Steam launch, waits until the probe reports
pwsh tools/probe.ps1 help             # every verb
pwsh tools/probe.ps1 state "eval Engine.GetSimulationTick()" "shot hud"
pwsh tools/probe.ps1 -File batch.txt  # one command per line, # comments
pwsh tools/probe.ps1 uninstall        # when done; leftover probes poll forever
```

Each argument is one line; a batch runs in order and holds on `wait N`,
`waitlua <secs> <expr>` and `waitmatch`, so a whole scenario is one call.

**Lua**: `lua <statements>` / `eval <expr>` run in the client VM inside
loadstring + pcall, so syntax and runtime errors come back as text (ModLua.Run
alone swallows them). Return values print, tables to `depth` levels. Game
files' globals are module-scoped: `Import('client/x.lua').Name`, not `Name`.
Selection: `Import('client/input/selectionSystem.lua')` (GetSelectedUnitsIds,
SetSelectedEntities). `luaf file.lua` runs a file - write it with the Write
tool, never printf (backslash paths break).

**A live match**: `skirmish [map]` = private lobby (The Forge unless named),
AI in slot 1 on team 2, ready, start, `waitmatch`. `maps [text]` lists the stock maps
and the installed map folders matching the text (`maps *` for all ~140); a map is named by part of its name
(`skirmish white desert`, `lobby 8 zone control`) or by its `Maps/...` path.
The four stock maps take 8 players. A replay instead: `replay latest`, `waitmatch`,
then `seek <tick>` / `speed <x>` / `replaystate` (ReplayManager must be loaded).
In a replay's ALL view every army reads as focused.

**UI**: `tree [path] [depth]` (active, CanvasGroup alpha, canvas sort, sprite,
text, size), `find <name>`, `texts [path]`, `click <path>`. `tap <path>` sends
pointer events and never `Button.onClick` (the Mods page's folding rows, the
lobby's Mods button and HUD tiles need it), `rtap <path>` with the right
button (take from a queue tile); `pointer <enter|exit|...> <path>`
sends one (hover a build tile for the build card, clear a stuck sidebar
highlight); `active <0|1> <path>` hides something for a shot; `scroll to <path>`
or `scroll <0..1> <path>` moves a list. Paths can have spaces; `path#n` is the
nth active match, for siblings that share a name (`Build options/Line/Tile#3`). Dump the tree
before guessing at a layout bug - three blind redeploys were spent on the
Mods page before a dump showed the sibling order.

**State**: `cfg <plugin> [Section Key [value]]` (writes the .cfg: restore it),
`pfield <plugin> <member> [value]` (a live instance field, e.g. force a panel
visible), `call <Type.Member> [args]` (statics; the newest hot-reload copy of
the type), `plugins` (stale copies show as "one of N copies").

**Screenshots**: `shot name` -> `%LOCALAPPDATA%\SanctuaryProbe\shots\name.png`;
read it with Read. Small details: `pwsh tools/crop.ps1 <png> x y w h -Scale 3`.
A shot in the same frame as an opening animation catches it half drawn: `wait 1`.

## Is my build the one running?

Every worktree deploys to the same `SanctuaryMods\<Mod>`, so the last build
from any session wins, and `0.2.*` assembly versions are build timestamps (a
stale DLL can show the higher number). `pwsh tools/gamelog.ps1 -Deployed`
lists each deployed DLL's commit, whether it is on main, which worktree built
it and the "built HH:MM UTC" of this launch's hot load. Check it before
debugging "my fix didn't work".

## Logs

`pwsh tools/gamelog.ps1` (`-Mod X`, `-Previous` after a crash,
`-Wait 'regex'` to block until a line appears). Exceptions thrown in mod code
reach only Player.log, not LogOutput.log.

## Known limits

- The game reports unfocused when nobody is at it; code gated on
  `Application.isFocused` won't run under the probe.
- Host-VM Lua is out of reach (the probe runs client Lua only); host effects
  are visible through their results (units, economy).
- `install` while the user plays hot-loads a plugin into their game - ask.
