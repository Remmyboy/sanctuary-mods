-- Appended to the game's common/colors.lua, in the same chunk, just before
-- that file's closing `return`. Everything colors.lua defines is in scope:
-- here, its Colors and ArmyColors tables.
--
-- An append survives game updates far better than replacing the whole file,
-- and several mods can append to the same file without clashing.
--
-- The options come from mod.json; the lobby host sets them, and the Mod API
-- writes the values into modoptions/<mod id>.lua on every player's machine.

local Options = Import("modoptions/sanctuarymods.example.pinkarmy.lua").Options

local palette = {
    pink = EngineClasses.float4(1, 0.2, 0.7, 1),
    lime = EngineClasses.float4(0.6, 1, 0.1, 1),
    gold = EngineClasses.float4(1, 0.78, 0.1, 1),
}

Colors.ExamplePink = palette[Options.color] or palette.pink
ArmyColors[Options.army] = Colors.ExamplePink
