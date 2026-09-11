SanctuaryMods - every mod lives here, one folder each.

A mod folder can hold either kind of mod, or both:

  SanctuaryMods\
    MyUiMod\
      MyUiMod.dll            <- UI mod: loaded and hot-reloaded on the spot
    MyGameMod\
      common\colors.lua      <- Lua mod: replaces LJ\lua\common\colors.lua
      gamemodes\mymode.lua   <- adds a new file visible to Lua

Manage both from the Mods page in the front menu (the cube icon in the
sidebar, or F8).

UI mods (DLLs) are client-side C# that never enters the lobby's Lua hash, so
they can be toggled at any time, even mid-match. Like any BepInEx plugin
they run as full-trust code inside the game, with your Windows account's
permissions, so only install DLLs from a source you trust.

Lua mods are overlaid in memory only - nothing on disk is touched - and take
effect at the next match launch, so toggle them from the main menu rather
than in a lobby or match. Only *.lua and *.santp files are applied.

Multiplayer: all players must have the same Lua mods enabled. The lobby
compares a hash of the Lua tree and refuses mismatched joiners, so a
mismatch cannot desync a game - it just cannot join. The Lua Mods tab shows
your current hash; compare it with your friends. Warning: .santp files
are NOT covered by that hash, so template mods must be coordinated
manually or they will desync mid-game.
