-- Appended to the game's common/systems/factions.lua: the fourth faction.
--
-- The lobby hands Lua a faction number (its dropdown index + 1), and the game
-- looks everything else up here: the starting unit, the unit-id prefix the AI
-- converts build plans with, and the tag every faction's build lists use.
-- Index 4 is the lobby's fourth dropdown entry, which this mod's DLL adds.

table.insert(FactionsData, {
    name = "Ascendant",
    tpLetter = "ua",
    tag = "ASCENDANT",
    initialUnit = "ual0000",
})
