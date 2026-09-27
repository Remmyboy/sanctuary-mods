# Writing a mod for Sanctuary: Shattered Sun

Anyone can write a mod on top of the Sanctuary mod framework and share it.
Players install a mod by dropping its folder into `engine\SanctuaryMods\`; the
framework loads it, hot-reloads it when its files change, lists it on the
**Mods** page, and — for mods that change the match — lets the lobby host pick
it and checks that every player has the same copy before the match starts.

A player with no mods at all can still play with anyone: outside a lobby the
game always runs vanilla, and a lobby with no gameplay mods picked is a vanilla
lobby.

## Two kinds of mod

| | **UI mod** | **Gameplay mod** |
| --- | --- | --- |
| What it changes | Your own screen: HUD, hotkeys, camera, info | The match itself: units, rules, AI, maps' Lua |
| Made of | A C# DLL | Lua and `.santp` files laid out like `LJ\lua`, optionally a DLL |
| Who switches it on | Each player, on the Mods page, any time (even mid-match) | The lobby host, in the lobby's **Mods** panel, before Start |
| Other players need it? | No | Yes, byte-identical, or Start stays greyed out |
| Hot reload | Rebuild and it reloads within a second, even mid-match | Lua: next match. DLL: reloads once the match ends |

## Quick start

With the .NET SDK installed:

```bash
dotnet new install <this repo>\templates\sanctuary-mod
dotnet new sanctuary-mod -n FasterTanks --kind gameplay --modAuthor "Alice"
cd FasterTanks
dotnet build
```

`dotnet build` compiles the mod and copies it into
`engine\SanctuaryMods\FasterTanks\`, where the loader picks it up within a
second, whether or not the game is running. The template's options:

- `--kind ui|gameplay` (default `ui`); a UI mod has no `lua\` folder.
- `--modAuthor "Your Name"` goes into `mod.json` and makes the mod's id unique
  (`alice.fastertanks`).
- `--gamePath "D:\...\engine"` if the game isn't in Steam's default folder.
  Or pass `-p:GamePath=...` to any build.

To build without touching the game (for example while you're playing), use
`dotnet build -p:DeployPath=some\other\folder`.

There are two worked examples in [`examples/`](../examples):

- **ExampleGameplayMod** is Lua only. It appends to `common/colors.lua` so the
  first army plays in pink. Copy the folder into `SanctuaryMods`, pick it in a
  lobby, and start a match against the AI.
- **ExampleUiMod** is C#. It shows which gameplay mods are live, binds
  settings that appear on the Mods page, and subscribes to match events.

## A mod folder

```
SanctuaryMods\
  FasterTanks\
    mod.json            what the mod is (below)
    FasterTanks.dll     optional: C#
    lua\                gameplay mods: mirrors the game's LJ\lua
      append\
        common\colors.lua   added to the end of common\colors.lua
      units\...\x.santp     replaces the game's file of that name
      fastertanks\util.lua  a new file: Import("fastertanks/util.lua")
```

One folder is the whole mod. Players install it by copying the folder in and
uninstall it by deleting the folder. There's no restart either way.

### mod.json

```json
{
  "id": "alice.fastertanks",
  "name": "Faster Tanks",
  "version": "1.0.0",
  "author": "Alice",
  "description": "Tanks move 20% faster.",
  "kind": "gameplay",
  "luaRoot": "lua",
  "url": "https://example.com/fastertanks",
  "requires": ["bob.tankcore"],
  "apiVersion": 1
}
```

| Field | Meaning |
| --- | --- |
| `id` | **Required.** 1–64 characters from `a-z 0-9 . _ -`. This is how lobbies name the mod to other players, so start it with your own name. |
| `name`, `version`, `author`, `description` | Shown on the Mods page and in the lobby. A player with a different `version` is told "has Faster Tanks 1.1 (host 1.2)". |
| `kind` | `"ui"` or `"gameplay"`. Only `"gameplay"` makes the folder's DLLs follow the lobby selection. A folder with Lua or `.santp` files is always gameplay. |
| `luaRoot` | The folder inside the mod that mirrors `LJ\lua`. Default `lua`. Nothing outside it is overlaid, so docs and scratch files are safe to keep in the mod folder. |
| `url` | Where to get the mod. It's shown to players who are missing it. It's never opened or fetched automatically. |
| `requires` | Ids of other gameplay mods that must be picked alongside this one. If one isn't, the lobby says so and holds Start. |
| `apiVersion` | The ModApi major version you wrote against (`1`). |

A folder with no `mod.json` still works the way mods worked before
manifests: its DLL is a UI mod, its Lua files are a gameplay mod whose id is
the folder name, and `luaRoot` is the folder itself.

**Same copy** means the same *content hash*. That is a SHA-256 over every
overlaid file's path and bytes, plus the DLLs of a gameplay mod. `mod.json`
isn't part of it, so rewording the description doesn't make you incompatible.
Anything else does, which is the point: two players whose Lua differs by one
byte would desync.

## Gameplay Lua

When the host starts a match, the framework swaps the picked mods' files into
the game's in-memory file cache on every player's machine. The game reads
all its Lua from that cache. Nothing on disk is changed, and after the match
the cache goes back to vanilla.

There are three ways a file can go in:

- **Append** (`lua\append\<path>`) adds your code to the end of the game's file, in
  the same Lua chunk, so the file's locals and globals are in scope and you can
  change what it defines. Many game files end in a top-level `return { ... }`;
  your code goes just before that return, so a function you replace also
  replaces the one the file hands out. Several mods can append to the same file,
  in the host's pick order. **Prefer this**: it survives game updates, and mods
  stack instead of clashing. Don't `return` from an append.
- **Replace** (`lua\<path>`, same path as a game file) swaps the file entirely.
  If two picked mods replace the same file, the one picked later wins, and the
  lobby panel warns about it.
- **Add** (`lua\<new path>`) creates a new file for your other files to
  `Import`.

Every file is compiled when the overlay goes on. A syntax error is logged
against your mod and file (`BepInEx\LogOutput.log`) instead of surfacing
halfway through a match.

Things that trip everyone up:

- **Game files are modules.** Every file runs in its own environment. Its
  "globals" live in the table `Import` returns, not in `_G`. From your own
  file, reach them through the module:
  `local Colors = Import("common/colors.lua").Colors`.
- **Host and client.** Only the host runs the simulation (`host\...`,
  `common\...` used by the host). Every player runs the client side
  (`client\...`). Both read the same overlaid files, which is why everyone
  needs the same copy.
- `.santp` unit templates are loaded by the game but aren't in the game's own
  Lua hash. The framework's content hash covers them, so the lobby still
  catches a mismatch.

## C# mods

A mod's DLL is a [BepInEx 5](https://docs.bepinex.dev/) plugin (`net472`,
`[BepInPlugin]` on a `BaseUnityPlugin`). The framework's loader creates it
instead of BepInEx, so that it can hot-reload. Reference the game's DLLs from
`engine\Sanctuary_Data\Managed` and the framework's from
`engine\BepInEx\plugins\Sanctuary.ModApi.dll`. The template sets this up, all
`Private=false`, so nothing gets copied beside your DLL.

### Hot reload rules

A rebuild destroys your plugin component and creates one from the new DLL.
Mono can't unload the old assembly, so it stays in memory with its static
state until the game exits. So:

1. **Undo everything in `OnDestroy`** that you set up in `Awake`: Harmony
   patches (`_harmony.UnpatchSelf()`), GameObjects you created, handlers on
   the game's static events, and Lua you injected.
2. **Use a new Harmony id per load**: `new Harmony("alice.fastertanks." + Guid.NewGuid())`.
3. **Keep state in fields, not statics**: the old copy's statics would
   linger, and the new copy starts with fresh ones.
4. **One DLL per mod.** Each DLL is loaded under a fresh name, so one mod DLL
   can't reference another. Libraries the game or BepInEx already have
   (Harmony, Newtonsoft.Json, Sanctuary.ModApi) are referenced, never shipped.
   The loader ignores copies found in `SanctuaryMods`.
5. **Settings**: anything you `Config.Bind` appears on the Mods page under
   your mod's name. A `bool` is a switch, an `AcceptableValueList<string>` a
   selector, an `AcceptableValueRange` a slider, anything else a text box.
   Settings save to `BepInEx\config\<your plugin GUID>.cfg`.

A **gameplay** mod's DLL (`"kind": "gameplay"`) is only created while a lobby
has picked the mod, and it's destroyed when the lobby or match ends. It runs on
every player's machine; `Modding.IsHost` tells you whether that machine runs
the simulation. Its rebuilds wait until the match is over.

### The API (`Sanctuary.ModApi`)

```csharp
using Sanctuary.ModApi;

Modding.Self(this)             // your ModInfo: Folder, Manifest, Id, Version...
Modding.FolderOf(this)         // for data files shipped beside the DLL
Modding.InLobby / IsHost / InMatch / InReplay
Modding.ActiveGameplayMods     // the gameplay mods live right now (empty = vanilla)
Modding.IsActive("bob.tankcore")

ModEvents.OnLobbyEntered(this, isHost => ...);
ModEvents.OnSelectionChanged(this, () => ...);   // gameplay mods live here changed
ModEvents.OnMatchStarting(this, () => ...);
ModEvents.OnMatchEnded(this, () => ...);
ModEvents.OnLobbyLeft(this, () => ...);
// Handlers are dropped when `this` is destroyed: a reload never calls an old copy.

ModLua.Ready                   // the client Lua VM exists (in a match or replay)
ModLua.Run("Log('hello')")     // run a chunk in your own client's VM
ModLua.GetGlobal("MyValue")    // read a _G global back as a string

Lobby.Selection                // the host's pick, as everyone sees it
Lobby.Players                  // who has what: Ok, Pending, Problem, Vanilla
Lobby.StartBlockedReason       // null, or why Start is held
Lobby.SetSelection(ids)        // host only, before Start

ModCatalog.Mods                // every mod folder, with manifest and content hash
```

`ModLua` is presentation-side: it runs in *your* client's VM only and never
reaches the host's simulation or other players. To change the match, ship Lua
in a gameplay mod instead.

The API's major version is `1`. Mods built against 1.x keep working on every
1.y.

## How the lobby decides

1. The host picks gameplay mods in the lobby's **Mods** panel. The **Mods**
   button beside Settings shows a count, and a `!` while someone is missing
   something. The pick also goes into the lobby chat. Players with the
   framework get the pick at once, apply the mods if their copies are
   identical, and report back.
2. **Start** stays greyed out while any player is missing a mod, has a
   different copy, or has no mod support at all. The panel and the chat say who
   and what: "Bob has Faster Tanks 1.1 (host 1.2)". The host can switch the mod
   off, or ask Bob to update.
3. With nothing picked there's no check at all, and players without any
   mods play as usual.
4. When the match starts the pick is frozen. The files stay exactly as they
   were until the match is over, even if you rebuild.
5. Replays of a modded match remember its mods (a `.mods.json` beside the
   replay) and put them back on to play, as long as they're installed and
   identical.

The lobby messages travel on the game's own lobby connection as message types
vanilla players silently ignore, so a modded host and vanilla players can
always see each other's lobbies.

## Sharing your mod

Zip the mod's folder (`FasterTanks\` with `mod.json`, the DLL and `lua\`)
and tell players to extract it into `engine\SanctuaryMods\`. Players need the
framework: any Standalone release from this repo includes it. Bump `version`
whenever the files change, so players with an old copy are told which one
they have.

Like every BepInEx plugin, a mod's DLL runs as full-trust code on the
player's PC. Only a mod's author can make it safe, so only publish what you'd
run yourself, and say where your source is.
