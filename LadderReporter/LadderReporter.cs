using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using HarmonyLib;
using Steamworks;
using UnityEngine;
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
    // with the result; the ladder server has Steam verify the ticket, so a
    // report is exactly as trustworthy as being signed in to Steam in the
    // running game. Nothing to configure, no tokens, no account linking.
    //
    // It reports only Steam lobbies with exactly two human players — the
    // ladder's shape. Skirmish vs AI, LAN, observers and team games are
    // recognised and left alone. The server ignores reports for games that
    // aren't an open ladder match, so playing unranked with a friend is fine.
    [BepInPlugin("com.sanctuarydb.ladderreporter", "Ladder Reporter", "0.1.0")]
    public class LadderReporterPlugin : BaseUnityPlugin
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

        private Callback<GetTicketForWebApiResponse_t> _ticketCallback;
        private HAuthTicket _pendingTicket = HAuthTicket.Invalid;
        private string _pendingBody; // payload awaiting its ticket

        private static readonly HttpClient Http = CreateHttpClient();

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
            public List<Participant> Humans;
            public float StartRealtime;
        }

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
                "with exactly two human players are reported; the ladder ignores games that aren't an open " +
                "ladder match, so unranked 1v1s are unaffected.");
            _cfgEndpoint = Config.Bind("Report", "Endpoint", "https://sanctuarydb.net/api/report",
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
            Logger.LogInfo("Ladder reporter loaded (toggle it from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            try
            {
                if (_hookInstalled && LuaReady) RunLua(RemoveChunk);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder reporter: win-condition hook could not be removed: {e.Message}");
            }
            _ticketCallback?.Dispose();
        }

        private void Update()
        {
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

            var snapshot = new MatchSnapshot
            {
                MapName = Path.GetFileNameWithoutExtension(state.mapPath ?? ""),
                StartRealtime = Time.realtimeSinceStartup,
                Humans = state.players
                    .Where(p => p != null && p.type == PlayerType.Player)
                    .Select(p => new Participant
                    {
                        SteamId = p.id.value,
                        ArmyId = p.armyID,
                        Team = p.team,
                        Name = p.name ?? "",
                    })
                    .ToList(),
            };

            var localId = LobbyManager.localPlayerID.value;
            string skip = null;
            if (!(LobbyManager.Backend is SteamLobbyBackend)) skip = "LAN lobby (no Steam identities)";
            else if (!SteamManager.IsSteamInitialized) skip = "Steam session not initialised";
            else if (snapshot.Humans.Count != 2) skip = $"{snapshot.Humans.Count} human player(s), ladder games have 2";
            else if (snapshot.Humans.All(p => p.SteamId != localId)) skip = "observing, not playing";

            snapshot.Reportable = skip == null;
            Logger.LogInfo(snapshot.Reportable
                ? $"Ladder reporter: ranked-shaped game on {snapshot.MapName} — will report the result."
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

            // The ticket callback arrives via the game's own per-frame
            // SteamAPI.RunCallbacks pump; the payload waits for it.
            _pendingBody = body;
            _ticketCallback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
            _pendingTicket = SteamUser.GetAuthTicketForWebApi(TicketIdentity);
            Logger.LogInfo("Ladder reporter: result detected, requesting a Steam ticket…");
        }

        private void OnTicket(GetTicketForWebApiResponse_t response)
        {
            if (response.m_hAuthTicket != _pendingTicket || _pendingBody == null) return;
            var body = _pendingBody;
            _pendingBody = null;

            if (response.m_eResult != EResult.k_EResultOK)
            {
                Logger.LogWarning($"Ladder reporter: Steam refused an auth ticket ({response.m_eResult}); " +
                                  "report the result on the site instead.");
                return;
            }

            var ticket = new StringBuilder(response.m_cubTicket * 2);
            for (var i = 0; i < response.m_cubTicket; i++)
            {
                ticket.Append(response.m_rgubTicket[i].ToString("x2"));
            }

            var json = "{\"ticket\":\"" + ticket + "\"," + body.Substring(1);
            var endpoint = _cfgEndpoint.Value;
            Task.Run(() => PostAsync(endpoint, json));
        }

        private string BuildPayload(List<Participant> winners)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"identity\":\"").Append(TicketIdentity).Append("\",");
            sb.Append("\"appId\":").Append(SteamUtils.GetAppID().m_AppId.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"mapName\":\"").Append(JsonEscape(_snapshot.MapName)).Append("\",");
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

        private static HttpClient CreateHttpClient()
        {
            // Older Unity Mono defaults can exclude TLS 1.2; the ladder is
            // https-only, so make sure it's on before the first request.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        // Off the main thread: a lockstep RTS cannot afford a blocked frame.
        private async Task PostAsync(string endpoint, string json)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await Http.PostAsync(endpoint, content).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        Logger.LogInfo("Ladder reporter: result reported to the ladder.");
                        return;
                    }
                    Logger.LogWarning($"Ladder reporter: the ladder said {(int)response.StatusCode}: " +
                                      $"{Truncate(body, 200)} (attempt {attempt}/3)");
                    // 4xx is a decision, not an outage — retrying won't change it.
                    if ((int)response.StatusCode < 500) return;
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Ladder reporter: couldn't reach the ladder (attempt {attempt}/3): {e.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(false);
            }
            Logger.LogWarning("Ladder reporter: giving up — report the result on the site instead.");
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
