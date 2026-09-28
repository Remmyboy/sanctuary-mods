SanctuaryMods - every mod lives here, one folder each.

Install a mod by extracting its zip here, so its folder sits beside the
others; remove it by deleting the folder. No restart either way: mods are
picked up within a couple of seconds. The Mods page's "Open Mods Folder"
button opens this folder. If a zip ended up in a folder of its own, or was
never extracted, the Mods page says so at the top.

  SanctuaryMods\
    MyUiMod\
      mod.json
      MyUiMod.dll            <- UI mod: yours alone
    MyGameMod\
      mod.json               <- "kind": "gameplay"
      lua\append\common\colors.lua   <- added to the game's colors.lua
      lua\units\...\x.santp          <- replaces the game's file

UI MODS change only your own screen. Switch them on and off, and change
their settings, on the Mods page (the cube icon in the menu's sidebar, or
F8, also mid-match). Other players never see them and don't need them.
"Play vanilla" at the top of the page switches them all off at once.

GAMEPLAY MODS change the match itself, so everyone in the match must run
exactly the same ones:
  - Outside a lobby the game always runs vanilla, so you can join anyone.
  - In a lobby the HOST picks gameplay mods, from the Mods button next to
    Settings. Everyone's game applies the pick before the match starts.
  - Start stays greyed out until every player has identical copies. The
    Mods panel and the chat say who is missing what; players without any
    mods can join, but only play vanilla matches.
  - Every lobby starts with none picked: a normal vanilla match. The host
    switches mods on for that lobby only.
  - Only use UI mods? Nothing changes for you: just never switch a
    gameplay mod on.
Nothing on disk is ever changed: the mods are applied in memory, for that
match only.

Replays of modded matches remember their mods and put them back on to
play, as long as you still have the same copies.

MAKING MODS
See docs/writing-mods.md in the source repository: a mod.json, a folder of
Lua laid out like the game's LJ\lua, and optionally C# built against
BepInEx\plugins\Sanctuary.ModApi.dll. There is a project template, so a
new mod builds and loads in a minute.

TRUST
A mod's DLL runs as full-trust code inside the game with your Windows
account's permissions, like any BepInEx plugin. Only install DLLs from a
source you trust.
