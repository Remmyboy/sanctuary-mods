-- Sanctuary Mod API: notes kept beside a match's replay.
--
-- A replay holds only what the recording player's game was sent. A mod
-- that keeps things from each player (hidden roles, a secret objective)
-- therefore can't show them in its replays: the replay never had them.
-- Notes fill that gap. Called in a client, Note saves data with that
-- player's recording of the match, typically once the match is over and
-- nothing is secret any more; when the replay plays, Get returns it from
-- the very start, before the replay has reached the point it was saved.
--
--   local Replay = Import("modapi/replay.lua").Replay
--   Replay.Note("alice.mymod", { roles = roles })   -- live, client side
--   if Replay.Playing() then
--       local story = Replay.Get("alice.mymod")      -- nil until the
--   end                                              -- framework hands it over
--
-- Get can return nil for a moment after a replay starts, and always for a
-- replay recorded before the mod saved anything (or by a player who left
-- before it did), so call it again later rather than once. Data is stored as
-- JSON: tables of strings, numbers and booleans. Keep it small (a few KB):
-- it lives in the replay's sidecar file. Key notes by your mod's id.

Replay = {}

local notes = {}
local loaded

--- Whether this client is playing a replay rather than a live match.
function Replay.Playing()
    return Engine.IsReplayPlayback() == true
end

--- Saves data under key with this player's recording of the match. A later
--- Note with the same key replaces it. Does nothing in a replay.
function Replay.Note(key, data)
    assert(type(key) == "string" and key ~= "", "Replay.Note takes a key string")
    if Replay.Playing() then return end
    notes[key] = data
    _G.__ModApiReplayNotes = json.encode(notes)
    _G.__ModApiReplayNotesRev = tostring((tonumber(_G.__ModApiReplayNotesRev) or 0) + 1)
end

--- In a replay: the data saved under key when it was recorded, or nil.
function Replay.Get(key)
    if not loaded then
        local text = _G.__ModApiReplayLoaded
        if type(text) ~= "string" then return nil end
        local ok, all = pcall(json.decode, text)
        loaded = ok and type(all) == "table" and all or {}
    end
    return loaded[key]
end
