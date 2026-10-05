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

The heartbeat is a **capability signal, not a gate**. Nobody needs the mod
to queue; the queue works for everyone exactly as it does today.

- A player is **launchable** while their last heartbeat is under 15 seconds
  old and its state is `menu`.
- The site can show this next to a queued player ("auto-launch ready") so
  people understand which kind of match they'll get, but it never blocks
  queueing.

### 3. Pairing picks the mode

Pairing is unchanged. When a pair is formed, the site decides the mode once:

- **`auto`** if *both* players are launchable at that moment. The rest of
  this document applies: countdown, then the mods create, join and start the
  game with no lobby interaction.
- **`manual`** otherwise: today's flow, unchanged. The site tells the host to
  create the lobby and the joiner who to look for, and the mod (if one side
  has it) does nothing beyond reporting the result as it does now.

The mode is recorded on the match (`mode: auto | manual`) and shown to both
players. Because a manual match needs no mod on either side, nothing about
the current player base is forced to update, and the auto path simply
appears for pairs who both have the mod and the game open.

If a countdown for an `auto` match reaches zero and one player is no longer
launchable (closed the game, entered a lobby), don't fail the match: **fall
back to `manual`** for that match and say why ("Skoub's game closed, so
host manually"). Failure states below are reserved for the auto flow going
wrong after `launch`.

### 3a. The `auto` countdown (site-owned)

For an `auto` match, create the record with everything the mod needs to
launch without a lobby screen:

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
still launchable, set `status: launch`; otherwise switch the match to
`mode: manual` with a `reason` naming who dropped ("Skoub closed the game",
"Remmy is in a lobby") and show today's manual-hosting instructions.

### 4. The match object returned to the mod

```json
{
  "id": "m_01HX...",
  "mode": "auto | manual",
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

### 8. Stats and replay uploads (LadderReporter 0.4, opt-in)

Both off by default in the mod (`[Upload] Stats`, `Replays`). The site's
plan is its repo's `docs/replays-and-stats-plan.md`; what the mod relies on:

- `POST /api/report` answers 200 `{ "outcome": "reported"|"applied"|"disputed", "matchId": "<uuid>" }`.
  The mod uploads against that `matchId` (else the matchmade id it already
  had); without one, and whenever the report itself failed, it uploads
  nothing.
- `POST /api/mm/match/{id}/stats` (bearer, JSON, at most 256 KB) -> 200
  `{ ok: true }`. Sent once per match, three tries on 5xx or no answer, no
  retry on a 4xx except a 401 (sign in again, once). The payload is
  `format: 1`: `modVersion`, `buildId` (Steam app build), `tickRate`,
  `endTick`, one entry per seated player (keyed by `steamId`, with
  `armyId`, `name`, `faction`, `team`, `colour` `#rrggbb`, `condition`,
  `conditionTick`, `alloy`/`energy` `{gathered, spent, wasted, stallTicks,
  peakIncome}`, `maxStorage`, `built {land, air, naval, engineers,
  structures, value}`, `lost {mobile, structures, commander, value}`,
  `killedValue`, `commanderKills`, `peakArmyValue`, `peakUnits`, `score`)
  and a columnar `timeline { intervalS, t[], series { <steamId>:
  { alloyIncome, energyIncome, alloySpend, energySpend, armyValue, units,
  score } } }`. `intervalS` is 5, doubled (10, 20, ...) only when a very
  long game would otherwise pass 256 KB. Numbers are rounded to 1 decimal.
- `POST /api/mm/match/{id}/replay` (bearer) `{ sizeBytes, sha256, gameVersion,
  buildId, mapPath, fileName, sidecarBytes }` -> `{ upload: { url,
  sidecarUrl|null }, expiresAt }`, `{ skip: "stored" }` or
  `{ retryAfterS }`. The mod PUTs the raw file to `url` (no auth header,
  `application/octet-stream`) and the `.mods.json` sidecar to
  `sidecarUrl`, then `POST .../replay/done` `{}` -> `{ ok: true }`; a 409
  there restarts from a new slot. It keeps an unexpired slot (more than 5
  minutes left) across retries and goes straight to the PUT, or straight to
  `done` if the PUT already went through. 413, 404 and any other 4xx but
  401, 408 and 429 drop the replay; anything else retries after 1, 5 and
  30 minutes, then next launch, for up to 7 days.
- The local bridge's `GET /status` adds `uploads: { stats, replays, pending }`.

## Not in scope now

- Private lobbies or passwords: the lobby is public for the few seconds
  before it fills; the host's mod kicks anyone who isn't the assigned
  opponent.
- Launching the game from the browser for `manual` matches
  (`steam://run/4511930//<sessionId>` would do it, and the game already
  handles that connect string). A possible later step: it would let a player
  without the mod be joined into a mod host's lobby with one click.
- Requiring the mod for anything. The manual flow stays the baseline; the
  auto flow is an upgrade that appears when both sides have it.
- An in-game countdown mirror. The site owns the countdown; the mod can add
  an overlay reading the same status later if people tab into the game while
  queued.
