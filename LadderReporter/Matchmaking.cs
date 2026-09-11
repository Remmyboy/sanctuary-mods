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
using EM.DOTS.Engine.Loader;
using EM.Network;
using EM.Network.Lobby;
using EM.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Ladder matchmaking: the in-game half of "queue on the site, get launched
    // into the game". The site pairs players, picks the map, factions, slots
    // and host, and runs the countdown; when a match reaches `launch` this
    // side creates or joins the lobby, sets its own seat, and (as host)
    // starts the game — no lobby interaction from either player. Nobody is
    // required to have this: the site only picks the auto path when both
    // sides have the game open in the menu with the mod.
    //
    // The site never calls the mod: the SanctuaryDB page in the player's
    // browser does, over the loopback listener in LocalBridge.cs. The page
    // reads the game's state there and relays it inside the polls it already
    // makes, and hands the match object over when there is one. Everything
    // here is driven by that match object (see docs/matchmaking-site-plan.md
    // and the site's docs/local-bridge.md). A local timeout mirrors each of
    // the site's, so both sides converge even when a push is late. The only
    // calls the mod makes to the site itself are the rare ones that carry a
    // bearer token: the session id, progress events and the result report.
    public partial class LadderReporterPlugin
    {
        private const string ModVersion = "0.3.1";

        private ConfigEntry<bool> _cfgMmEnabled;
        private ConfigEntry<string> _cfgMmBaseUrl;
        private ConfigEntry<string> _cfgMmMockFile;

        // Session with the site: one Steam ticket becomes a bearer token.
        // Minted lazily, when the first real match arrives, so a player who
        // never queues never asks Steam for a ticket.
        private string _mmToken;
        private bool _mmSessionInFlight;
        private float _mmNextSessionTry;
        private uint _mmSessionTicket;         // the ticket the sign-in in flight holds, 0 if none

        private string _mmLastHttpError;
        private float _mmLastHttpErrorAt = -999f;
        private float _mockAccum;
        private float _cfgReloadAccum;

        // The match being acted on.
        private enum Phase { Idle, Leaving, HostCreating, HostWaiting, JoinerWaiting, JoinerJoining, JoinerInLobby, Started }
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

        // Mock testing is two people coordinating by hand, so every wait
        // stretches to ten minutes there; the live limits mirror the site's.
        private float Limit(float seconds) => MockMode ? Mathf.Max(seconds, 600f) : seconds;

        private sealed class MmMatch
        {
            // No map has more army slots than this; anything outside
            // 1..MaxSlot can't be a seat.
            private const int MaxSlot = 16;
            private static readonly System.Text.RegularExpressions.Regex IdShape =
                new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_-]{1,64}$");
            private static readonly System.Text.RegularExpressions.Regex WordShape =
                new System.Text.RegularExpressions.Regex(@"^[a-z_]{1,32}$");
            private static readonly System.Text.RegularExpressions.Regex SteamIdShape =
                new System.Text.RegularExpressions.Regex(@"^[0-9]{17}$");

            public string Id, Mode, Status, Host, Joiner, Map, Reason, CancelledBy, OpponentName;
            public ulong SessionId;
            public readonly Dictionary<string, string> Factions = new Dictionary<string, string>();
            public readonly Dictionary<string, int> Slots = new Dictionary<string, int>();

            /// Reads the site's match object. Throws FormatException when a
            /// field has the wrong JSON type; Problem() then checks what the
            /// values mean.
            public static MmMatch Parse(JObject o)
            {
                if (o == null) return null;
                var m = new MmMatch
                {
                    Id = Str(o, "id"),
                    Mode = Str(o, "mode") ?? "manual",
                    Status = Str(o, "status") ?? "",
                    Host = Str(o, "host"),
                    Joiner = Str(o, "joiner"),
                    Map = Str(o, "map"),
                    Reason = Str(o, "reason"),
                    CancelledBy = Str(o, "cancelledBy"),
                    OpponentName = o["opponent"] is JObject opponent ? Str(opponent, "name") : null,
                };
                var sid = o["sessionId"];
                if (sid != null && sid.Type != JTokenType.Null)
                {
                    if ((sid.Type != JTokenType.String && sid.Type != JTokenType.Integer) ||
                        !ulong.TryParse(sid.ToString(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out m.SessionId))
                    {
                        throw new FormatException("sessionId must be a whole number");
                    }
                }
                if (o["factions"] is JObject f)
                {
                    foreach (var kv in f)
                    {
                        var faction = Str(f, kv.Key);
                        if (faction != null) m.Factions[kv.Key] = faction;
                    }
                }
                if (o["slots"] is JObject s)
                {
                    foreach (var kv in s)
                    {
                        if (kv.Value == null || kv.Value.Type == JTokenType.Null) continue;
                        if (kv.Value.Type != JTokenType.Integer) throw new FormatException($"slot for {kv.Key} must be a whole number");
                        var slot = (long)kv.Value;
                        if (slot < 1 || slot > MaxSlot) throw new FormatException($"slot for {kv.Key} is {slot}, outside 1..{MaxSlot}");
                        m.Slots[kv.Key] = (int)slot;
                    }
                }
                return m;
            }

            private static string Str(JObject o, string key)
            {
                var t = o[key];
                if (t == null || t.Type == JTokenType.Null) return null;
                if (t.Type != JTokenType.String) throw new FormatException($"'{key}' must be a string");
                return (string)t;
            }

            /// What is wrong with the match, or null. The site is trusted to
            /// send sense, but the bridge is still a boundary: the id ends up
            /// in URL paths, Steam IDs decide who gets kicked, and the map is a
            /// file path.
            public string Problem()
            {
                if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Status)) return "match needs an id and a status";
                if (!IdShape.IsMatch(Id)) return "id must be 1-64 letters, digits, '-' or '_'";
                if (!WordShape.IsMatch(Status)) return "status is not a status word";
                if (!WordShape.IsMatch(Mode)) return "mode is not a mode word";
                if (Host != null && !SteamIdShape.IsMatch(Host)) return "host is not a Steam ID";
                if (Joiner != null && !SteamIdShape.IsMatch(Joiner)) return "joiner is not a Steam ID";
                if (Host != null && Host == Joiner) return "host and joiner are the same player";
                foreach (var player in Slots.Keys.Concat(Factions.Keys))
                {
                    if (!SteamIdShape.IsMatch(player)) return $"'{player}' in slots or factions is not a Steam ID";
                }
                if (Slots.Values.Distinct().Count() != Slots.Count) return "two players share a slot";
                if (Map != null && !IsMapPath(Map)) return "map is not a relative Maps/.../*.sanmap path";
                return null;
            }
        }

        /// A game map path as the site sends it: relative, under the game's
        /// Maps folder, a .sanmap, with no empty, "." or ".." segments. Checked
        /// before the path gets near the file system, where Path.Combine would
        /// otherwise let a rooted path replace the base folder entirely.
        private static bool IsMapPath(string map)
        {
            if (string.IsNullOrEmpty(map) || map.Length > 260) return false;
            if (map.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || map.IndexOf(':') >= 0) return false;
            if (Path.IsPathRooted(map)) return false;
            if (!map.EndsWith(".sanmap", StringComparison.OrdinalIgnoreCase)) return false;
            var segments = map.Split('/', '\\');
            if (segments.Length < 2 || !string.Equals(segments[0], "Maps", StringComparison.OrdinalIgnoreCase)) return false;
            return segments.All(s => s.Length > 0 && s != "." && s != "..");
        }

        // ---- lifecycle -------------------------------------------------------

        private void AwakeMatchmaking()
        {
            _cfgMmEnabled = Config.Bind("Matchmaking", "Enabled", true,
                "Let the ladder launch you straight into a matchmade game. The SanctuaryDB page in your browser " +
                "sees that the game is open in the main menu; when both players in a match have it, the site " +
                "counts down and the mods create and join the lobby and start the game. Nothing changes for " +
                "players without it.");
            _cfgMmBaseUrl = Config.Bind("Matchmaking", "BaseUrl", "https://www.sanctuarydb.net",
                "The ladder site. Endpoints are under /api/mm/.");
            _cfgMmMockFile = Config.Bind("Matchmaking", "MockFile", "",
                "For testing without the site: a JSON file holding the match object. Read every few seconds " +
                "in place of what the page would push; session and event posts are logged, not sent.");
            AwakeBridge();

            // The game already runs in the background (checked on the playtest
            // build), so a minimised window keeps polling; assert it anyway
            // so a patch changing that can't silently stall matchmaking.
            _runInBackgroundWas = Application.runInBackground;
            Application.runInBackground = true;

            // A refused join surfaces only as the game's error modal; a kick
            // only as the lobby quietly emptying. Catch both with their
            // reasons so a failed launch says why.
            LobbyManager.OnKicked += OnMmKicked;
            try
            {
                _mmHarmony = new Harmony("com.sanctuarydb.ladderreporter.mm." + Guid.NewGuid().ToString("N").Substring(0, 8));
                var joined = AccessTools.Method(typeof(InterfaceManager), "OnSteamEventLobbyJoined");
                if (joined != null)
                {
                    _mmHarmony.Patch(joined, postfix: new HarmonyMethod(typeof(LadderReporterPlugin), nameof(LobbyJoinedPostfix)));
                }
                else
                {
                    Logger.LogWarning("Matchmaking: InterfaceManager.OnSteamEventLobbyJoined not found; a refused join will time out instead of reporting why.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: join-result patch failed: {e.Message}");
            }
        }

        private Harmony _mmHarmony;
        private static string _lastJoinFailure;
        private bool _sawLobby;

        private void DestroyMatchmaking()
        {
            StopBridge();
            LobbyManager.OnLobbyCreated -= OnMmLobbyCreated;
            LobbyManager.OnKicked -= OnMmKicked;
            _mmHarmony?.UnpatchSelf();
            Application.runInBackground = _runInBackgroundWas;
        }

        private static void LobbyJoinedPostfix(bool isSuccessful, string failureReason)
        {
            if (isSuccessful) return;
            _lastJoinFailure = failureReason ?? "unknown";
        }

        private void OnMmKicked()
        {
            if (_phase == Phase.JoinerJoining || _phase == Phase.JoinerInLobby)
            {
                Abort("The host's game removed you from the lobby.", "kicked by host");
            }
        }

        private void UpdateMatchmaking()
        {
            if (_cfgMmEnabled == null) return;
            var dt = Time.unscaledDeltaTime;

            // Re-read the config file so a tester without the F8 window can
            // set MockFile (or flip Enabled) by editing it, no restart.
            _cfgReloadAccum += dt;
            if (_cfgReloadAccum >= 15f)
            {
                _cfgReloadAccum = 0f;
                try { Config.Reload(); } catch { }
            }

            // The listener starts and stops with the Enabled flag, so this
            // runs even while disabled; everything below it doesn't.
            UpdateBridge();
            if (!_cfgMmEnabled.Value) return;

            if (MockMode)
            {
                _mockAccum += dt;
                if (_mockAccum >= 5f)
                {
                    _mockAccum = 0f;
                    try { ApplyMatch(ReadMock()); }
                    catch (Exception e) { Logger.LogWarning($"Matchmaking (mock): {e.Message}"); }
                }
            }
            else if (_mmToken == null && NeedsSession(_match))
            {
                // An auto match is on and the site posts (session id,
                // events) need the token; keep asking until it comes.
                // EnsureSession throttles itself.
                if (UsingSteam) EnsureSession();
                else if (!_loggedNoSteam)
                {
                    _loggedNoSteam = true;
                    Logger.LogWarning("Matchmaking: a match arrived but Steam isn't up (backend " +
                                      $"{LobbyManager.Backend?.GetType().Name ?? "none"}, initialised {SteamManager.IsSteamInitialized}).");
                }
            }

            // A replay closed for a countdown that never became a launch
            // (cancelled, or the pair went manual): stop reporting on its
            // behalf once the menu has had ample time to come back.
            if (_leavingReplay && _phase == Phase.Idle && Time.realtimeSinceStartup - _leaveRequestedAt > 60f) ForgetReplayQuit();

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

        // What the site needs to know about where we are: menu, lobby,
        // loading, ingame or replay. The site decides which of those it will
        // launch into; this side can launch from menu, lobby (leaving it) and
        // replay (closing it).
        private string CurrentState()
        {
            // Watching a replay looks like a match to the economy signal and
            // like the menu when it's paused; it is neither.
            if (NetworkManager.IsReplayPlayback) return "replay";
            // A replay this side is closing for a match: the scene reload
            // and the economy signal's five-second tail would read as a
            // game, and the menu is back as soon as the new UI is up.
            if (_leavingReplay) return MenuIsBack() ? "menu" : "replay";
            if (InMatch) return "ingame";
            if (LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby && LobbyManager.IsInLobby) return "loading";
            if (LobbyManager.IsInLobby) return "lobby";
            return "menu";
        }

        // ---- leaving a replay --------------------------------------------------

        // A replay gives way to a match (nothing is lost by closing one).
        // The game's own quit path tears it down and reloads the menu scene,
        // which brings a fresh InterfaceManager; that instance appearing is
        // how "the menu is back" is known. No dependency on ReplayManager:
        // the flag is the engine loader's.
        private bool _leavingReplay;
        private InterfaceManager _leaveOldUi;
        private float _leaveRequestedAt;
        private float _menuBackSince = -1f;

        private void QuitReplay()
        {
            if (_leavingReplay) return;
            Logger.LogInfo("Matchmaking: closing the replay for the ladder match.");
            _leavingReplay = true;
            _leaveOldUi = InterfaceManager.Instance;
            _leaveRequestedAt = Time.realtimeSinceStartup;
            _menuBackSince = -1f;
            EngineLoader.isGameRestartRequested = true;
        }

        private bool MenuIsBack()
        {
            if (NetworkManager.IsReplayPlayback || LobbyManager.IsInLobby) return false;
            var ui = InterfaceManager.Instance;
            return ui != null && ui != _leaveOldUi && EngineLoader.Instance != null;
        }

        private void ForgetReplayQuit()
        {
            _leavingReplay = false;
            _leaveOldUi = null;
            _menuBackSince = -1f;
        }

        // ---- session ---------------------------------------------------------

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
                // Only while Steam is what's being waited on: once the ticket
                // is in, the web request's own timeout applies.
                if (_ticketWaiters.ContainsKey(_mmSessionTicket) && now - _mmSessionStarted > 30f)
                {
                    Logger.LogWarning("Matchmaking: Steam did not answer the ticket request in 30 s; is Steam online? Retrying.");
                    // Abandon it properly: cancel the handle and drop the
                    // waiter, so a late answer can't start a stale sign-in
                    // alongside the retry.
                    ReleaseTicket(_mmSessionTicket);
                    _mmSessionTicket = 0;
                    _mmSessionInFlight = false;
                    _mmNextSessionTry = now + 30f;
                }
                return;
            }
            if (now < _mmNextSessionTry) return;
            _mmSessionInFlight = true;
            _mmSessionStarted = now;
            Logger.LogInfo("Matchmaking: requesting a Steam ticket to sign in to the ladder.");
            _mmSessionTicket = RequestTicket((ticketId, ticket) =>
            {
                if (ticket == null)
                {
                    Logger.LogWarning("Matchmaking: Steam refused a ticket; retrying in a minute.");
                    _mmSessionTicket = 0;
                    _mmSessionInFlight = false;
                    _mmNextSessionTry = Time.realtimeSinceStartup + 60f;
                    return;
                }
                StartCoroutine(SessionRoutine(ticket, ticketId));
            });
        }

        private IEnumerator SessionRoutine(string ticket, uint ticketId)
        {
            try
            {
                foreach (var step in SessionExchange(ticket)) yield return step;
            }
            finally
            {
                // Signed in or not, the ticket has done its job.
                ReleaseTicket(ticketId);
                if (_mmSessionTicket == ticketId) _mmSessionTicket = 0;
            }
        }

        private IEnumerable SessionExchange(string ticket)
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

        private string _loggedMatchKey;

        // The page pushes the same match every few seconds; one line per
        // change of id, status or mode is enough to follow a launch in the log.
        private void LogMatchOnce(MmMatch match)
        {
            var key = match.Id + ":" + match.Status + ":" + match.Mode;
            if (key == _loggedMatchKey) return;
            _loggedMatchKey = key;
            Logger.LogInfo($"Matchmaking: match {match.Id} mode={match.Mode} status={match.Status} " +
                           $"host={(match.Host == LocalSteamId ? "me" : match.Host)} " +
                           $"joiner={(match.Joiner == LocalSteamId ? "me" : match.Joiner)} map={match.Map} " +
                           $"session={match.SessionId} reason={match.Reason ?? "-"}");
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

        // Don't repeat the same failure while the site is down.
        private void LogHttp(string what, UnityWebRequest req)
        {
            var msg = (int)req.responseCode > 0
                ? $"{req.responseCode}: {Truncate(req.downloadHandler?.text, 160)}"
                : req.error;
            if (msg == _mmLastHttpError && Time.realtimeSinceStartup - _mmLastHttpErrorAt < 300f) return;
            _mmLastHttpError = msg;
            _mmLastHttpErrorAt = Time.realtimeSinceStartup;
            Logger.LogWarning($"Matchmaking: {what} failed, {msg}");
        }

        private IEnumerator PostAndForget(string path, JObject body, string what)
        {
            if (MockMode)
            {
                Logger.LogInfo($"Matchmaking (mock): would POST {path} {body.ToString(Newtonsoft.Json.Formatting.None)}");
                yield break;
            }
            // The token is minted when the match first arrives, which is
            // normally seconds before anything here needs it; give the Steam
            // ticket and the session exchange a moment rather than dropping
            // the post.
            var waitUntil = Time.realtimeSinceStartup + 15f;
            while (_mmToken == null && Time.realtimeSinceStartup < waitUntil)
            {
                if (UsingSteam) EnsureSession();
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (_mmToken == null)
            {
                Logger.LogWarning($"Matchmaking: {what} not sent, no ladder session.");
                yield break;
            }
            var req = Post(path, body, _mmToken);
            yield return req.SendWebRequest();
            if ((int)req.responseCode == 401) _mmToken = null;   // expired; the next post signs in again
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
                if (_phase == Phase.Idle) ForgetReplayQuit();
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
                        // A replay is closed now rather than at launch: the
                        // scene reload takes a few seconds, and the
                        // countdown has them to spare where the site's
                        // lobby-creation window doesn't.
                        var closingReplay = m.Mode == "auto" && NetworkManager.IsReplayPlayback;
                        if (closingReplay) QuitReplay();
                        Overlay("MATCH FOUND", m.Mode != "auto"
                            ? $"vs {opponent}. {(isHost ? "You're hosting" : "They're hosting")}, see the site."
                            : closingReplay
                                ? $"vs {opponent} on {MapName(m.Map)}. Closing the replay; launching when the site's countdown ends."
                                : $"vs {opponent} on {MapName(m.Map)}. Launching when the site's countdown ends.", 20f);
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
                    if (_phase == Phase.Idle) ForgetReplayQuit();
                    _match = null;
                    break;

                case "done":
                    if (_phase == Phase.Idle) ForgetReplayQuit();
                    _match = null;
                    break;
            }
        }

        private void BeginLaunch(MmMatch m, bool isHost)
        {
            _eventsSent.Clear();
            _launchSince = Time.realtimeSinceStartup;
            _lobbyIsOurs = false;

            // Any menu screen is fine (settings, the lobby browser, the
            // profile page all count as `menu`). A game that is already
            // playing or loading one is left alone; a lobby the player is
            // sitting in is left for them, and a replay closed, below.
            var state = CurrentState();
            if (state == "replay") QuitReplay();   // no-op if the countdown already did
            if (state == "ingame")
            {
                Abort("The match launched while this game was in a match or a replay.", "in a game");
                return;
            }
            if (state == "loading")
            {
                Abort("The match launched while this game was loading another one.", "loading a game");
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

            if (state == "lobby")
            {
                // The player queued on purpose, so their current lobby gives
                // way (as the host, that closes it for everyone in it).
                // Leaving a Steam lobby isn't instant, and creating or
                // joining another while it is still attached fails, so the
                // launch continues from TickMatch once the game is out.
                Logger.LogInfo("Matchmaking: leaving the current lobby for the ladder match.");
                Overlay("LAUNCHING", $"vs {opponent} on {MapName(m.Map)}: leaving your current lobby...", 120f);
                SetPhase(Phase.Leaving);
                try
                {
                    LobbyManager.LeaveLobby();
                    InterfaceManager.Instance?.TransitionTo(InterfaceManager.Window.Main);
                }
                catch (Exception e)
                {
                    Abort("Couldn't leave your current lobby: " + e.Message, "leave failed: " + e.Message);
                }
                return;
            }
            if (state == "replay")
            {
                Overlay("LAUNCHING", $"vs {opponent} on {MapName(m.Map)}: closing the replay...", 120f);
                SetPhase(Phase.Leaving);
                return;
            }
            StartLobby(m, isHost);
        }

        // Host: create the lobby; joiner: wait for (or join) the host's.
        // Runs from the menu, either straight away or once Leaving is done.
        private void StartLobby(MmMatch m, bool isHost)
        {
            ForgetReplayQuit();
            var me = LocalSteamId;
            var opponent = string.IsNullOrEmpty(m.OpponentName) ? "your opponent" : m.OpponentName;
            Overlay("LAUNCHING", $"vs {opponent} on {MapName(m.Map)}: {(isHost ? "creating the lobby" : "waiting for the host's lobby")}...", 120f);

            if (isHost)
            {
                // Logged so a kick of the wrong person is diagnosable: the
                // host keeps only this Steam ID and its own in the lobby.
                Logger.LogInfo($"Matchmaking: hosting as {me}; expecting joiner {m.Joiner ?? "(none)"}.");
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
            LobbyInterface.Instance?.Clear();
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
            Logger.LogInfo($"Matchmaking: joining session {sessionId} as {LocalSteamId}.");
            Overlay("LAUNCHING", "Joining the host's lobby...", 120f);
            _sawLobby = false;
            _lastJoinFailure = null;
            SetPhase(Phase.JoinerJoining);
            ui.JoinSessionFromInvite(new PlayerID(sessionId));
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

            // The game left the lobby for loading: launched. (Not while
            // Leaving: a lobby loading then is the old one starting a game.)
            if (_phase != Phase.Started && _phase != Phase.Idle && _phase != Phase.Leaving && LobbyManager.IsInLobby &&
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
                case Phase.Leaving:
                    if (_leavingReplay)
                    {
                        // Give the fresh menu a second to settle before
                        // asking it for a lobby.
                        if (MenuIsBack())
                        {
                            if (_menuBackSince < 0) _menuBackSince = now;
                            else if (now - _menuBackSince >= 1f) StartLobby(m, m.Host == me);
                        }
                        else if (now - _leaveRequestedAt > Limit(30f)) Abort("The replay didn't close in time.", "stuck in replay");
                    }
                    else if (!LobbyManager.IsInLobby) StartLobby(m, m.Host == me);
                    else if (LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby)
                    {
                        Abort("Your lobby started its game before this one could leave it.", "loading a game");
                    }
                    else if (inPhase > Limit(10f)) Abort("Couldn't leave your current lobby in time.", "stuck in lobby");
                    break;

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
                        joiner.team == jslot &&
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
                    if (_lastJoinFailure != null)
                    {
                        var why = _lastJoinFailure;
                        _lastJoinFailure = null;
                        Abort("The host's lobby refused the join: " + why, "join refused: " + why);
                    }
                    else if (LobbyManager.IsInLobby && LobbyManager.CurrentState != null)
                    {
                        _lobbyIsOurs = true;
                        PostEvent("joined");
                        Overlay("LAUNCHING", "In the lobby. Readying up...", 60f);
                        SetPhase(Phase.JoinerInLobby);
                    }
                    else if (_sawLobby && !LobbyManager.IsInLobby)
                    {
                        Abort("The lobby went away while joining.", "lobby lost during join");
                    }
                    else if (inPhase > Limit(30f)) Abort("Couldn't join the host's lobby.", "join timeout");
                    if (LobbyManager.IsInLobby) _sawLobby = true;
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
            // The lobby puts everyone on team 1; a 1v1 needs one team per
            // seat, so the team is the slot number.
            if (local.faction != faction) LobbyManager.SetMemberFaction(local, faction);
            else if (local.team != slot) LobbyManager.SetMemberTeam(local, slot);
            else if (local.armyID != slot) LobbyManager.SetMemberArmyID(local, slot);
            else if (!local.isReady) LobbyManager.SetMemberIsReady(local, true);
        }

        private static bool LocalIsSeated(MmMatch m, string me)
        {
            var local = FindPlayer(me);
            return local != null && local.isReady && m.Slots.TryGetValue(me, out var slot) && local.armyID == slot &&
                   local.team == slot && TryFaction(m.Factions[me], out var f) && local.faction == f;
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
            ForgetReplayQuit();
            Logger.LogInfo($"Matchmaking: {message}");
            Overlay("MATCH NOT LAUNCHED", message + " You can host a game manually from the site's instructions.", 40f);
            SetPhase(Phase.Idle);
        }

        private static bool MapExists(string map)
        {
            if (!IsMapPath(map)) return false;
            try
            {
                foreach (var baseDir in new[] { Application.dataPath, BepInEx.Paths.GameRootPath })
                {
                    // Belt and braces after IsMapPath: the resolved file must
                    // still sit inside the folder it was resolved against.
                    var root = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var full = Path.GetFullPath(Path.Combine(root, map));
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return true;
                }
                return false;
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
