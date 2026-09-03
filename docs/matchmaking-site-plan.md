# SanctuaryDB matchmaking: site-side plan

This is the server half of "queue on the site, get launched into the game
automatically". The other half is the in-game mod (see
`matchmaking-mod-plan.md`); this document is what the mod expects from the
site. Everything the mod sends is authenticated the way ladder reports
already are: a Steam web-API auth ticket that the server verifies with Steam.

## What exists today

- Players queue on the site; it pairs them, picks a host at random, and plays
  a "match found" ding.
- The in-game mod (`LadderReporter`) posts results to `POST /api/report` with
  a Steam ticket and the two Steam IDs.

## What the site needs to add

### 1. A mod session

Minting a Steam ticket for every poll is slow (a Steam callback per call), so
the mod exchanges one ticket for a short-lived token.

`POST /api/mm/session` `{ ticket }`
→ `{ token, steamId, name, expiresAt }`

Verify the ticket exactly as `/api/report` does. Token lifetime of a few
hours is fine; the mod re-mints on `401`.

All endpoints below take `Authorization: Bearer <token>`.

### 2. Heartbeat, which doubles as the poll

The mod calls this every 5 seconds while the game is running.

`POST /api/mm/heartbeat`
`{ state, gameVersion, modVersion }`

- `state` is one of `menu`, `lobby`, `loading`, `ingame`. Only `menu` is
  launchable: a player in a lobby or a game cannot be started into a match.

→ `{ queued, match }` where `match` is `null` or the player's current match
(see §4).

Rules driven by the heartbeat:

- A player is **online** while their last heartbeat is under 15 seconds old.
- **Queueing requires being online and in `menu`.** The queue button on the
  site is disabled otherwise, with a line saying why ("Open the game to
  queue", "Leave your lobby to queue"). A player whose heartbeat lapses, or
  whose state leaves `menu`, is dropped from the queue and told so.

This is what makes no-shows nearly impossible: anyone the site pairs is, by
construction, sitting in the game's main menu with the mod running.

### 3. Pairing and countdown (mostly exists)

On pairing, create a match record with everything the mod needs to launch
without a lobby screen:

- `host` / `joiner` Steam IDs (host is picked as today)
- `map`: the game's map path, e.g. `Maps/The_Forge/The_Forge.sanmap`, from
  the ladder map pool
- `factions`: one per Steam ID, from what each player queued as (a player
  who queued with several gets one picked at random)
- `slots`: army slot per Steam ID, `1` or `2`, assigned at random so hosting
  doesn't fix the spawn
- `status: countdown`, `countdownEndsAt`: now + 10 s

The site plays the ding and shows the countdown with a **Cancel** button. A
cancel sets `status: cancelled`, `cancelledBy`. At zero, if both players are
still online and in `menu`, set `status: launch`; otherwise
`status: failed` with `reason` naming who wasn't ready ("Skoub closed the
game", "Remmy is in a lobby").

### 4. The match object returned to the mod

```json
{
  "id": "m_01HX...",
  "status": "countdown | launch | cancelled | failed | done",
  "host": "7656119...",
  "joiner": "7656119...",
  "opponent": { "steamId": "7656119...", "name": "Skoub" },
  "map": "Maps/The_Forge/The_Forge.sanmap",
  "factions": { "7656119...": "EDA", "7656119...": "Chosen" },
  "slots": { "7656119...": 1, "7656119...": 2 },
  "sessionId": "9015...",          // null until the host posts it
  "countdownEndsAt": "2026-09-03T15:02:10Z",
  "cancelledBy": null,
  "reason": null
}
```

Factions are the game's names: `EDA`, `Chosen`, `Guard`.

### 5. Session handoff

When the host's mod has created the lobby it posts the session ID (the
host's Steam game-server ID, a `ulong`). The joiner's mod sees it on its
next heartbeat and joins.

`POST /api/mm/match/{id}/session` `{ sessionId }` — host only.

### 6. Progress events

Both mods report what happened so the site can show it and time out cleanly.

`POST /api/mm/match/{id}/event` `{ type, detail? }`

- `lobby_created` (host), `joined` (joiner), `ready`, `started`,
  `failed` (with `detail`), `left`.

Site-side timeouts after `launch`:

| Waiting for | Limit | On expiry |
| --- | --- | --- |
| host `sessionId` | 20 s | `failed`, "Remmy's game could not create a lobby" |
| joiner `joined` | 30 s after `sessionId` | `failed`, "Skoub didn't join the lobby" |
| both `started` | 60 s after `launch` | `failed`, "The game didn't start" |

On `failed` or `cancelled`, both players see the reason and a plain "Host a
game manually" hint, and both are free to queue again. Whether a failure or
cancel counts against anyone is a policy choice; nothing here assumes it.

### 7. Linking results

The mod will add `matchId` to its existing `/api/report` payload. Accept it
(ignore it if unknown) and use it to mark the match `done` and tie the
result to the matchmade game, so a manually hosted rematch doesn't get
confused with it.

## Not in scope now

- Private lobbies or passwords: the lobby is public for the few seconds
  before it fills; the host's mod kicks anyone who isn't the assigned
  opponent.
- Launching the game from the browser (`steam://run/4511930//<sessionId>`
  would do it, and the game already handles that connect string) — the
  "must be in the menu to queue" rule makes it unnecessary.
- An in-game countdown mirror. The site owns the countdown; the mod can add
  an overlay reading the same status later if people tab into the game while
  queued.
