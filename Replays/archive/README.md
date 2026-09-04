# Archived: the mod's own replay capture

These three files were the Replays mod before the game shipped native
replays (playtest update of 2026-09-04). They recorded the host-to-client
packet stream from a Harmony prefix on `NetworkManager.HandleMessage`, stored
it with the game's per-field Brotli undone and one long-window Brotli pass
over the whole file, and played it back by feeding packets into a socket-less
client. The game's `ReplayClientSockets` now does the same job (a fake
socket that reads `.sanreplay` frames paced by the sim speed), so the mod
only drives that.

Kept for reference; excluded from the build by `Replays.csproj`.
