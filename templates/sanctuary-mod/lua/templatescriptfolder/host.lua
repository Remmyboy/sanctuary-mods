-- Your mod's host script: mod.json names it as "hostScript", so the host's
-- simulation imports it once, while loading, before the match starts. This
-- is the place for the rules of the match. Only the host runs it.
--
-- Register what should happen and when; the framework calls it:
--   Events.OnMatchStart(fn)     the armies and map are set up
--   Events.After(seconds, fn)   once, in game time
--   Events.Every(seconds, fn)   repeatedly
--   Events.OnTick(fn)           every tick (10 a second)
--   Events.OnArmyDefeated(fn)   fn(army) when an army is out
-- Armies are in the global Armies table (skip army.civilian).

local Events = Import("modapi/events.lua").Events

Events.OnMatchStart(function()
    Log("SanctuaryModTemplate: the match has started.")
end)
