using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using EM.Network.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Opt-in uploads after a ranked game (0.4): the match's stats, shown on
    // its SanctuaryDB match page, and its replay (ReplayUpload.cs), for
    // anyone to download. Both default off; nothing is sent unless the
    // player turns them on.
    //
    // Stats come from the same Lua collector SanctuaryHud's MatchStats
    // screen uses (shared/Stats), installed here too so this works without
    // SanctuaryHud; whichever mod installs it first serves both. They are
    // pulled once, when the game's result panel comes up (or 5 s after the
    // victory), while the match's Lua VM is still alive, and posted once the
    // report has said which match this was. The site's match id comes back
    // in the /api/report answer; a matchmade game also knows it locally.
    //
    // Every call uses the bearer session from the matchmaking sign-in
    // (EnsureSession), not a Steam ticket per request.
    public partial class LadderReporterPlugin
    {
        private const int StatsPayloadFormat = 1;
        private const int StatsMaxBytes = 256 * 1024;

        private ConfigEntry<bool> _cfgUpStats;
        private ConfigEntry<bool> _cfgUpReplays;
        private ConfigEntry<int> _cfgUpMaxMB;
        private ConfigEntry<string> _cfgUpForceId;

        private static readonly Regex UuidShape =
            new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
        // What a match id may look like before it goes into a URL path or a
        // file name (the site's ids are UUIDs; matchmaking's IdShape).
        private static readonly Regex SafeIdShape = new Regex(@"^[A-Za-z0-9_-]{1,64}$");

        // One finished match's uploads, from the victory until the stats are
        // posted and the replay is handed to the pending queue.
        private sealed class UploadJob
        {
            public bool WantStats, WantReplay;
            public bool Forced, DryRun;
            public float DecidedAt;
            public List<Participant> Seats;   // the seated humans, armyId -> steamId
            public string FallbackId;         // the matchmade id, when there was one
            public string ReplayPath;         // the game's recording of this match
            public int BuildId;

            public bool Resolved;             // the report has answered (MatchId may still be null)
            public string MatchId;
            public bool StatsPulled;          // tried, whether or not it found anything
            public string StatsJson;
            public bool StatsStarted;
            public bool ReplayQueued;
        }

        private UploadJob _job;
        private bool _statsHooked;
        private float _statsAccum;
        private string _statsLuaErr;
        private string _forceIdWarned;
        private bool _uploadsFailedLogged;

        // Work finished on a background thread (copy and hash), run in Update.
        private readonly ConcurrentQueue<Action> _uploadMainThread = new ConcurrentQueue<Action>();

        private static string UploadDir => Path.Combine(BepInEx.Paths.CachePath, "LadderReporter");

        private void AwakeUploads()
        {
            _cfgUpStats = Config.Bind("Upload", "Stats", false,
                "Upload this match's stats (economy, units, score over time) to sanctuarydb.net after a ranked game, " +
                "shown on the match page.");
            _cfgUpReplays = Config.Bind("Upload", "Replays", false,
                "Upload the replay of each ranked game to sanctuarydb.net once it is decided (or after you leave the " +
                "match), so anyone can download and watch it.");
            _cfgUpMaxMB = Config.Bind("Upload", "MaxReplayMB", 30,
                "Replays bigger than this many megabytes are not uploaded.");
            _cfgUpForceId = Config.Bind("Upload", "ForceUploadMatchId", "",
                "Debug only, for testing against a site dev server (Matchmaking.BaseUrl): a match id (UUID). While set, " +
                "every finished game (against AI, any number of players) runs the stats and replay uploads against " +
                "this id, skipping the ranked 1v1 check. Upload.Stats and Upload.Replays still choose what is sent. " +
                "Leave empty.");
            ResumePending();
        }

        private void UpdateUploads()
        {
            try
            {
                while (_uploadMainThread.TryDequeue(out var work)) work();
                _statsAccum += Time.unscaledDeltaTime;
                if (_statsAccum >= 0.25f)
                {
                    _statsAccum = 0f;
                    StatsStep();
                }
                ReplayStep(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                if (_uploadsFailedLogged) return;
                _uploadsFailedLogged = true;
                Logger.LogError($"Ladder uploads: update failed: {e}");
            }
        }

        /// The forced upload id, or null when unset, malformed, or this is a replay.
        private string ForceUploadId()
        {
            var v = _cfgUpForceId?.Value?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (!UuidShape.IsMatch(v))
            {
                if (_forceIdWarned != v)
                {
                    _forceIdWarned = v;
                    Logger.LogWarning($"Ladder uploads: Upload.ForceUploadMatchId '{v}' is not a UUID; ignored.");
                }
                return null;
            }
            return NetworkManager.IsReplayPlayback ? null : v.ToLowerInvariant();
        }

        private static string UploadId(string id) =>
            !string.IsNullOrEmpty(id) && SafeIdShape.IsMatch(id) ? id.ToLowerInvariant() : null;

        // ---- the collector -------------------------------------------------

        private void StatsStep()
        {
            var job = _job;
            var pullPending = job != null && job.WantStats && !job.StatsPulled;
            var sendPending = job != null && job.StatsJson != null && !job.StatsStarted;
            var hookWanted = _cfgEnabled.Value && _cfgUpStats.Value;
            // Nothing to do: no Lua calls at all for a player who hasn't opted in.
            if (!pullPending && !sendPending && !hookWanted && !_statsHooked) return;

            if (!LuaReady)
            {
                // Between matches; the next one's VM needs its own hook.
                _statsHooked = false;
                if (pullPending)
                {
                    job.StatsPulled = true;
                    Logger.LogWarning("Ladder uploads: the match closed before its stats could be read; no stats for this one.");
                }
            }
            else
            {
                // Hooked as early as the client VM allows, as MatchStats
                // does, so the whole match is seen; the chunk itself waits
                // for the armies to exist.
                if (hookWanted && !_statsHooked && !NetworkManager.IsReplayPlayback && StatsEligible()) InstallStatsHook();

                if (pullPending)
                {
                    var panel = MatchStatsCore.ResultPanel();
                    if ((panel != null && panel.IsVisible) || Time.realtimeSinceStartup - job.DecidedAt >= 5f) PullStats(job);
                }
            }

            if (sendPending && job.Resolved)
            {
                job.StatsStarted = true;
                if (job.MatchId == null) job.StatsJson = null;   // ResolveUploadId said why
                else StartCoroutine(SendStatsRoutine(job));
            }
        }

        // Whether this match may want its stats uploaded: a forced test, or
        // ranked-shaped. Before the reporter's own roster check has run
        // (it waits for the economy stream), a lobby check that can't see
        // observers stands in; an extra hook in an unranked game only costs
        // that game a little Lua, and is never uploaded.
        private bool StatsEligible()
        {
            if (ForceUploadId() != null) return true;
            if (_snapshot != null) return _snapshot.Reportable;
            if (!LobbyManager.IsInLobby || !(LobbyManager.Backend is SteamLobbyBackend)) return false;
            var players = LobbyManager.CurrentState?.players;
            if (players == null) return false;
            var humans = 0;
            foreach (var p in players)
            {
                if (p == null) continue;
                if (p.type == PlayerType.AI) return false;
                if (p.type == PlayerType.Player) humans++;
            }
            return humans >= 2;
        }

        private void InstallStatsHook()
        {
            if (!RunLua(MatchStatsCore.InstallChunk)) return;
            _statsHooked = GetLuaGlobal("__SdbStatsHook") == "true";
            var err = GetLuaGlobal("__SdbStatsErr");
            if (!string.IsNullOrEmpty(err) && err != _statsLuaErr)
            {
                _statsLuaErr = err;
                Logger.LogWarning($"Ladder uploads: the stats collector reports: {err}");
            }
            if (_statsHooked) Logger.LogInfo("Ladder uploads: stats collector running in this match.");
        }

        private void PullStats(UploadJob job)
        {
            job.StatsPulled = true;
            // A global of our own: SanctuaryHud's MatchStats reads through
            // __SdbStatsOut, and the whole pull is a big string to keep.
            if (!RunLua("__SdbLadderStatsOut = __SdbStatsPull and __SdbStatsPull(0) or ''"))
            {
                Logger.LogWarning("Ladder uploads: couldn't read this match's stats.");
                return;
            }
            var raw = GetLuaGlobal("__SdbLadderStatsOut");
            RunLua("__SdbLadderStatsOut = nil");
            if (MatchData.SessionOf(raw) == null)
            {
                Logger.LogWarning("Ladder uploads: no stats collector ran in this match, so there are no stats to upload.");
                return;
            }
            var data = new MatchData();
            data.Apply(raw);
            if (data.Format < MatchStatsCore.Format)
            {
                Logger.LogInfo($"Ladder uploads: the collector in this match came from an older mod build (format {data.Format}); same figures.");
            }
            job.StatsJson = BuildStatsJson(job, data);
            if (job.StatsJson != null)
            {
                Logger.LogInfo($"Ladder uploads: stats read at tick {data.Tick} ({Encoding.UTF8.GetByteCount(job.StatsJson) / 1024} KB).");
            }
        }

        // ---- the payload ---------------------------------------------------

        private struct Seat
        {
            public string SteamId;
            public int ArmyId, Team;
            public string Name;
        }

        private string BuildStatsJson(UploadJob job, MatchData data)
        {
            var seats = new List<Seat>();
            if (job.Seats != null && job.Seats.Count > 0)
            {
                foreach (var p in job.Seats.OrderBy(p => p.ArmyId))
                {
                    seats.Add(new Seat { SteamId = p.SteamId.ToString(CultureInfo.InvariantCulture), ArmyId = p.ArmyId, Team = p.Team, Name = p.Name });
                }
            }
            else if (job.Forced && LocalSteamId != null && data.Armies.TryGetValue(data.Focus, out var mine))
            {
                // A forced test in a skirmish, which has no lobby roster:
                // this player is the army the game focuses.
                seats.Add(new Seat { SteamId = LocalSteamId, ArmyId = mine.Id, Team = mine.Team, Name = "" });
            }
            seats.RemoveAll(s => !data.Armies.ContainsKey(s.ArmyId));
            if (seats.Count == 0)
            {
                Logger.LogWarning("Ladder uploads: the stats have no army for any player in the roster; nothing to upload.");
                return null;
            }

            // The site takes 256 KB; a long game's timeline is thinned until
            // it fits (intervalS says how far).
            for (var interval = 5; interval <= 80; interval *= 2)
            {
                var json = StatsPayload(job, data, seats, interval).ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(json) <= StatsMaxBytes) return json;
            }
            Logger.LogWarning("Ladder uploads: the stats are too big to upload even thinned to one sample in 80 s.");
            return null;
        }

        private static JObject StatsPayload(UploadJob job, MatchData data, List<Seat> seats, int interval) => new JObject
        {
            ["format"] = StatsPayloadFormat,
            ["modVersion"] = ModVersion,
            ["buildId"] = job.BuildId,
            ["tickRate"] = data.TickRate,
            ["endTick"] = data.Tick,
            ["armies"] = new JArray(seats.Select(s => ArmyJson(s, data.Armies[s.ArmyId]))),
            ["timeline"] = Timeline(data, seats, interval),
        };

        private static JObject ArmyJson(Seat s, ArmyStats a) => new JObject
        {
            ["steamId"] = s.SteamId,
            ["armyId"] = a.Id,
            ["name"] = string.IsNullOrEmpty(s.Name) ? a.Name : s.Name,
            ["faction"] = a.Faction,
            ["team"] = s.Team,
            ["colour"] = "#" + ColorUtility.ToHtmlStringRGB(a.Colour).ToLowerInvariant(),
            ["condition"] = a.Condition,
            ["conditionTick"] = a.ConditionTick,
            ["alloy"] = new JObject
            {
                ["gathered"] = Num(a.AlloyGathered),
                ["spent"] = Num(a.AlloySpent),
                ["wasted"] = Num(a.AlloyWasted),
                ["stallTicks"] = a.AlloyStallTicks,
                ["peakIncome"] = Num(a.PeakAlloyIncome),
            },
            ["energy"] = new JObject
            {
                ["gathered"] = Num(a.EnergyGathered),
                ["spent"] = Num(a.EnergySpent),
                ["wasted"] = Num(a.EnergyWasted),
                ["stallTicks"] = a.EnergyStallTicks,
                ["peakIncome"] = Num(a.PeakEnergyIncome),
            },
            ["maxStorage"] = Num(a.MaxStorage),
            ["built"] = new JObject
            {
                ["land"] = a.BuiltLand,
                ["air"] = a.BuiltAir,
                ["naval"] = a.BuiltNaval,
                ["engineers"] = a.BuiltEngineers,
                ["structures"] = a.BuiltStructures,
                ["value"] = Num(a.BuiltValue),
            },
            ["lost"] = new JObject
            {
                ["mobile"] = a.LostMobile,
                ["structures"] = a.LostStructures,
                ["commander"] = a.LostCommander,
                ["value"] = Num(a.LostValue),
            },
            ["killedValue"] = Num(a.KilledValue),
            ["commanderKills"] = Num(a.CommanderKills),
            ["peakArmyValue"] = Num(a.PeakArmyValue),
            ["peakUnits"] = a.PeakUnits,
            ["score"] = Num(a.Score),
        };

        // The once-a-second samples in `interval`-second buckets, by sim
        // time: rates are the bucket's mean, levels its last sample (carried
        // over a bucket with none, e.g. after an army is out). Every series
        // is as long as t, which runs from the first bucket any player has a
        // sample in to the last.
        private static JObject Timeline(MatchData data, List<Seat> seats, int interval)
        {
            var perSecond = 1.0 / Math.Max(1, data.TickRate);
            int Bucket(Sample s) => (int)Math.Floor(s.Tick * perSecond / interval);
            var samples = seats.Select(s => data.Armies[s.ArmyId].Samples.OrderBy(x => x.Tick).ToList()).ToList();
            var first = int.MaxValue;
            var last = -1;
            foreach (var list in samples)
            {
                foreach (var s in list)
                {
                    var b = Math.Max(0, Bucket(s));
                    if (b < first) first = b;
                    if (b > last) last = b;
                }
            }
            var n = last < 0 ? 0 : last - first + 1;

            var t = new JArray();
            for (var i = 0; i < n; i++) t.Add((first + i) * interval);
            var series = new JObject();
            for (var k = 0; k < seats.Count; k++)
            {
                var sums = new double[n, 4];
                var counts = new int[n];
                var lastIn = new Sample?[n];
                foreach (var s in samples[k])
                {
                    var b = Math.Max(0, Bucket(s)) - first;
                    if (b < 0 || b >= n) continue;
                    sums[b, 0] += s.AlloyIncome;
                    sums[b, 1] += s.EnergyIncome;
                    sums[b, 2] += s.AlloySpend;
                    sums[b, 3] += s.EnergySpend;
                    counts[b]++;
                    lastIn[b] = s;
                }
                var alloyIncome = new JArray();
                var energyIncome = new JArray();
                var alloySpend = new JArray();
                var energySpend = new JArray();
                var armyValue = new JArray();
                var units = new JArray();
                var score = new JArray();
                Sample carry = default;
                for (var i = 0; i < n; i++)
                {
                    var c = Math.Max(1, counts[i]);
                    alloyIncome.Add(Num(sums[i, 0] / c));
                    energyIncome.Add(Num(sums[i, 1] / c));
                    alloySpend.Add(Num(sums[i, 2] / c));
                    energySpend.Add(Num(sums[i, 3] / c));
                    if (lastIn[i].HasValue) carry = lastIn[i].Value;
                    armyValue.Add(Num(carry.ArmyValue));
                    units.Add(carry.Units);
                    score.Add(Num(carry.Score));
                }
                series[seats[k].SteamId] = new JObject
                {
                    ["alloyIncome"] = alloyIncome,
                    ["energyIncome"] = energyIncome,
                    ["alloySpend"] = alloySpend,
                    ["energySpend"] = energySpend,
                    ["armyValue"] = armyValue,
                    ["units"] = units,
                    ["score"] = score,
                };
            }
            return new JObject { ["intervalS"] = interval, ["t"] = t, ["series"] = series };
        }

        /// A figure rounded to one decimal, written as an integer when it is
        /// one. Newtonsoft writes numbers with the invariant culture.
        private static JValue Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return new JValue(0L);
            var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            return Math.Abs(r) < 9e15 && r == Math.Floor(r) ? new JValue((long)r) : new JValue(r);
        }

        // ---- the match id and the job --------------------------------------

        /// At the victory: what this match will upload, captured while the
        /// roster, the recording's path and the matchmade id are at hand.
        private void BeginUploads(string forceId)
        {
            var stats = _cfgUpStats.Value;
            var replays = _cfgUpReplays.Value;
            if (!stats && !replays)
            {
                _job = null;
                if (forceId != null) Logger.LogInfo("Ladder uploads: ForceUploadMatchId is set, but Upload.Stats and Upload.Replays are both off.");
                return;
            }
            _job = new UploadJob
            {
                WantStats = stats,
                WantReplay = replays,
                Forced = forceId != null,
                DryRun = _cfgDryRun.Value,
                DecidedAt = Time.realtimeSinceStartup,
                Seats = _snapshot?.Humans?.ToList(),
                FallbackId = _mmReportMatchId,
                ReplayPath = replays ? RecordingPath() : null,
                BuildId = AppBuildId(),
            };
            if (forceId != null)
            {
                Logger.LogInfo($"Ladder uploads: ForceUploadMatchId set: uploading this game as match {forceId}.");
                ResolveUploadId(_job, forceId, true);
            }
        }

        /// Settles the match id once the report has answered: the site's
        /// matchId, else the matchmade one. A failed report uploads nothing.
        private void ResolveUploadId(UploadJob job, string responseId, bool reported)
        {
            if (job == null || job.Resolved) return;
            job.Resolved = true;
            if (!reported)
            {
                Logger.LogInfo("Ladder uploads: the result wasn't reported, so this match's stats and replay are not uploaded.");
                return;
            }
            job.MatchId = UploadId(responseId) ?? UploadId(job.FallbackId) ??
                          (job.DryRun ? "dryrun-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) : null);
            if (job.MatchId == null)
            {
                Logger.LogWarning("Ladder uploads: the ladder didn't say which match this was; nothing uploaded.");
                return;
            }
            if (job.WantReplay && !job.ReplayQueued)
            {
                job.ReplayQueued = true;
                QueueReplay(job);
            }
        }

        private static System.Reflection.FieldInfo _recordingPathField;
        private bool _recordingPathFailed;

        // The file the game records into (or last did: StopRecording leaves
        // the path). Read every frame while a replay uploads in a match.
        private string RecordingPath()
        {
            if (_recordingPathFailed) return null;
            try
            {
                _recordingPathField ??= HarmonyLib.AccessTools.Field(typeof(ReplayFile), "filePath") ??
                                        throw new MissingFieldException(nameof(ReplayFile), "filePath");
                return _recordingPathField.GetValue(null) as string;
            }
            catch (Exception e)
            {
                _recordingPathFailed = true;
                Logger.LogWarning($"Ladder uploads: can't find this match's replay file: {e.Message}");
                return null;
            }
        }

        private int AppBuildId()
        {
            try { return SteamManager.IsSteamInitialized ? SteamApps.GetAppBuildId() : 0; }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads: no Steam build id: {e.Message}");
                return 0;
            }
        }

        // ---- sending -------------------------------------------------------

        private sealed class ApiResult
        {
            public bool Ok;
            public int Status;
            public string Text, Error;

            public void Reset()
            {
                Ok = false;
                Status = 0;
                Text = Error = null;
            }

            public string Describe() =>
                Status > 0 ? $"{Status}: {Truncate(Text, 200)}" : Error ?? "no answer";
        }

        /// Waits up to `seconds` for the bearer session, signing in if needed.
        private IEnumerable WaitForSession(float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            var halfSecond = new WaitForSecondsRealtime(0.5f);
            while (_mmToken == null && Time.realtimeSinceStartup < until)
            {
                if (!SteamManager.IsSteamInitialized) yield break;
                EnsureSession();   // throttles itself
                yield return halfSecond;
            }
        }

        /// One POST to the site with the bearer token. A 401 means the
        /// session expired: sign in again and send it once more.
        private IEnumerable ApiPost(string path, string json, ApiResult result)
        {
            var reminted = false;
            while (true)
            {
                result.Reset();
                foreach (var step in WaitForSession(30f)) yield return step;
                if (_mmToken == null)
                {
                    result.Error = "no ladder session";
                    yield break;
                }
                var req = Post(path, json, _mmToken);
                yield return req.SendWebRequest();
                _inFlight.Remove(req);
                result.Status = (int)req.responseCode;
                result.Ok = req.result == UnityWebRequest.Result.Success;
                result.Text = req.downloadHandler?.text;
                result.Error = req.error;
                req.Dispose();
                if (result.Status != 401 || reminted) yield break;
                reminted = true;
                _mmToken = null;
                Logger.LogInfo("Ladder uploads: the ladder session expired; signing in again.");
            }
        }

        private IEnumerator SendStatsRoutine(UploadJob job)
        {
            if (job.DryRun)
            {
                WriteDryRun(job.MatchId + ".stats.json", JObject.Parse(job.StatsJson).ToString(Formatting.Indented));
                yield break;
            }
            var path = $"/api/mm/match/{job.MatchId}/stats";
            var result = new ApiResult();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                foreach (var step in ApiPost(path, job.StatsJson, result)) yield return step;
                if (result.Ok)
                {
                    Logger.LogInfo($"Ladder uploads: stats uploaded for match {job.MatchId}.");
                    job.StatsJson = null;
                    yield break;
                }
                // 4xx is the site's decision; retrying won't change it.
                if (result.Status >= 400 && result.Status < 500)
                {
                    Logger.LogWarning($"Ladder uploads: the ladder refused the stats ({result.Describe()}).");
                    yield break;
                }
                Logger.LogWarning($"Ladder uploads: stats not uploaded ({result.Describe()}), attempt {attempt}/3.");
                if (attempt < 3) yield return new WaitForSecondsRealtime(5f * attempt);
            }
            Logger.LogWarning("Ladder uploads: giving up on this match's stats.");
        }

        private void WriteDryRun(string name, string text)
        {
            try
            {
                var dir = Path.Combine(UploadDir, "dryrun");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text);
                Logger.LogInfo($"Ladder uploads (dry run): wrote {path} instead of sending it.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads (dry run): couldn't write {name}: {e.Message}");
            }
        }
    }
}
