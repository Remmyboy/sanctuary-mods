# 2v2 and 3v3 auto-launch: plan

Status: plan, nothing built. Written 2026-10-13, after LadderReporter 0.6.0
(in-game queue) and site PR #75. Today a team match is always `manual`: the
site picks a host and the players set up the lobby by hand. The goal is for a
2v2 or 3v3 to launch itself the way a 1v1 does: countdown, host creates the
lobby, everyone else joins, everyone gets seated, game starts, result reports
itself.

Nothing about the game stops this. The work is in the places that assume
"two players" today, listed below with where each lives.

## What assumes 1v1 today

| Where | What it assumes |
| --- | --- |
| `pair_queue` (site, live in `0013_manual_slots.sql`) | `v_auto := p_mode = '1v1' and ...`; slots only for 1v1 |
| `match_participants.slot` (`0009`) | `check (slot in (1, 2))` |
| `sweep_mm_matches` (`0012`) | one joiner: `select ... player_id <> host limit 1`; "X didn't join" for that one |
| `not_startable_reason` / `is_launchable` (`0014`) | per player already, but only ever called for two |
| `toModMatch` (`src/server/mm.ts`) | returns null unless exactly 2 players; shape is `host`, `joiner`, `opponent` |
| `/api/mm/queue`, match room | only hand a match object to 1v1s |
| `launchProgress` (`src/lib/mm.ts`) | host + one joiner |
| `/api/report` | exactly one winning participant; late-answer lookup joins two players |
| LadderReporter `MmMatch` | `Host`, `Joiner`, `OpponentName` |
| LadderReporter host flow | starts when *the* joiner is seated; `KickStrangers` keeps host + joiner; `maxPlayerCount = 2` in practice |
| LadderReporter `ApplyOwnSeat` | team = slot (right for 1v1, wrong for teams) |
| LadderReporter reporter | skips unless exactly 2 humans on opposing teams |

Already fine for N players: the split into teams by rating (`pair_queue`
already does it), `apply_match_result` / `applyTeamResult` (team Elo), stats
and replay uploads (one army per seated human), live streaming, the in-game
queue itself.

## Where each player starts

The site has never assigned team starts because team maps don't order their
armies by team, as far as it knew. `public/ladder-maps/spawns.json` has every
start's position, and on all 12 generated team maps in the pools the
starts alternate: armies 1, 3, 5 share one side, 2, 4, 6 the other (checked
by eye from the coordinates, not from the map files or the generator). So:

- Team 1 gets the odd starts, team 2 the even ones, shuffled within the team.
- A `ladder_maps.team_starts` column (e.g. `{"1":[1,3],"2":[2,4]}`), filled
  for these maps by that rule, and editable on the admin page for any
  hand-made map that doesn't follow it. A team map with no layout stays
  manual.
- Worth confirming against the map generator's source before relying on it.

## Site changes

1. **Migration 0017** (replaces functions, so not purely additive; it changes
   only how team matches form, and only for groups where every player can be
   launched):
   - slot check widened to `1..6`; `ladder_maps.team_starts`.
   - `pair_queue`: auto for 2v2/3v3 when every player is launchable **and**
     runs a mod that can team-launch (presence `mod_version >= 0.7.0`), and
     the map has a path and a team layout. Faction per player from their
     queue entry, as 1v1 does. Slots from the layout.
   - `sweep_mm_matches`: wait for *every* joiner's `joined` (one timeout, the
     message names whoever is missing); start timeout scaled a little for
     6 players.
   - Fallback stays the same: anyone not startable at zero, and the whole
     match goes manual with the reason.
2. **Match object v2**: add `players: [{ steamId, name, team, slot, faction }]`
   and `ladderMode`. Keep `host`, and keep `joiner`/`opponent` for 1v1 so
   0.5/0.6 mods keep working. Team auto matches only go to mods that asked
   for them (the version gate above), so an old mod never sees one.
3. `/api/mm/queue` and the match room hand team matches over too.
4. `/api/report`: accept N participants and a winning *team* (all its
   members in `winnerSteamIds`), checked against the match's teams.
5. Match room: countdown and launch progress for every player.

## Mod changes (LadderReporter 0.7.0)

1. `MmMatch.Players`; `Joiner`/`OpponentName` kept for 1v1.
2. Host: lobby sized to the map, keep every match player (kick anyone
   else), start when all are present, seated and ready.
3. Joiners: unchanged (wait for the session id, join).
4. Seating: `team` = the match's team, `armyID` = slot (1v1 keeps team = slot).
5. Reporter: a launched team game reports when one team has won, with
   every human and the winning team; the shape check becomes "the players
   the ladder matched, on the teams it gave them".
6. Overlay and queue strip: "2v2 on X, launching", names of teammates.

## Testing

The hard part. A 1v1 could be tested with two machines; a 2v2 needs four
Steam accounts with the game.

- Unit: SQL against PGlite (`supabase/migrations.test.ts` already pairs
  1v1s; add a 2v2 that goes auto, one that falls back, and the join
  timeout).
- Mod: `Matchmaking.MockFile` with a 4-player match object, run as host:
  checks the lobby, kick and seating logic up to "waiting for 3 players".
- Real: one scheduled 2v2 with three other players on 0.7, then a 3v3.
  Until then team auto-launch could sit behind a site flag (admin page),
  so it can be switched on for the test and off again.

## Decisions for you

- **Mixed groups**: if one of four players lacks 0.7, the whole match goes
  manual (simplest, recommended). The alternative, launching the rest and
  leaving one to join by hand, is fragile.
- **Host**: random, as now, or the highest-rated / longest-queued player.
- **Team starts**: trust the odd/even rule for generated maps, or require
  each team map to be set up on the admin page first.
- **Rollout**: behind an admin flag until a real 2v2 has worked
  (recommended), or on as soon as 0.7 ships.
- **Mod-only queueing** (the eventual plan) would let the site drop the
  version gate and the manual fallback for missing mods.

## Rough size

Site: migration plus tests, match object, report endpoint, match room;
about half a day. Mod: about half a day. Plus a test session with real
players.
