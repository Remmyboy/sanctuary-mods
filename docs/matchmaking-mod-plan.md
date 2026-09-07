# Ladder matchmaking: mod-side plan

The in-game half of "queue on the site, get launched into the game". The
site half is `matchmaking-site-plan.md`; this document assumes those
endpoints and the match object described there.

Lives in the existing `LadderReporter` project (renamed in the README to
"Ladder", still one DLL): it already has the Steam ticket minting, the
`UnityWebRequest` delivery, the lobby snapshot, and the result report.

## What the game gives us (verified in the decompile)

- A lobby is a Steam game server. `LobbyManager.CreateLobby(props)` makes
  one (name, map path, player limit, type); `LobbyManager.currentSessionID`
  is the host's server Steam ID once it's up.
- Joining by session ID from anywhere is built in:
  `InterfaceManager.Instance.JoinSessionFromInvite(ulong)` is public, leaves
  any current lobby, moves the UI to the lobby list with a join in flight,
  and calls `LobbyManager.JoinLobby`. It's the path Steam "join game" uses.
- Every lobby setting is a public call the host validates like a click:
  `SetMemberFaction`, `SetMemberArmyID`, `SetMemberTeam`,
  `SetMemberIsReady` (all on the local `LobbyPlayer`), `RequestStartGame`
  (host), `SendKick(PlayerID)` (host), `CanStartGame()`.
- `LobbyManager.CurrentState.players` is the live roster with Steam IDs
  (`LobbyPlayer.id.value`), so the host can verify the joiner.
- The UI only listens for `OnLobbyCreated` while on its own lobby-settings
  screen; the mod registers its own handler and, on success, moves the UI
  the way the game's private `CreateLobby(LobbyState)` does
  (`TransitionTo(Window.Lobby)`, then `LobbyInterface.Instance.ClearData()`
  and `UpdateData(state)`), via reflection if those aren't public.

## Behaviour

### Session and the local bridge

> Since 0.3.0 the 5 s heartbeat is gone. The mod listens on
> `127.0.0.1:27555` (`LocalBridge.cs`) and the SanctuaryDB page in the
> player's browser reads `GET /status` and pushes the match object with
> `POST /match`; the page relays the state to the site inside its own
> polls. Full design: `docs/local-bridge.md` in the site repo. The rest of
> this section describes the original heartbeat for the record.

- When the first real match is pushed, and whenever a call returns `401`:
  mint a ticket (existing code), `POST /api/mm/session`, keep the token.
  It is only used for the session id, events and the result report.
- The state the page reads is derived from what the mod can see — `menu`
  (no lobby, no match), `lobby` (`LobbyManager.IsInLobby`), `loading` /
  `ingame` (client engine initialised; `InMatch` once the economy stream
  is flowing).
- The game already runs with `Application.runInBackground = true` (checked
  on 2026-09-03 from a running playtest build), so a minimised or unfocused
  game keeps polling with no help from the mod. The mod asserts it anyway
  at load, as a one-line guard against the setting changing in a patch.

### Launch state machine

Driven entirely by the `match` object the page pushes (`POST /match`). The site
owns the countdown and the cancel, and decides the mode when it pairs: the
mod acts only on `mode: auto` with `status: launch`. A `manual` match is
today's flow; the mod's only job there is the result report, plus an
optional overlay line ("Ladder match vs Skoub: you're hosting, they'll
join") so a player who tabbed into the game isn't left guessing. Nobody is
required to have the mod: the auto path appears when both sides do.

```
Idle ──match.mode==auto && status==launch──▶ Launching
Launching (host):
  1. restore/focus the game window (below)
  2. CreateLobby { name: "Ladder: A vs B", mapPath, maxPlayerCount:
     GetMapMemberLimit(map), ownerName, type: Public }
  3. on created: move the UI to the lobby; POST session { sessionId };
     event lobby_created
  4. SetMemberFaction / SetMemberArmyID(slots[me]) / SetMemberIsReady(true)
  5. every frame while waiting: kick any Player whose Steam ID isn't the
     joiner's; when the joiner is present, on their slot, and ready, and
     CanStartGame(): RequestStartGame(); event started
Launching (joiner):
  1. restore/focus the game window
  2. wait for match.sessionId (comes with the page's next push)
  3. JoinSessionFromInvite(sessionId); event joined when IsInLobby
  4. SetMemberFaction / SetMemberArmyID(slots[me]) / SetMemberIsReady(true)
  5. the host starts; the loading screen follows
Any state ──match.status in {cancelled, failed}──▶ Abort
Abort: leave the lobby if in one, show the reason in the overlay with a
       "host a game manually" hint, back to Idle.
```

Local timeouts mirror the site's (lobby creation 20 s, joiner arrival 30 s,
start 60 s); on expiry the mod posts `failed` with a detail and aborts, so
both sides converge even if one push is late.

Guards before acting on `launch`: the map file exists under the game
install (else `failed: map missing`), the faction and slot are valid, and
the mod is in `menu`. A `launch` seen while in a lobby or game is reported
as `failed: not in the main menu` rather than acted on.

### Window restore

On `launch`, bring the game back if it's minimised or behind the browser:
`ShowWindow(hwnd, SW_RESTORE)` then `SetForegroundWindow`, with the
attach-thread-input workaround for Windows' focus-stealing rule, and
`FlashWindowEx` as the fallback signal. `hwnd` is the current process's
main window. All via `user32` P/Invoke; nothing else is needed.

### Overlay

A small IMGUI panel in the Replays style, only while something is
happening: "Ladder: launching vs Skoub on The Forge", the step it's on, and
any failure reason with the manual-host hint. No countdown (the site has
it); no cancel after `launch` (leaving the lobby is the game's own button
and the site times it out).

### Reporting

Add `matchId` to the existing report payload when the game was matchmade,
so the site can close the match and tie the result to it.

### Config

`Matchmaking.Enabled` (default on), `BaseUrl` (the site), `LocalPort`
(27555, the loopback port the page talks to), `DevOrigins` (extra web
origins allowed to talk to the mod, for a site dev server). All editable
from the F8 window like every other setting.

## Testing without the site

A `Matchmaking.MockFile` setting: when set, the mod reads the match object
from that JSON file every 5 s in place of what the page would push (a
`POST /match` is refused with 409 while it is set). Two people with the game open
can then test the whole launch flow (one file says `host`, the other
`joiner`; the joiner's file is edited to add the `sessionId` the host logs).
It's also how I'll test the host half alone against a friend's manual join.

## Order of work

1. Session + heartbeat + run-in-background (an afternoon; testable against
   the site as soon as those two endpoints exist).
2. Host launch path with the mock file (creating the lobby, moving the UI,
   posting the session ID, kicking strangers, auto-start).
3. Joiner path with the mock file.
4. Abort handling, timeouts, overlay.
5. Window restore.
6. `matchId` in reports; README.

Steps 2–5 need two accounts to test end to end; the mock file means that
can happen before the site's launch flow is done.
