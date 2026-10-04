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
--   Events.OnUnitKilled(fn)     fn(victim, info): info.army and info.unit
--                               dealt the killing damage (either may be nil)
--   Events.ModifyDamage(fn)     fn(victim, amount, info) returns a new amount
--   Events.Kills(army)          enemy units that army has killed
-- And helpers (Mod API 1.8):
--   Events.Players()            the player armies, AI included, by army id
--   Events.ArmyName(army)       the player's name
--   Events.Guard(what, fn, ...) runs fn; an error is reported once, to the
--                               log and the match's message log
--   Events.Tell(army, text)     a line in one player's message log
--   Events.SendToEachClient(name, fn)  each client fn(army, clientId)'s
--                               table, when it has changed
--   Events.OnRequest(name, fn)  fn(army, data) for a client's
--                               SendToHost(data, name), with who sent it
-- Armies are in the global Armies table (skip army.civilian).

local Events = Import("modapi/events.lua").Events

Events.OnMatchStart(function()
    Warn("SanctuaryModTemplate: the match has started.")
end)
