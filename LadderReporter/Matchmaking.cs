using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using EM.UI;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Ladder matchmaking: the in-game half of "queue on the site, get launched
    // into the game". The site pairs players, picks the map, factions, slots
    // and host, and runs the countdown; this side heartbeats so the site knows
    // the game is open, and when a match reaches `launch` it creates or joins
    // the lobby, sets its own seat, and (as host) starts the game — no lobby
    // interaction from either player. Nobody is required to have this: the
    // site only picks the auto path when both sides are heartbeating.
    //
    // Everything here is driven by the match object the heartbeat returns
    // (see docs/matchmaking-site-plan.md). A local timeout mirrors each of
    // the site's, so both sides converge even when a heartbeat is late.
    public partial class LadderReporterPlugin
    {
        private const string ModVersion = "0.2.0";

        private ConfigEntry<bool> _cfgMmEnabled;
        private ConfigEntry<string> _cfgMmBaseUrl;
        private ConfigEntry<float> _cfgMmHeartbeat;
        private ConfigEntry<string> _cfgMmMockFile;

        // Session with the site: one Steam ticket becomes a bearer token.
        private string _mmToken;
        private bool _mmSessionInFlight;
        private float _mmNextSessionTry;

        // Heartbeat.
        private float _mmHbAccum;
        private bool _mmHbInFlight;
        private bool _mmQueued;
        private string _mmLastHbError;
        private float _mmLastHbErrorAt = -999f;

        // The match being acted on.
        private enum Phase { Idle, HostCreating, HostWaiting, JoinerWaiting, JoinerJoining, JoinerInLobby, Started }
        private Phase _phase = Phase.Idle;
        private MmMatch _match;
        private float _phaseSince;
        private float _launchSince;
        private string _handledKey;            // "{id}:{status}" already acted on
        private readonly HashSet<string> _eventsSent = new HashSet<string>();
        private float _settingsAccum;
        private bool _lobbyIsOurs;             // we created/joined it for this match
        private string _mmReportMatchId;       // attached to the result report

        // Overlay.
        private string _overlayTitle;
        private string _overlayText;
        private float _overlayUntil;

        private bool _runInBackgroundWas;
        private int _cfgReloadAccum;

        // Mock testing is two people coordinating by hand, so every wait
        // stretches to ten minutes there; the live limits mirror the site's.
        private float Limit(float seconds) => MockMode ? Mathf.Max(seconds, 600f) : seconds;

        private sealed class MmMatch
        {
            public string Id, Mode, Status, Host, Joiner, Map, Reason, CancelledBy, OpponentName;
            public ulong SessionId;
            public readonly Dictionary<string, string> Factions = new Dictionary<string, string>();
            public readonly Dictionary<string, int> Slots = new Dictionary<string, int>();

            public static MmMatch Parse(JObject o)
            {
                if (o == null) return null;
                var m = new MmMatch
                {
                    Id = (string)o["id"],
                    Mode = (string)o["mode"] ?? "manual",
                    Status = (string)o["status"] ?? "",
                    Host = (string)o["host"],
                    Joiner = (string)o["joiner"],
                    Map = (string)o["map"],
                    Reason = (string)o["reason"],
                    CancelledBy = (string)o["cancelledBy"],
                    OpponentName = (string)o["opponent"]?["name"],
                };
                var sid = (string)o["sessionId"];
                if (!string.IsNullOrEmpty(sid)) ulong.TryParse(sid, out m.SessionId);
                if (o["factions"] is JObject f)
                {
                    foreach (var kv in f) m.Factions[kv.Key] = (string)kv.Value;
                }
                if (o["slots"] is JObject s)
                {
                    foreach (var kv in s) m.Slots[kv.Key] = (int)kv.Value;
                }
                return m;
            }
        }

        // ---- lifecycle -------------------------------------------------------

        private void AwakeMatchmaking()
        {
            _cfgMmEnabled = Config.Bind("Matchmaking", "Enabled", true,
                "Let the ladder launch you straight into a matchmade game. While the game is open in the main " +
                "menu the mod tells the site so; when both players in a match have it, the site counts down and " +
                "the mods create and join the lobby and start the game. Nothing changes for players without it.");
            _cfgMmBaseUrl = Config.Bind("Matchmaking", "BaseUrl", "https://www.sanctuarydb.net",
                "The ladder site. Endpoints are under /api/mm/.");
            _cfgMmHeartbeat = Config.Bind("Matchmaking", "HeartbeatSeconds", 5f,
                "How often to tell the site the game is open (and to check for a match).");
            _cfgMmMockFile = Config.Bind("Matchmaking", "MockFile", "",
                "For testing without the site: a JSON file holding the match object. Read instead of the " +
                "heartbeat; session and event posts are logged, not sent.");

            // The game already runs in the background (checked on the playtest
            // build), so a minimised window keeps polling; assert it anyway
            // so a patch changing that can't silently stall matchmaking.
            _runInBackgroundWas = Application.runInBackground;
            Application.runInBackground = true;
        }

        private void DestroyMatchmaking()
        {
            LobbyManager.OnLobbyCreated -= OnMmLobbyCreated;
            Application.runInBackground = _runInBackgroundWas;
        }

        private void UpdateMatchmaking()
        {
            if (_cfgMmEnabled == null || !_cfgMmEnabled.Value) return;
            var dt = Time.unscaledDeltaTime;

            _mmHbAccum += dt;
            if (_mmHbAccum >= Mathf.Max(2f, _cfgMmHeartbeat.Value))
            {
                _mmHbAccum = 0f;
                // Re-read the config file so a tester without the F8 window
                // can set MockFile (or flip Enabled) by editing it, no restart.
                _cfgReloadAccum += 1;
                if (_cfgReloadAccum >= 3)
                {
                    _cfgReloadAccum = 0;
                    try { Config.Reload(); } catch { }
                }
                Heartbeat();
            }

            if (_phase != Phase.Idle)
            {
                _settingsAccum += dt;
                if (_settingsAccum >= 0.5f)
                {
                    _settingsAccum = 0f;
                    try { TickMatch(); }
                    catch (Exception e)
                    {
                        Logger.LogError($"Matchmaking: {e}");
                        Abort("Something went wrong on this side: " + e.Message, "exception: " + e.Message);
                    }
                }
            }
        }

        private static string LocalSteamId =>
            SteamManager.IsSteamInitialized ? SteamUser.GetSteamID().m_SteamID.ToString() : null;

        private static bool UsingSteam => LobbyManager.Backend is SteamLobbyBackend && SteamManager.IsSteamInitialized;

        private bool MockMode => !string.IsNullOrWhiteSpace(_cfgMmMockFile.Value);

        // What the site needs to know about where we are. Only `menu` is
        // launchable.
        private string CurrentState()
        {
            if (InMatch) return "ingame";
            if (LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby && LobbyManager.IsInLobby) return "loading";
            if (LobbyManager.IsInLobby) return "lobby";
            return "menu";
        }

        // ---- session + heartbeat ---------------------------------------------

        private void Heartbeat()
        {
            if (MockMode)
            {
                try { ApplyMatch(ReadMock()); }
                catch (Exception e) { Logger.LogWarning($"Matchmaking (mock): {e.Message}"); }
                return;
            }
            if (!UsingSteam)
            {
                if (!_loggedNoSteam)
                {
                    _loggedNoSteam = true;
                    Logger.LogWarning("Matchmaking: waiting for Steam (backend " +
                                      $"{LobbyManager.Backend?.GetType().Name ?? "none"}, initialised {SteamManager.IsSteamInitialized}).");
                }
                return;
            }
            if (_mmHbInFlight) return;
            if (_mmToken == null)
            {
                EnsureSession();
                return;
            }
            StartCoroutine(HeartbeatRoutine());
        }

        private bool _loggedNoSteam;
        private float _mmSessionStarted;
        private bool _mmUpdateFailedLogged;

        private void EnsureSession()
        {
            var now = Time.realtimeSinceStartup;
            if (_mmSessionInFlight)
            {
                // Steam never answered the ticket request (offline mode, or
                // no connection to Steam's servers). Say so and try again.
                if (now - _mmSessionStarted > 30f)
                {
                    Logger.LogWarning("Matchmaking: Steam did not answer the ticket request in 30 s; is Steam online? Retrying.");
                    _mmSessionInFlight = false;
                    _mmNextSessionTry = now + 30f;
                }
                return;
            }
            if (now < _mmNextSessionTry) return;
            _mmSessionInFlight = true;
            _mmSessionStarted = now;
            Logger.LogInfo("Matchmaking: requesting a Steam ticket to sign in to the ladder.");
            RequestTicket(ticket =>
            {
                if (ticket == null)
                {
                    Logger.LogWarning("Matchmaking: Steam refused a ticket; retrying in a minute.");
                    _mmSessionInFlight = false;
                    _mmNextSessionTry = Time.realtimeSinceStartup + 60f;
                    return;
                }
                StartCoroutine(SessionRoutine(ticket));
            });
        }

        private IEnumerator SessionRoutine(string ticket)
        {
            var body = new JObject { ["ticket"] = ticket, ["identity"] = TicketIdentity };
            var req = Post("/api/mm/session", body, null);
            yield return req.SendWebRequest();
            _mmSessionInFlight = false;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var o = JObject.Parse(req.downloadHandler.text);
                    _mmToken = (string)o["token"];
                    Logger.LogInfo("Matchmaking: signed in to the ladder.");
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Matchmaking: session reply unreadable: {e.Message}");
                }
            }
            else
            {
                LogHttp("session", req);
                _mmNextSessionTry = Time.realtimeSinceStartup + 60f;
            }
            req.Dispose();
        }

        private IEnumerator HeartbeatRoutine()
        {
            _mmHbInFlight = true;
            var body = new JObject
            {
                ["state"] = CurrentState(),
                ["gameVersion"] = Application.version,
                ["modVersion"] = ModVersion,
            };
            var req = Post("/api/mm/heartbeat", body, _mmToken);
            yield return req.SendWebRequest();
            _mmHbInFlight = false;

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var o = JObject.Parse(req.downloadHandler.text);
                    _mmQueued = (bool?)o["queued"] ?? false;
                    ApplyMatch(MmMatch.Parse(o["match"] as JObject));
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Matchmaking: heartbeat reply unreadable: {e.Message}");
                }
            }
            else if ((int)req.responseCode == 401)
            {
                _mmToken = null;   // expired; the next heartbeat signs in again
            }
            else
            {
                LogHttp("heartbeat", req);
            }
            req.Dispose();
        }

        private UnityWebRequest Post(string path, JObject body, string token)
        {
            var req = new UnityWebRequest(_cfgMmBaseUrl.Value.TrimEnd('/') + path, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString())) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
            };
            if (token != null) req.SetRequestHeader("Authorization", "Bearer " + token);
            return req;
        }

        // Don't spam the log at heartbeat rate while the site is down.
        private void LogHttp(string what, UnityWebRequest req)
        {
            var msg = (int)req.responseCode > 0
                ? $"{req.responseCode}: {Truncate(req.downloadHandler?.text, 160)}"
                : req.error;
            if (msg == _mmLastHbError && Time.realtimeSinceStartup - _mmLastHbErrorAt < 300f) return;
            _mmLastHbError = msg;
            _mmLastHbErrorAt = Time.realtimeSinceStartup;
            Logger.LogWarning($"Matchmaking: {what} failed, {msg}");
        }

        private IEnumerator PostAndForget(string path, JObject body, string what)
        {
            if (MockMode)
            {
                Logger.LogInfo($"Matchmaking (mock): would POST {path} {body.ToString(Newtonsoft.Json.Formatting.None)}");
                yield break;
            }
            if (_mmToken == null) yield break;
            var req = Post(path, body, _mmToken);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) LogHttp(what, req);
            req.Dispose();
        }

        private void PostEvent(string type, string detail = null)
        {
            if (_match == null) return;
            var key = type + ":" + (detail ?? "");
            if (!_eventsSent.Add(key)) return;
            var body = new JObject { ["type"] = type };
            if (detail != null) body["detail"] = detail;
            StartCoroutine(PostAndForget($"/api/mm/match/{_match.Id}/event", body, "event " + type));
        }

        private MmMatch ReadMock()
        {
            var path = _cfgMmMockFile.Value;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0 || text == "null") return null;
            // "me" stands in for this machine's Steam ID, so one file can be
            // written without looking the ID up.
            var me = LocalSteamId ?? "0";
            return MmMatch.Parse(JObject.Parse(text.Replace("\"me\"", "\"" + me + "\"")));
        }

        // ---- reacting to the match object -----------------------------------

        private void ApplyMatch(MmMatch m)
        {
            if (m == null)
            {
                // The site no longer has a match for us. If we were mid-launch
                // that's a cancel we never saw.
                if (_phase != Phase.Idle && _phase != Phase.Started) Abort("The match was cancelled.", null);
                _match = null;
                return;
            }

            var key = m.Id + ":" + m.Status + ":" + m.Mode;
            var isNew = key != _handledKey;
            _handledKey = key;
            var me = LocalSteamId;
            var isHost = m.Host == me;
            var opponent = string.IsNullOrEmpty(m.OpponentName) ? "your opponent" : m.OpponentName;

            switch (m.Status)
            {
                case "countdown":
                    _match = m;
                    if (isNew)
                    {
                        Overlay("MATCH FOUND", m.Mode == "auto"
                            ? $"vs {opponent} on {MapName(m.Map)}. Launching when the site's countdown ends."
                            : $"vs {opponent}. {(isHost ? "You're hosting" : "They're hosting")}, see the site.", 20f);
                    }
                    break;

                case "launch":
                    if (m.Mode != "auto")
                    {
                        _match = m;
                        if (isNew)
                        {
                            Overlay("LADDER MATCH", $"vs {opponent} on {MapName(m.Map)}. " +
                                (isHost ? "You're hosting: create the lobby as usual." : "They're hosting: join their lobby."), 30f);
                        }
                        break;
                    }
                    if (_phase == Phase.Idle && isNew)
                    {
                        _match = m;
                        BeginLaunch(m, isHost);
                    }
                    else if (_match != null && _match.Id == m.Id)
                    {
                        _match.SessionId = m.SessionId != 0 ? m.SessionId : _match.SessionId;
                    }
                    break;

                case "cancelled":
                case "failed":
                    if (_match != null && _match.Id == m.Id && _phase != Phase.Idle && _phase != Phase.Started)
                    {
                        var why = m.Status == "cancelled"
                            ? (string.IsNullOrEmpty(m.CancelledBy) || m.CancelledBy == me ? "Match cancelled." : $"{m.CancelledBy} cancelled the match.")
                            : (string.IsNullOrEmpty(m.Reason) ? "The launch failed." : m.Reason);
                        Abort(why, null);
                    }
                    else if (isNew && _match != null && _match.Id == m.Id)
                    {
                        Overlay(m.Status == "cancelled" ? "MATCH CANCELLED" : "LAUNCH FAILED",
                            string.IsNullOrEmpty(m.Reason) ? "" : m.Reason, 20f);
                    }
                    _match = null;
                    break;

                case "done":
                    _match = null;
                    break;
            }
        }

        private void BeginLaunch(MmMatch m, bool isHost)
        {
            _eventsSent.Clear();
            _launchSince = Time.realtimeSinceStartup;
            _lobbyIsOurs = false;

            if (CurrentState() != "menu")
            {
                Abort("The match launched while this game wasn't in the main menu.", "not in the main menu");
                return;
            }
            if (!MapExists(m.Map))
            {
                Abort($"This install doesn't have the map {MapName(m.Map)}.", "map missing");
                return;
            }
            var me = LocalSteamId;
            if (me == null || !m.Factions.ContainsKey(me) || !m.Slots.ContainsKey(me) || !TryFaction(m.Factions[me], out _))
            {
                Abort("The match is missing your faction or slot.", "bad assignment");
                return;
            }

            RestoreWindow();
            var opponent = string.IsNullOrEmpty(m.OpponentName) ? "your opponent" : m.OpponentName;
            Overlay("LAUNCHING", $"vs {opponent} on {MapName(m.Map)}: {(isHost ? "creating the lobby" : "waiting for the host's lobby")}...", 120f);

            if (isHost)
            {
                SetPhase(Phase.HostCreating);
                LobbyManager.OnLobbyCreated -= OnMmLobbyCreated;
                LobbyManager.OnLobbyCreated += OnMmLobbyCreated;
                var limit = LobbyManager.GetMapMemberLimit(m.Map);
                LobbyManager.CreateLobby(new LobbyManager.LobbyProperties
                {
                    name = $"Ladder: {LobbyManager.CurrentUserName} vs {opponent}",
                    mapPath = m.Map,
                    maxPlayerCount = Math.Max(2, (int)limit),
                    ownerName = LobbyManager.CurrentUserName,
                    type = LobbyManager.LobbyType.Public,
                });
            }
            else
            {
                SetPhase(Phase.JoinerWaiting);
                if (m.SessionId != 0) TryJoin(m.SessionId);
            }
        }

        private void OnMmLobbyCreated(bool ok, LobbyState state, string reason)
        {
            LobbyManager.OnLobbyCreated -= OnMmLobbyCreated;
            if (_phase != Phase.HostCreating) return;
            if (!ok)
            {
                Abort("The lobby could not be created: " + reason, "lobby: " + reason);
                return;
            }
            _lobbyIsOurs = true;
            MoveUiToLobby(state);
            var sessionId = LobbyManager.currentSessionID;
            Logger.LogInfo($"Matchmaking: lobby created, session {sessionId}.");
            StartCoroutine(PostAndForget($"/api/mm/match/{_match.Id}/session",
                new JObject { ["sessionId"] = sessionId.ToString() }, "session id"));
            PostEvent("lobby_created");
            if (MockMode)
            {
                // Testing without the site: the joiner needs this number.
                try { GUIUtility.systemCopyBuffer = sessionId.ToString(); } catch { }
                Overlay("LAUNCHING (MOCK)", $"Lobby up. Session ID {sessionId} (copied to the clipboard). " +
                    $"Waiting for {_match.OpponentName ?? "your opponent"} to join...", 300f);
            }
            else
            {
                Overlay("LAUNCHING", $"Lobby up. Waiting for {_match.OpponentName ?? "your opponent"} to join...", 120f);
            }
            SetPhase(Phase.HostWaiting);
        }

        // The game only moves its UI on lobby creation while on its own
        // lobby-settings screen; do what its private CreateLobby(LobbyState)
        // does, with a public fallback.
        private void MoveUiToLobby(LobbyState state)
        {
            var ui = InterfaceManager.Instance;
            if (ui == null) return;
            try
            {
                var mi = typeof(InterfaceManager).GetMethod("CreateLobby", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(LobbyState) }, null);
                if (mi != null)
                {
                    mi.Invoke(ui, new object[] { state });
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: UI move via CreateLobby failed ({e.Message}); using the public route.");
            }
            ui.TransitionTo(InterfaceManager.Window.Lobby);
            LobbyInterface.Instance?.ClearData();
            LobbyInterface.Instance?.UpdateData(state);
        }

        private void TryJoin(ulong sessionId)
        {
            if (_phase != Phase.JoinerWaiting) return;
            var ui = InterfaceManager.Instance;
            if (ui == null)
            {
                Abort("The game UI isn't ready to join.", "no ui");
                return;
            }
            Logger.LogInfo($"Matchmaking: joining session {sessionId}.");
            Overlay("LAUNCHING", "Joining the host's lobby...", 120f);
            SetPhase(Phase.JoinerJoining);
            ui.JoinSessionFromInvite(sessionId);
        }

        private void TickMatch()
        {
            var m = _match;
            if (m == null)
            {
                if (_phase != Phase.Started) SetPhase(Phase.Idle);
                return;
            }
            var now = Time.realtimeSinceStartup;
            var inPhase = now - _phaseSince;
            var me = LocalSteamId;

            // The game left the lobby for loading: launched.
            if (_phase != Phase.Started && _phase != Phase.Idle && LobbyManager.IsInLobby &&
                LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby)
            {
                PostEvent("started");
                _mmReportMatchId = m.Id;
                Overlay("LAUNCHED", $"vs {m.OpponentName ?? "your opponent"} on {MapName(m.Map)}. Good luck.", 8f);
                SetPhase(Phase.Started);
                return;
            }

            switch (_phase)
            {
                case Phase.HostCreating:
                    if (inPhase > Limit(20f)) Abort("The lobby took too long to create.", "lobby timeout");
                    break;

                case Phase.HostWaiting:
                    if (!LobbyManager.IsInLobby)
                    {
                        Abort("The lobby closed before the game started.", "lobby closed");
                        break;
                    }
                    ApplyOwnSeat(m, me);
                    KickStrangers(m, me);
                    var joiner = FindPlayer(m.Joiner);
                    if (joiner != null && joiner.isReady && m.Slots.TryGetValue(m.Joiner, out var jslot) && joiner.armyID == jslot &&
                        LocalIsSeated(m, me) && LobbyManager.CanStartGame())
                    {
                        Logger.LogInfo("Matchmaking: both seated and ready, starting.");
                        Overlay("LAUNCHING", "Starting the game...", 60f);
                        LobbyManager.RequestStartGame();
                        SetPhase(Phase.JoinerInLobby);   // reuse the "waiting for start" timeout
                    }
                    else if (inPhase > Limit(60f))
                    {
                        Abort($"{m.OpponentName ?? "Your opponent"} didn't arrive in time.", "opponent did not join");
                    }
                    break;

                case Phase.JoinerWaiting:
                    if (m.SessionId != 0) TryJoin(m.SessionId);
                    else if (now - _launchSince > Limit(30f)) Abort("The host's lobby never appeared.", "no session id");
                    break;

                case Phase.JoinerJoining:
                    if (LobbyManager.IsInLobby && LobbyManager.CurrentState != null)
                    {
                        _lobbyIsOurs = true;
                        PostEvent("joined");
                        Overlay("LAUNCHING", "In the lobby. Readying up...", 60f);
                        SetPhase(Phase.JoinerInLobby);
                    }
                    else if (inPhase > Limit(30f)) Abort("Couldn't join the host's lobby.", "join timeout");
                    break;

                case Phase.JoinerInLobby:
                    if (!LobbyManager.IsInLobby)
                    {
                        Abort("The lobby closed before the game started.", "lobby closed");
                        break;
                    }
                    ApplyOwnSeat(m, me);
                    if (LocalIsSeated(m, me)) PostEvent("ready");
                    if (now - _launchSince > Limit(90f)) Abort("The game didn't start in time.", "start timeout");
                    break;

                case Phase.Started:
                    // Back in the menu after the game: done with this match.
                    if (!LobbyManager.IsInLobby && !InMatch)
                    {
                        _mmReportMatchId = null;
                        _match = null;
                        SetPhase(Phase.Idle);
                    }
                    break;
            }
        }

        // ---- lobby helpers ---------------------------------------------------

        private static LobbyPlayer FindPlayer(string steamId)
        {
            var state = LobbyManager.CurrentState;
            if (state?.players == null || steamId == null) return null;
            return state.players.FirstOrDefault(p => p != null && p.type == PlayerType.Player && p.id.value.ToString() == steamId);
        }

        private static bool TryFaction(string name, out Faction faction) =>
            Enum.TryParse(name, true, out faction);

        // Faction, slot, then ready — the host validates each like a click, so
        // keep re-sending until the roster shows it took.
        private void ApplyOwnSeat(MmMatch m, string me)
        {
            var local = FindPlayer(me);
            if (local == null) return;
            if (!TryFaction(m.Factions[me], out var faction)) return;
            var slot = m.Slots[me];
            if (local.faction != faction) LobbyManager.SetMemberFaction(local, faction);
            else if (local.armyID != slot) LobbyManager.SetMemberArmyID(local, slot);
            else if (!local.isReady) LobbyManager.SetMemberIsReady(local, true);
        }

        private static bool LocalIsSeated(MmMatch m, string me)
        {
            var local = FindPlayer(me);
            return local != null && local.isReady && m.Slots.TryGetValue(me, out var slot) && local.armyID == slot &&
                   TryFaction(m.Factions[me], out var f) && local.faction == f;
        }

        // The lobby is public for the seconds before it fills; anyone who
        // isn't the assigned opponent is shown the door.
        private void KickStrangers(MmMatch m, string me)
        {
            var state = LobbyManager.CurrentState;
            if (state?.players == null) return;
            foreach (var p in state.players)
            {
                if (p == null || p.type != PlayerType.Player) continue;
                var id = p.id.value.ToString();
                if (id == me || id == m.Joiner) continue;
                Logger.LogInfo($"Matchmaking: kicking {p.name}, not part of this match.");
                LobbyManager.SendKick(p.id);
            }
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _phaseSince = Time.realtimeSinceStartup;
        }

        // Ends the launch on this side: leave a lobby we made or joined for
        // it, tell the site, tell the player, back to the menu.
        private void Abort(string message, string failureDetail)
        {
            LobbyManager.OnLobbyCreated -= OnMmLobbyCreated;
            if (failureDetail != null) PostEvent("failed", failureDetail);
            if (_lobbyIsOurs && LobbyManager.IsInLobby)
            {
                try
                {
                    LobbyManager.LeaveLobby();
                    InterfaceManager.Instance?.TransitionTo(InterfaceManager.Window.Main);
                }
                catch (Exception e) { Logger.LogWarning($"Matchmaking: leaving the lobby failed: {e.Message}"); }
            }
            _lobbyIsOurs = false;
            Logger.LogInfo($"Matchmaking: {message}");
            Overlay("MATCH NOT LAUNCHED", message + " You can host a game manually from the site's instructions.", 40f);
            SetPhase(Phase.Idle);
        }

        private static bool MapExists(string map)
        {
            if (string.IsNullOrEmpty(map)) return false;
            try
            {
                return File.Exists(Path.Combine(Application.dataPath, map)) ||
                       File.Exists(Path.Combine(BepInEx.Paths.GameRootPath, map));
            }
            catch { return false; }
        }

        private static string MapName(string map) =>
            string.IsNullOrEmpty(map) ? "?" : Path.GetFileNameWithoutExtension(map).Replace('_', ' ');

        // ---- window ----------------------------------------------------------

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        // Bring a minimised or buried game back when a match launches.
        // Windows refuses focus changes from background processes; attaching
        // to the foreground thread's input usually gets past that, and the
        // taskbar flash is the fallback signal.
        private void RestoreWindow()
        {
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd == IntPtr.Zero) return;
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
                var fg = GetForegroundWindow();
                if (fg == hwnd) return;
                var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
                var me = GetCurrentThreadId();
                var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                SetForegroundWindow(hwnd);
                if (attached) AttachThreadInput(me, fgThread, false);
                if (GetForegroundWindow() != hwnd)
                {
                    var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 3 | 12, uCount = 0, dwTimeout = 0 };
                    FlashWindowEx(ref info);   // FLASHW_ALL | FLASHW_TIMERNOFG: until it's looked at
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: window restore failed: {e.Message}");
            }
        }

        // ---- overlay ---------------------------------------------------------

        private static Texture2D _mmPanelTex;
        private static GUIStyle _mmTitle, _mmBody;

        private void Overlay(string title, string text, float seconds)
        {
            _overlayTitle = title;
            _overlayText = text;
            _overlayUntil = Time.realtimeSinceStartup + seconds;
        }

        private void OnGUI()
        {
            if (_overlayText == null || Time.realtimeSinceStartup > _overlayUntil) return;
            if (_mmPanelTex == null)
            {
                _mmPanelTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _mmPanelTex.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.09f, 0.9f));
                _mmPanelTex.Apply();
                _mmTitle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.3f, 0.6f, 0.95f) } };
                _mmBody = new GUIStyle { fontSize = 14, wordWrap = true, normal = { textColor = Color.white } };
            }

            var scale = Screen.height / 1080f;
            var previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var width = Screen.width / scale;
            var rect = new Rect((width - 520) / 2, 90, 520, 0);
            var textHeight = _mmBody.CalcHeight(new GUIContent(_overlayText), 492);
            rect.height = 14 + 16 + 4 + textHeight + 14;
            GUI.DrawTexture(rect, _mmPanelTex);
            GUI.Label(new Rect(rect.x + 14, rect.y + 12, 492, 16), "LADDER  ·  " + _overlayTitle, _mmTitle);
            GUI.Label(new Rect(rect.x + 14, rect.y + 32, 492, textHeight), _overlayText, _mmBody);
            GUI.matrix = previous;
        }
    }
}
