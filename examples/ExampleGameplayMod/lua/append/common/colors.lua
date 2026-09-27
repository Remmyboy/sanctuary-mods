-- Appended to the game's common/colors.lua, in the same chunk, just before
-- that file's closing `return`. Everything colors.lua defines is in scope:
-- here, its Colors and ArmyColors tables.
--
-- An append survives game updates far better than replacing the whole file,
-- and several mods can append to the same file without clashing.

Colors.ExamplePink = EngineClasses.float4(1, 0.2, 0.7, 1)
ArmyColors[1] = Colors.ExamplePink
