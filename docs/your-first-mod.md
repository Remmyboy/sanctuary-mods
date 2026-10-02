# Your first mod for the Mod Manager

This walks you through making a gameplay mod for Sanctuary: Shattered Sun,
from an empty folder to a match where it runs, and then sharing it. You need
no programming tools: a text editor is enough. It takes about half an hour.

The mod you'll build does two things:

1. It paints one army's units a loud colour, so you can see at a glance that
   it's working.
2. Every few minutes it gives every army a drop of energy, with the lobby
   host choosing how much.

Along the way you'll use everything most gameplay mods are made of: the
`mod.json` file, an **append** to one of the game's files, **options** the
host sets in the lobby, and a **host script** with a timer. Once you've done
it, [writing-mods.md](writing-mods.md) is the full reference.

## What you need

- **The game with the Mod Manager installed.** Download the **Standalone**
  zip of the [latest ModManager release](https://github.com/Remmyboy/sanctuary-mods/releases)
  and extract it into the game's `engine` folder, the one with `Sanctuary.exe`
  in it. On a default Steam install that's
  `C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine`.
  This page calls it `engine` from here on.
- **A text editor.** Anything works. [VS Code](https://code.visualstudio.com/)
  is a good choice, because it checks `mod.json` as you type (step 2 turns
  that on).

## How a gameplay mod works

A gameplay mod is a folder of Lua files (the language the game's rules are
written in) laid out like the game's own Lua folder, `engine\LJ\lua`. When the
host of a lobby picks your mod, every player's game swaps your files in
before the match starts. Nothing on disk is changed, and after the match the
game goes back to vanilla.

Because your files change the match itself, every player needs an identical
copy. The lobby checks this, and holds Start until everyone matches.

## 1. Make the folder

In `engine\SanctuaryMods`, make a folder called `FirstMod`. Inside it, make a
folder called `lua`. By the end, the mod will look like this:

```
engine\SanctuaryMods\
  FirstMod\
    mod.json                  what the mod is
    lua\
      append\
        common\
          colors.lua          added to the end of the game's common\colors.lua
      firstmod\
        host.lua              the host script: the mod's rules
```

`lua\` mirrors the game's `engine\LJ\lua`. A file under `lua\append\` is
added to the end of the game's file at the same path. Any other file is a
file of your own, or replaces the game's file at that path.

## 2. Write mod.json

Make `FirstMod\mod.json`:

```json
{
  "$schema": "https://raw.githubusercontent.com/Remmyboy/sanctuary-mods/main/docs/mod.schema.json",
  "id": "yourname.firstmod",
  "name": "My First Mod",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Paints army 1 hot pink, and drops energy on every army now and then.",
  "kind": "gameplay",
  "luaRoot": "lua",
  "gameVersion": "0.0.1.20",
  "apiVersion": 1
}
```

- **`id`** is how lobbies tell your mod apart from everyone else's. Use your
  own name in front of it, in lower case, with no spaces (`a-z`, `0-9`, `.`,
  `_` and `-`). Never change it once people have your mod.
- **`kind`** is `"gameplay"`, because this mod changes the match. (A mod
  that only changes your own screen, a HUD for example, is a UI mod: a C#
  DLL, covered in [writing-mods.md](writing-mods.md#c-mods).)
- **`gameVersion`** is the version of the game you tested with (it's shown on
  the game's main menu). After a game update, players can see which mods
  might need one too.
- **`$schema`** is optional. In VS Code it underlines mistakes and suggests
  the fields as you type.

Save it, then start the game (or switch to it if it's running: the Mod
Manager looks at the folder every couple of seconds). Open **Mods** in the
menu's sidebar (or press F8) and go to the **Gameplay Mods** tab. **My First
Mod 1.0.0** is listed under **Installed**. It has no files yet, so it's also
listed under **Problems** with "nothing to apply". That goes away in the next
step.

If it isn't listed at all, check that the file is called `mod.json` (not
`mod.json.txt`: Windows hides extensions by default) and sits directly in
`FirstMod`.

## 3. Change a game file: paint an army

The game's army colours live in `engine\LJ\lua\common\colors.lua`. Open it
(read-only: never edit the game's own files) and you'll find a table called
`ArmyColors`. The armies take its colours in the order they're set up, which
is normally seat order: the first seat's army gets `ArmyColors[1]`.

Make `FirstMod\lua\append\common\colors.lua`:

```lua
-- Added to the end of the game's common/colors.lua, so everything that file
-- defines (Colors, ArmyColors) is in scope here.

ArmyColors[1] = EngineClasses.float4(1, 0.2, 0.7, 1)   -- red, green, blue, opacity
```

That's an **append**. Your code runs inside the game's file, at the end, as
if it had been written there, so it can change anything the file defines.
Prefer an append to replacing a whole file: it keeps working when a game
update changes the rest of the file, and several mods can append to the same
file without clashing.

## 4. Play it

1. From the main menu, host a custom lobby.
2. Add an AI to the second slot.
3. Click **Mods**, beside the lobby's **Settings** button. The Mods panel
   lists your installed gameplay mods. Switch on **My First Mod**. The lobby
   chat says "Gameplay mods: My First Mod 1.0.0", and the panel's top line
   says "Everyone has them: ready to start".
4. Close the panel and press **Start**.

The first seat's army (yours, when you host from the first seat) is hot
pink. That's a working mod.

If the match doesn't start or your army isn't pink, look at
[When something goes wrong](#when-something-goes-wrong).

Every lobby starts with no gameplay mods picked, so you have to switch it on
each time you host. Outside a lobby the game is always vanilla, so your mod
never gets in the way when you join someone else's game.

## 5. Let the host choose: options

A mod can offer settings that the lobby host picks. Add an `options` list to
`mod.json`, after `apiVersion` (mind the comma):

```json
  "apiVersion": 1,
  "options": [
    {
      "key": "color",
      "label": "Colour",
      "type": "choice",
      "default": "pink",
      "choices": [
        { "value": "pink", "label": "Hot pink" },
        { "value": "lime", "label": "Lime" },
        { "value": "gold", "label": "Gold" }
      ]
    },
    {
      "key": "army",
      "label": "Army",
      "type": "number",
      "default": 1, "min": 1, "max": 8, "step": 1
    }
  ]
```

Then read them in your append. The framework writes the host's values into a
Lua file named after your mod id, on every player's machine, before any of
your files run:

```lua
local Options = Import("modoptions/yourname.firstmod.lua").Options

local palette = {
    pink = EngineClasses.float4(1, 0.2, 0.7, 1),
    lime = EngineClasses.float4(0.6, 1, 0.1, 1),
    gold = EngineClasses.float4(1, 0.78, 0.1, 1),
}

ArmyColors[Options.army] = palette[Options.color]
```

Host a lobby again and switch the mod on. The options appear under it in the
Mods panel: a selector for the colour and a slider for the army. The other
players see the values, and the chat says what changed. Your picks are
remembered for the next lobby you host.

Every option always has a value (the host's, or the default), so you never
need to check for `nil`. There are three types: `toggle` (on or off),
`choice`, and `number`. [writing-mods.md](writing-mods.md#options-let-the-host-tune-your-mod)
has every field.

## 6. Add a rule: a host script with a timer

Colours are for looking at. Rules, such as what can be built, who gets what
and who wins, belong in the **host**. The host is the player whose machine
runs the simulation; everyone else's game shows what the host's decides.

Tell the framework about your host script in `mod.json`, next to `luaRoot`:

```json
  "luaRoot": "lua",
  "hostScript": "firstmod/host.lua",
```

Add an option for the drop, in the `options` list (again, mind the comma
after the previous one):

```json
    {
      "key": "energy",
      "label": "Energy per drop",
      "type": "number",
      "default": 1000, "min": 0, "max": 5000, "step": 250
    }
```

Then make `FirstMod\lua\firstmod\host.lua`:

```lua
-- The host script: imported once in the host's simulation, while the match
-- loads, whenever the mod is picked.

local Events = Import("modapi/events.lua").Events
local Options = Import("modoptions/yourname.firstmod.lua").Options

-- Every two minutes of game time, every army still playing gets energy.
Events.Every(2 * 60, function()
    for _, army in pairs(Armies) do
        if not army.civilian and army:IsAlive() then
            army:GiveResources("energy", Options.energy)
        end
    end
    Warn("First mod: energy drop at " .. Events.GameTime() .. " seconds")
end)
```

A few things to know:

- **Events** is the framework's timing helper. Besides `Every` there's
  `OnMatchStart`, `After` (once, after a delay), `OnTick` (10 times a second)
  and `OnArmyDefeated`. Times are game time, so they pause and speed up with
  the game.
- **Armies** is the game's table of armies. Skip `army.civilian`: those are
  the map's neutral buildings.
- **A drop can't overfill your storage.** If your energy is already full,
  the drop does nothing you can see. Spend some before the two minutes are
  up.
- **`Warn`** writes a line to the game's log, which is how you see what your
  script is doing (see below). The game's `Log` works the same way, but its
  lines are left out of the log in normal play, so use `Warn` while you test.
- The folder name `firstmod` keeps your files apart from the game's and from
  other mods'. Name your own files' folder after your mod.

Play it as in step 4, spend some energy, and wait two minutes.

## 7. Change things while you work

You don't need to restart anything while you work on a mod:

- **In a lobby,** save a file and the mod is updated within a couple of
  seconds. The new version is what the next match runs. Other players have to
  get the same change before Start lets you go.
- **In a match,** nothing changes: the match keeps the files it started
  with. Finish it (or quit) and start a new one.

## When something goes wrong

There are two logs:

- **`engine\BepInEx\LogOutput.log`**: what the framework did. Look for
  `Sanctuary Mod API` lines. "Lua overlay: … from My First Mod 1.0.0" means
  your mod went on. "doesn't compile" names your file, the line and the
  mistake.
- **`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Player.log`**:
  the game's own log. Your `Warn` lines are here, and so are the game's Lua
  errors, each with the file and line. `HostLua` is the simulation, `ClientLua`
  a player's screen. For an append, the line number is past the end of the
  game's own file: the extra lines are yours.

| What you see | Usually |
| --- | --- |
| The mod isn't on the Gameplay Mods tab | `mod.json` isn't directly in the mod's folder, or is really `mod.json.txt`. |
| It's listed as **FirstMod** (the folder's name), not **My First Mod** | The game couldn't read `mod.json`: usually a missing comma or quote. **Problems** says where. |
| It's listed under **Problems** | Hover over it: the Mods page says what's wrong, such as a `hostScript` path that doesn't match a file. |
| "doesn't compile" in LogOutput.log | A Lua syntax mistake, at the line it names. Fix it and save; no restart needed. |
| Start stays greyed out | Someone in the lobby doesn't have an identical copy. The Mods panel says who, and what: "missing", "has a different copy", or "no mod support" (they don't have the Mod Manager). |
| The match starts but nothing changed | Check that the mod was switched on in the lobby, and look for the "Lua overlay" line. A host script that fails to load says so in Player.log. |
| The energy drop does nothing | Storage was already full. Spend some first. |

## 8. Share it

1. **Set the version.** Raise `version` in `mod.json` every time you change a
   file you've already shared (1.0.0, then 1.0.1 or 1.1.0). A player with an
   old copy is then told which version they have and which the host has.
2. **Say where to get it.** Add `"url": "https://…"`, a page where your mod
   can be downloaded. Players who are missing your mod see it in the lobby.
3. **Zip the folder.** Zip the `FirstMod` folder itself, so the zip contains
   `FirstMod\mod.json`.
4. **Tell players how to install it:** extract the zip into
   `engine\SanctuaryMods`, so the mod sits at
   `engine\SanctuaryMods\FirstMod\mod.json`. They also need the Mod Manager
   (its Standalone zip). If a zip tool puts an extra folder around the mod,
   it still works, and the Mods page says it's a folder too deep.

**Everyone plays from the same zip.** The lobby compares mods byte for byte.
If you keep your mod in Git, a checkout on Windows can change the line
endings in your Lua files, and two players with "the same" mod then have
different copies. Share the zip, and if you use Git, add a `.gitattributes`
file with these lines so Git leaves your mod's files alone:

```
**/lua/** -text
*.santp -text
```

**Only share what you'd run yourself.** A Lua mod can change anything in the
match. A mod with a DLL runs as a program on every player's PC.

## Where next

- **[writing-mods.md](writing-mods.md)**, the full reference: every
  `mod.json` field, kills and damage events, replacing unit stats (`.santp`
  files), adding units and whole factions, C# mods, and how the lobby
  decides.
- **[modding-field-notes.md](modding-field-notes.md)**, what we learnt about
  the game: how a match runs, Lua scoping, UI, unit and economy data, maps,
  debugging, and what breaks when the game patches.
- **The examples** in [`examples/`](../examples), each a complete mod you
  can copy into `SanctuaryMods` and play:
  - **ExampleGameplayMod** is the colour part of this tutorial.
  - **SupplyDrop** is the timer part, with alloys too.
  - **EngineersAndRaiders** limits what factories can build, both in the
    build menus and on the host, and explains why it needs both.
  - **AscendantFaction** adds a fourth faction, with a DLL for the lobby's
    faction menu.
  - **ExampleUiMod** is a C# UI mod with settings on the Mods page.
- **C# mods:** with the .NET SDK installed,
  `dotnet new install <this repository>\templates\sanctuary-mod`, then
  `dotnet new sanctuary-mod -n MyMod --kind gameplay --modAuthor "Your Name"`
  makes a mod project that builds straight into `SanctuaryMods`.
