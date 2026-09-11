using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Reports ranked 1v1 results to the SanctuaryDB ladder, automatically.
    //
    // The host computes each army's win condition in Lua and broadcasts every
    // change to every client ("WinConditionUpdate"). The dispatcher reaches
    // that function through a module-table lookup on client/winCondition.lua
    // at call time, so wrapping the table field (the AssistUpgrade technique,
    // read-only variant) sees every update without touching a file — the
    // lobby's Lua hash is unchanged and this stays MP-compatible.
    //
    // Identity is the game's own Steam session: at report time the plugin
    // mints a Steam web-API auth ticket (GetAuthTicketForWebApi) and sends it
    // with the result; the ladder server has Steam verify the ticket. That
    // proves which Steam account sent the report, not that the result in it
    // is true: the ladder applies a player's own loss at once, holds a
    // claimed win for the opponent to confirm, and freezes contradicting
    // reports as disputed. Nothing to configure, no tokens, no account
    // linking. Every ticket is cancelled once its request is over, as Steam
    // requires, and any still outstanding when the plugin unloads.
    //
    // It reports only the ladder's shape: a Steam lobby with exactly two
    // human players, on opposing teams, and no AI. Skirmish, LAN, AI and team
    // games are recognised and left alone, as is a game this player is only
    // watching. Spectators in a ladder game don't stop it reporting. The
    // server ignores reports for games that aren't an open ladder match, so
    // playing unranked with a friend is fine.
    [BepInPlugin("com.sanctuarydb.ladderreporter", "Ladder Reporter", "0.3.1")]
    public partial class LadderReporterPlugin : BaseUnityPlugin
    {
        private const string TicketIdentity = "sanctuarydb-ladder";

        private Harmony _harmony;
        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<string> _cfgEndpoint;
        private ConfigEntry<bool> _cfgDryRun;

        // ---- per-match state, reset when the match ends -------------------
        private MatchSnapshot _snapshot;
        private bool _hookInstalled;
        private bool _reported;
        private bool _snapshotFailed;
        private float _tickAccum;

        // Steam web-API tickets arrive by callback; each request remembers
        // what to do with its ticket (null on refusal). Shared with the
        // matchmaking session exchange in Matchmaking.cs. Every handle Steam
        // issued stays in _liveTickets until ReleaseTicket cancels it.
        private Callback<GetTicketForWebApiResponse_t> _ticketCallback;
        private readonly Dictionary<uint, Action<uint, string>> _ticketWaiters = new Dictionary<uint, Action<uint, string>>();
        private readonly Dictionary<uint, HAuthTicket> _liveTickets = new Dictionary<uint, HAuthTicket>();

        private sealed class Participant
        {
            public ulong SteamId;
            public int ArmyId;
            public int Team;
            public string Name;
        }

        private sealed class MatchSnapshot
        {
            public bool Reportable;
            public string MapName;
            public List<Participant> Humans;    // humans commanding an army
            public List<Participant> Observers; // humans watching (see ArmyCountChunk)
            public float StartRealtime;
        }

        // The lobby has no observer dropdown yet: a human whose armyID is
        // beyond the map's army slots is made an observer at match start
        // (script.lua, "Till we have proper observer dropdown from in the
        // lobby"). The lobby state still lists them as PlayerType.Player, so
        // the map's army count is what tells a spectator from a competitor.
        // PlayerType.Observer counts as a spectator too, for when the lobby
        // starts sending it.
        private const string ArmyCountChunk =
            "__SdbLadderArmyCount = '' " +
            "pcall(function() " +
            "  local n = 0 " +
            "  for _ in pairs(GameInfo.MapData.armies) do n = n + 1 end " +
            "  __SdbLadderArmyCount = tostring(n) " +
            "end)";

        // Wrap the client's WinConditionUpdate; every {armyID, condition} the
        // host broadcasts is appended to a _G global this side polls. Guarded
        // by a global so re-running is harmless; each match builds a fresh
        // Lua VM, so the hook (and the guard) vanish with the old one.
        private const string InstallChunk =
            "if not __SdbLadderHook then " +
            "  __SdbLadderHook = true " +
            "  __SdbLadderWCU = '' " +
            "  local m = Import('client/winCondition.lua') " +
            "  __SdbLadderOrig = m.WinConditionUpdate " +
            "  m.WinConditionUpdate = function(data) " +
            "    pcall(function() " +
            "      __SdbLadderWCU = __SdbLadderWCU .. tostring(data.armyID) .. ':' .. tostring(data.condition) .. ';' " +
            "    end) " +
            // The game's own handling always runs, hook or no hook.
            "    return __SdbLadderOrig(data) " +
            "  end " +
            "end";

        private const string RemoveChunk =
            "if __SdbLadderHook and __SdbLadderOrig then " +
            "  local m = Import('client/winCondition.lua') " +
            "  m.WinConditionUpdate = __SdbLadderOrig " +
            "  __SdbLadderHook = nil " +
            "  __SdbLadderOrig = nil " +
            "end";

        private void Awake()
        {
            _log ??= Logger;

            _cfgEnabled = Config.Bind("Report", "Enabled", true,
                "Report ranked 1v1 results to the SanctuaryDB ladder when the game ends. Only Steam lobbies " +
                "with exactly two human players on opposing teams and no AI are reported; the ladder ignores " +
                "games that aren't an open ladder match, so unranked 1v1s are unaffected.");
            // www, not the apex: the apex 308-redirects, and UnityWebRequest
            // drops the POST body when it follows a redirect — the report
            // arrives empty. Talk to the canonical host directly.
            _cfgEndpoint = Config.Bind("Report", "Endpoint", "https://www.sanctuarydb.net/api/report",
                "Where results are sent.");
            _cfgDryRun = Config.Bind("Report", "DryRun", false,
                "Log the report instead of sending it. For testing.");

            try
            {
                // The economy stream doubles as the in-match signal (see
                // HudCore.ApplyEconomyPatch) — same pattern as the other mods.
                _harmony = new Harmony("com.sanctuarydb.ladderreporter." + Guid.NewGuid().ToString("N").Substring(0, 8));
                ApplyEconomyPatch(_harmony);
            }
            catch (Exception e)
            {
                Logger.LogError($"Ladder reporter: economy patch failed (results will not be reported): {e}");
            }
            AwakeMatchmaking();
            // The assembly version changes every build, so a log can be
            // matched to the DLL that produced it.
            Logger.LogInfo($"Ladder reporter loaded, build {typeof(LadderReporterPlugin).Assembly.GetName().Version} " +
                           $"(toggle it from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            DestroyMatchmaking();
            _harmony?.UnpatchSelf();
            try
            {
                if (_hookInstalled && LuaReady) RunLua(RemoveChunk);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder reporter: win-condition hook could not be removed: {e.Message}");
            }
            // A coroutine doesn't run its finally block when the component
            // goes, so any ticket one still held is cancelled here.
            ReleaseAllTickets();
            _ticketCallback?.Dispose();
        }

        private void Update()
        {
            // Matchmaking first and on its own: a fault in the shared HUD
            // polling must not starve the launch state machine, or vice versa.
            try { UpdateMatchmaking(); }
            catch (Exception e)
            {
                if (!_mmUpdateFailedLogged)
                {
                    _mmUpdateFailedLogged = true;
                    Logger.LogError($"Matchmaking: update failed: {e}");
                }
            }
            SharedTick();
            if (!_cfgEnabled.Value) return;

            _tickAccum += Time.unscaledDeltaTime;
            if (_tickAccum < 1f) return;
            _tickAccum = 0f;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // A game update that renames a type lands here (as a
                // TypeLoad/MissingField from the JIT); don't spam every tick.
                if (!_snapshotFailed)
                {
                    _snapshotFailed = true;
                    Logger.LogError($"Ladder reporter: disabled for this session, the game's internals moved: {e.Message}");
                }
            }
        }

        private void Tick()
        {
            if (_snapshotFailed) return;

            if (!InMatch)
            {
                if (_snapshot != null || _hookInstalled || _reported)
                {
                    _snapshot = null;
                    _hookInstalled = false; // the VM went down with the match
                    _reported = false;
                }
                return;
            }

            _snapshot ??= TrySnapshot(); // null until the lobby state is readable
            if (_snapshot == null || !_snapshot.Reportable || _reported) return;

            if (!_hookInstalled)
            {
                if (!LuaReady) return;
                if (RunLua(InstallChunk))
                {
                    _hookInstalled = true;
                    Logger.LogInfo("Ladder reporter: watching this match for a result.");
                }
                return;
            }

            var raw = GetLuaGlobal("__SdbLadderWCU");
            if (string.IsNullOrEmpty(raw)) return;

            // "armyID:condition;" pairs, last write per army wins.
            // Conditions: 0 Undecided, 1 Won, 2 Lost (common/winCondition.lua).
            var conditions = new Dictionary<int, int>();
            foreach (var pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = pair.IndexOf(':');
                if (colon <= 0) continue;
                if (int.TryParse(pair.Substring(0, colon), out var armyId) &&
                    int.TryParse(pair.Substring(colon + 1), out var condition))
                {
                    conditions[armyId] = condition;
                }
            }

            var wonArmies = new HashSet<int>(conditions.Where(kv => kv.Value == 1).Select(kv => kv.Key));
            if (wonArmies.Count == 0) return; // still playing (or a no-winner wipe — leave those to manual reporting)

            var winners = _snapshot.Humans.Where(p => wonArmies.Contains(p.ArmyId)).ToList();
            _reported = true; // one attempt per match, however it goes

            if (winners.Count == 0 || winners.Count == _snapshot.Humans.Count)
            {
                // An AI won, or every human "won" — not a 1v1 result.
                Logger.LogInfo("Ladder reporter: game decided but not a 1v1 human result; nothing to report.");
                return;
            }

            SendReport(winners);
        }

        // Reads the roster once per match. Returning null retries next tick;
        // a non-reportable snapshot (with the reason logged) ends the matter
        // for this match.
        private MatchSnapshot TrySnapshot()
        {
            if (!LobbyManager.IsInLobby) return null; // solo skirmishes have no lobby session
            var state = LobbyManager.CurrentState;
            if (state?.players == null || state.players.Count == 0) return null;

            // Map data lands in the client VM early in loading; until it has,
            // the chunk leaves the global empty and we try again next tick.
            if (!LuaReady || !RunLua(ArmyCountChunk)) return null;
            if (!int.TryParse(GetLuaGlobal("__SdbLadderArmyCount"), out var armyCount) || armyCount <= 0) return null;

            Participant ToParticipant(LobbyPlayer p) => new Participant
            {
                SteamId = p.id.value,
                ArmyId = p.armyID,
                Team = p.team,
                Name = p.name ?? "",
            };
            bool Seated(LobbyPlayer p) => p.armyID >= 1 && p.armyID <= armyCount;
            var players = state.players.Where(p => p != null).ToList();
            var snapshot = new MatchSnapshot
            {
                MapName = Path.GetFileNameWithoutExtension(state.mapPath ?? ""),
                StartRealtime = Time.realtimeSinceStartup,
                Humans = players.Where(p => p.type == PlayerType.Player && Seated(p)).Select(ToParticipant).ToList(),
                Observers = players.Where(p => p.type == PlayerType.Observer || (p.type == PlayerType.Player && !Seated(p)))
                    .Select(ToParticipant).ToList(),
            };
            // An AI in any slot makes it a skirmish or a team game, not a 1v1.
            var aiCount = players.Count(p => p.type == PlayerType.AI);

            var localId = LobbyManager.localPlayerID.value;
            string skip = null;
            if (!(LobbyManager.Backend is SteamLobbyBackend)) skip = "LAN lobby (no Steam identities)";
            else if (!SteamManager.IsSteamInitialized) skip = "Steam session not initialised";
            else if (snapshot.Observers.Any(p => p.SteamId == localId)) skip = "observing, not playing";
            else if (aiCount > 0) skip = $"{aiCount} AI player(s) in the game";
            else if (snapshot.Humans.Count != 2)
                skip = $"{snapshot.Humans.Count} human player(s) plus {snapshot.Observers.Count} observer(s), ladder games have 2 players";
            else if (snapshot.Humans.All(p => p.SteamId != localId)) skip = "not one of the two players";
            // Armies on the same team are allies (gameUtils.lua, CreateArmies),
            // so two humans sharing one are playing together, not a 1v1.
            else if (snapshot.Humans[0].Team == snapshot.Humans[1].Team) skip = $"both players are on team {snapshot.Humans[0].Team}";

            snapshot.Reportable = skip == null;
            var roster = string.Join(" vs ", snapshot.Humans.Select(p => p.Name)) +
                         (snapshot.Observers.Count > 0 ? $" ({snapshot.Observers.Count} observing)" : "");
            Logger.LogInfo(snapshot.Reportable
                ? $"Ladder reporter: ranked-shaped game on {snapshot.MapName}, {roster} — will report the result."
                : $"Ladder reporter: not a ladder game ({skip}).");
            return snapshot;
        }

        // ---- ticket + delivery --------------------------------------------

        private void SendReport(List<Participant> winners)
        {
            var body = BuildPayload(winners);

            if (_cfgDryRun.Value)
            {
                Logger.LogInfo($"Ladder reporter (dry run): {body}");
                return;
            }

            Logger.LogInfo("Ladder reporter: result detected, requesting a Steam ticket…");
            RequestTicket((ticketId, ticket) =>
            {
                if (ticket == null)
                {
                    Logger.LogWarning("Ladder reporter: no Steam ticket; report the result on the site instead.");
                    return;
                }
                var json = "{\"ticket\":\"" + ticket + "\"," + body.Substring(1);
                StartCoroutine(PostRoutine(_cfgEndpoint.Value, json, ticketId));
            });
        }

        /// Asks Steam for a web-API ticket and hands `onTicket` its handle and
        /// hex string, or a null string if Steam refused (a refused handle is
        /// already cancelled). The callback arrives via the game's own
        /// per-frame SteamAPI.RunCallbacks pump. A ticket stays valid until it
        /// is cancelled, so the caller owes ReleaseTicket once the request the
        /// ticket authenticates has completed, failed or been abandoned.
        /// Returns the handle, or 0 when Steam issued none.
        private uint RequestTicket(Action<uint, string> onTicket)
        {
            _ticketCallback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
            var handle = SteamUser.GetAuthTicketForWebApi(TicketIdentity);
            if (handle == HAuthTicket.Invalid)
            {
                onTicket(0, null);
                return 0;
            }
            _liveTickets[handle.m_HAuthTicket] = handle;
            _ticketWaiters[handle.m_HAuthTicket] = onTicket;
            return handle.m_HAuthTicket;
        }

        private void OnTicket(GetTicketForWebApiResponse_t response)
        {
            var id = response.m_hAuthTicket.m_HAuthTicket;
            // No waiter: not ours, or a request abandoned (and its ticket
            // cancelled) before Steam answered. A late answer to an abandoned
            // request must not start anything.
            if (!_ticketWaiters.TryGetValue(id, out var waiter)) return;
            _ticketWaiters.Remove(id);

            if (response.m_eResult != EResult.k_EResultOK)
            {
                Logger.LogWarning($"Ladder reporter: Steam refused an auth ticket ({response.m_eResult}).");
                ReleaseTicket(id);
                waiter(id, null);
                return;
            }

            var ticket = new StringBuilder(response.m_cubTicket * 2);
            for (var i = 0; i < response.m_cubTicket; i++)
            {
                ticket.Append(response.m_rgubTicket[i].ToString("x2"));
            }
            waiter(id, ticket.ToString());
        }

        /// Cancels a ticket (ISteamUser::CancelAuthTicket) and forgets any
        /// callback still waiting for it. Safe to call twice, or with 0.
        private void ReleaseTicket(uint id)
        {
            _ticketWaiters.Remove(id);
            if (!_liveTickets.TryGetValue(id, out var handle)) return;
            _liveTickets.Remove(id);
            try
            {
                SteamUser.CancelAuthTicket(handle);
                Logger.LogInfo($"Steam ticket {id} cancelled.");
            }
            catch (Exception e)
            {
                // Steam has already shut down with the game, and the ticket with it.
                Logger.LogWarning($"Steam ticket {id} could not be cancelled: {e.Message}");
            }
        }

        private void ReleaseAllTickets()
        {
            foreach (var id in _liveTickets.Keys.ToList()) ReleaseTicket(id);
            _ticketWaiters.Clear();
        }

        private string BuildPayload(List<Participant> winners)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"identity\":\"").Append(TicketIdentity).Append("\",");
            sb.Append("\"appId\":").Append(SteamUtils.GetAppID().m_AppId.ToString(CultureInfo.InvariantCulture)).Append(',');
            // A matchmade game carries its match id so the site can close
            // the match and tie the result to it.
            if (!string.IsNullOrEmpty(_mmReportMatchId))
            {
                sb.Append("\"matchId\":\"").Append(JsonEscape(_mmReportMatchId)).Append("\",");
            }
            sb.Append("\"mapName\":\"").Append(JsonEscape(_snapshot.MapName)).Append("\",");
            // Approximate: real time from the first readable roster, a few
            // seconds into the match, pauses included. The ladder doesn't read
            // it today; derive it from the simulation tick before anything
            // relies on it.
            sb.Append("\"durationSeconds\":")
                .Append(((int)Math.Max(0f, Time.realtimeSinceStartup - _snapshot.StartRealtime)).ToString(CultureInfo.InvariantCulture))
                .Append(',');
            sb.Append("\"participants\":[");
            for (var i = 0; i < _snapshot.Humans.Count; i++)
            {
                var p = _snapshot.Humans[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"steamId\":\"").Append(p.SteamId).Append("\",");
                sb.Append("\"armyId\":").Append(p.ArmyId).Append(',');
                sb.Append("\"team\":").Append(p.Team).Append(',');
                sb.Append("\"name\":\"").Append(JsonEscape(p.Name)).Append("\"}");
            }
            sb.Append("],");
            sb.Append("\"winnerSteamIds\":[");
            for (var i = 0; i < winners.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(winners[i].SteamId).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // UnityWebRequest, not HttpClient: Unity's Mono BCL can't complete a
        // TLS handshake against modern cert chains ("An error occurred while
        // sending the request"), while UnityWebRequest uses the engine's
        // native TLS. A coroutine never blocks the frame — SendWebRequest is
        // asynchronous and this only wakes to check on it.
        private IEnumerator PostRoutine(string endpoint, string json, uint ticketId)
        {
            try
            {
                foreach (var step in PostAttempts(endpoint, json)) yield return step;
            }
            finally
            {
                // Delivered, rejected or given up on: the ticket is spent.
                ReleaseTicket(ticketId);
            }
        }

        private IEnumerable PostAttempts(string endpoint, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(payload) { contentType = "application/json" };
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 15;
                    yield return request.SendWebRequest();

                    var status = (int)request.responseCode;
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        Logger.LogInfo("Ladder reporter: result reported to the ladder.");
                        yield break;
                    }
                    if (status > 0)
                    {
                        Logger.LogWarning($"Ladder reporter: the ladder said {status}: " +
                                          $"{Truncate(request.downloadHandler?.text, 200)} (attempt {attempt}/3)");
                        // 4xx is a decision, not an outage — retrying won't
                        // change it. Log what was sent (ticket masked) so the
                        // rejection is debuggable from this side alone.
                        if (status < 500)
                        {
                            Logger.LogWarning("Ladder reporter: rejected payload was " +
                                              System.Text.RegularExpressions.Regex.Replace(
                                                  json, "\"ticket\":\"[0-9a-f]*\"", "\"ticket\":\"…\""));
                            yield break;
                        }
                    }
                    else
                    {
                        Logger.LogWarning($"Ladder reporter: couldn't reach the ladder " +
                                          $"(attempt {attempt}/3): {request.error}");
                    }
                }
                yield return new WaitForSecondsRealtime(5f * attempt);
            }
            Logger.LogWarning("Ladder reporter: giving up — report the result on the site instead.");
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
