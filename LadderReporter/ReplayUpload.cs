using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EM.Network;
using EM.Network.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SanctuaryHud
{
    // The replay half of the opt-in uploads (see StatsUpload.cs).
    //
    // The game writes a match's .sanreplay as it plays and closes it only
    // when the client is torn down (NetworkManager.DisposeClient ->
    // ReplayFile.StopRecording), i.e. when the player leaves the match. It
    // holds the file open for writing with FileShare.Read, so opening it
    // with FileShare.Read ourselves fails with a sharing violation until
    // then: that is the "left the match" signal, straight from the OS.
    //
    // The file is a length-prefixed header, then one record per network
    // frame (a type byte, an int32 length, the payload), flushed after each.
    // So it can be copied while the game still writes it (FileShare.
    // ReadWrite), cut after the last whole frame: 5 s after the victory the
    // recording is taken as it stands, and the replay ends at the result
    // rather than with the players idling on the result screen. If that
    // copy fails, or the player left first, the closed file is taken once
    // the game lets go of it, as before.
    //
    // Either way it is copied (with its .mods.json sidecar, if any) into
    // BepInEx\cache\LadderReporter\pending\<matchId>.sanreplay, out of reach
    // of the game's keep-the-newest-15 prune, and hashed on the way, on a
    // worker thread. A <matchId>.json manifest beside it survives a restart;
    // a match still waiting to close is written down too, so a crash or a
    // quit straight from the match still gets its replay sent next launch.
    //
    // Uploads run in the menu or a lobby, and in the decided match itself
    // (only that match's own replay, the game being over), and stop the
    // moment a game starts loading: slot request -> PUT straight to storage
    // (streamed from disk by UploadHandlerFile) -> done. Retries back off 1,
    // 5, 30 minutes, then wait for the next launch; an item is dropped once
    // uploaded, when the site says it already has the replay or refuses it,
    // or after 7 days.
    public partial class LadderReporterPlugin
    {
        private const string Waiting = "waiting";   // the game may still be writing it
        private const string Ready = "ready";       // copied and hashed, to upload
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(7);
        private const float EarlyCopyDelay = 5f;    // seconds after the victory

        private sealed class PendingReplay
        {
            public string matchId;
            public string state;
            public string sourcePath;        // the game's file
            public string fileName;          // its name, which the game's replay list needs
            public string mapPath;
            public string gameVersion;
            public int buildId;
            public long sizeBytes;
            public string sha256;
            public long sidecarBytes;
            public bool early;               // copied mid-match, at the result
            public DateTime createdUtc;
            public int attempts;
            public bool dryRun;
            // A slot the site handed out, reused until it expires; uploaded
            // once the PUT went through and only /done is left.
            public string uploadUrl;
            public string sidecarUrl;
            public DateTime? uploadExpiresUtc;
            public bool uploaded;

            [JsonIgnore] public float NextTry;
            [JsonIgnore] public int Failures;   // this session's, for the backoff
            [JsonIgnore] public bool Busy;
            // This session's victory, for the copy at the result; an item
            // from an earlier session has none, and its file is closed.
            [JsonIgnore] public float? DecidedAt;
        }

        private readonly List<PendingReplay> _pending = new List<PendingReplay>();
        private bool _replayUploading;
        private float _replayAccum;
        private string _replayOpenError;

        private static string PendingDir => Path.Combine(UploadDir, "pending");
        private static string CopyPath(PendingReplay p) => Path.Combine(PendingDir, p.matchId + ReplayFile.Extension);
        private static string SidecarCopyPath(PendingReplay p) => CopyPath(p) + ".mods.json";
        private static string ManifestPath(PendingReplay p) => Path.Combine(PendingDir, p.matchId + ".json");

        private long MaxReplayBytes => Math.Max(1, _cfgUpMaxMB.Value) * 1024L * 1024L;

        // ---- queueing ------------------------------------------------------

        private void QueueReplay(UploadJob job)
        {
            if (string.IsNullOrEmpty(job.ReplayPath))
            {
                Logger.LogWarning("Ladder uploads: the game didn't record this match, so there is no replay to upload.");
                SetLine(_status?.Replay, Tone.Bad, "Replay: the game didn't record this match");
                return;
            }
            if (_pending.Any(p => p.matchId == job.MatchId)) return;
            var item = new PendingReplay
            {
                matchId = job.MatchId,
                state = Waiting,
                sourcePath = job.ReplayPath,
                fileName = Path.GetFileName(job.ReplayPath),
                buildId = job.BuildId,
                createdUtc = DateTime.UtcNow,
                dryRun = job.DryRun,
                DecidedAt = job.DecidedAt,
            };
            _pending.Add(item);
            SaveManifest(item);
            Logger.LogInfo(FormattableString.Invariant($"Ladder uploads: replay {item.fileName} will be copied {EarlyCopyDelay:0} s after the result, or once you leave the match."));
            SetReplay(item.matchId, Tone.Busy, "Replay: copying the recording...");
        }

        /// Picks up what earlier sessions left: items waiting for their file
        /// to close (it has, by now) and copies still to upload.
        private void ResumePending()
        {
            try
            {
                if (!Directory.Exists(PendingDir)) return;
                foreach (var tmp in Directory.GetFiles(PendingDir, "*.tmp"))
                {
                    try { File.Delete(tmp); }
                    catch (Exception e) { Logger.LogWarning($"Ladder uploads: couldn't remove {Path.GetFileName(tmp)}: {e.Message}"); }
                }
                foreach (var path in Directory.GetFiles(PendingDir, "*.json"))
                {
                    if (path.EndsWith(".mods.json", StringComparison.OrdinalIgnoreCase)) continue;
                    PendingReplay item = null;
                    try { item = JsonConvert.DeserializeObject<PendingReplay>(File.ReadAllText(path)); }
                    catch (Exception e) { Logger.LogWarning($"Ladder uploads: {Path.GetFileName(path)} unreadable: {e.Message}"); }
                    if (item == null || UploadId(item.matchId) != item.matchId ||
                        !string.Equals(Path.GetFileNameWithoutExtension(path), item.matchId, StringComparison.Ordinal))
                    {
                        Logger.LogWarning($"Ladder uploads: dropping {Path.GetFileName(path)}, not a pending replay.");
                        TryDelete(path);
                        continue;
                    }
                    _pending.Add(item);
                    if (item.state == Ready && !File.Exists(CopyPath(item))) Drop(item, "its copy is gone");
                    else if (item.state != Ready && item.state != Waiting) Drop(item, "unknown state");
                }
                if (_pending.Count > 0) Logger.LogInfo($"Ladder uploads: {_pending.Count} replay(s) still to upload from earlier sessions.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads: couldn't read the pending replays: {e.Message}");
            }
        }

        private void ReplayStep(float dt)
        {
            if (_pending.Count == 0) return;
            _replayAccum += dt;
            if (_replayAccum < 2f) return;
            _replayAccum = 0f;

            foreach (var p in _pending.ToList())
            {
                if (p.Busy) continue;
                if (DateTime.UtcNow - p.createdUtc > PendingLifetime) Drop(p, "it is more than 7 days old");
                else if (p.state == Waiting) TryCollect(p);
            }

            if (_replayUploading) return;
            var now = Time.realtimeSinceStartup;
            var next = _pending.FirstOrDefault(p => p.state == Ready && !p.Busy && p.NextTry <= now && UploadStateOk(p));
            if (next != null) StartCoroutine(UploadReplayRoutine(next));
        }

        // Uploads while the player is in the menu or a lobby, and in a
        // decided match its own replay: never while a game loads, or plays
        // on undecided.
        private bool UploadStateOk(PendingReplay p)
        {
            var state = CurrentState();
            return state == "menu" || state == "lobby" || (state == "ingame" && InDecidedMatchOf(p));
        }

        // Still in the match this replay is of: the game records into its
        // file (a new match records into a new one). Every pending replay
        // is of a decided match; it is queued at the result.
        private bool InDecidedMatchOf(PendingReplay p) =>
            p.sourcePath != null && string.Equals(RecordingPath(), p.sourcePath, StringComparison.OrdinalIgnoreCase);

        // ---- collecting the file -------------------------------------------

        private void TryCollect(PendingReplay p)
        {
            var source = p.sourcePath;
            if (!File.Exists(source))
            {
                // Saved from the replay list since: it moved to Replays\Saved.
                var saved = Path.Combine(ReplayFile.SavedReplayDirectory, p.fileName ?? "");
                if (!string.IsNullOrEmpty(p.fileName) && File.Exists(saved)) source = saved;
                else
                {
                    Drop(p, "the game's replay file is gone");
                    return;
                }
            }
            long size;
            var early = false;
            try
            {
                // Refused (a sharing violation) while the game still has it
                // open for writing, i.e. until the player leaves the match.
                using (var probe = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    size = probe.Length;
                }
            }
            catch (IOException)
            {
                // Still recording. Once, 5 s after this session's victory
                // and still in that match: take it as it stands.
                if (p.DecidedAt == null || Time.realtimeSinceStartup - p.DecidedAt.Value < EarlyCopyDelay ||
                    NetworkManager.IsReplayPlayback || !InDecidedMatchOf(p)) return;
                p.DecidedAt = null;
                try { size = new FileInfo(source).Length; }
                catch (Exception) { return; }
                early = true;
            }
            catch (Exception e)
            {
                if (_replayOpenError != e.Message)
                {
                    _replayOpenError = e.Message;
                    Logger.LogWarning($"Ladder uploads: can't open {p.fileName}: {e.Message}");
                }
                return;
            }
            if (size > MaxReplayBytes)
            {
                Drop(p, FormattableString.Invariant($"it is {size / (1024.0 * 1024.0):0.0} MB, over Upload.MaxReplayMB ({_cfgUpMaxMB.Value})"));
                return;
            }

            p.Busy = true;
            var dest = CopyPath(p);
            var sidecar = source + ".mods.json";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CopyAndHash(source, sidecar, dest, out var bytes, out var frames, out var cut, out var sha, out var sidecarBytes);
                    _uploadMainThread.Enqueue(() => Collected(p, early, bytes, frames, cut, sha, sidecarBytes, null));
                }
                catch (Exception e)
                {
                    _uploadMainThread.Enqueue(() => Collected(p, early, 0, 0, 0, null, 0, e.Message));
                }
            });
        }

        // Worker thread: copies the replay up to its last whole frame
        // (hashing it on the way) and its sidecar into the pending folder.
        // `cut` is what was left off: a frame the game was still writing,
        // or the torn end of a recording a crash cut short.
        private static void CopyAndHash(string source, string sidecar, string dest, out long bytes, out int frames,
            out long cut, out string sha256, out long sidecarBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var tmp = dest + ".tmp";
            bytes = 0;
            using (var sha = SHA256.Create())
            {
                // ReadWrite: the game may still have it open for writing.
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920))
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    var length = WholeFrames(input, out frames);
                    cut = input.Length - length;
                    input.Position = 0;
                    var buffer = new byte[81920];
                    while (bytes < length)
                    {
                        var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - bytes));
                        if (n <= 0) throw new IOException("the replay got shorter while it was being copied");
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        output.Write(buffer, 0, n);
                        bytes += n;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                }
                var hex = new StringBuilder(64);
                foreach (var b in sha.Hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                sha256 = hex.ToString();
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);

            var sidecarDest = dest + ".mods.json";
            sidecarBytes = 0;
            if (File.Exists(sidecar))
            {
                File.Copy(sidecar, sidecarDest, true);
                sidecarBytes = new FileInfo(sidecarDest).Length;
            }
            else if (File.Exists(sidecarDest))
            {
                File.Delete(sidecarDest);
            }
        }

        /// The length of a replay's header plus every whole frame after it
        /// (ReplayFile.WriteHeader / RecordFrame): what the game's own
        /// player reads before it stops. Throws when not even the header is
        /// whole.
        private static long WholeFrames(Stream s, out int frames)
        {
            frames = 0;
            var end = s.Length;
            var head = new byte[5];
            s.Position = 0;
            if (ReadFully(s, head, 4) != 4) throw new IOException("the replay has no header");
            long pos = 4 + BitConverter.ToInt32(head, 0);
            if (pos <= 4 || pos > end) throw new IOException("the replay's header is cut short");
            while (pos + 5 <= end)
            {
                s.Position = pos;
                if (ReadFully(s, head, 5) != 5) break;
                var length = BitConverter.ToInt32(head, 1);
                if (length < 0 || pos + 5 + length > end) break;
                pos += 5 + length;
                frames++;
            }
            return pos;
        }

        private static int ReadFully(Stream s, byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            return read;
        }

        // Main thread, back from the copy: read the header and queue it.
        private void Collected(PendingReplay p, bool early, long bytes, int frames, long cut, string sha256, long sidecarBytes, string error)
        {
            p.Busy = false;
            if (!_pending.Contains(p)) return;
            if (error != null)
            {
                p.Failures++;
                Logger.LogWarning($"Ladder uploads: couldn't copy {p.fileName}" + (early ? " at the result" : "") + $": {error}" +
                                  (early ? "; it is copied once you leave the match instead." : ""));
                if (early) SetReplay(p.matchId, Tone.Busy, "Replay: copied once you leave the match");
                if (p.Failures >= 3) Drop(p, "it couldn't be copied");
                return;
            }
            if (!TryReadHeader(CopyPath(p), out var gameVersion, out var mapPath))
            {
                Drop(p, "its header is unreadable");
                return;
            }
            p.state = Ready;
            p.sizeBytes = bytes;
            p.sha256 = sha256;
            p.sidecarBytes = sidecarBytes;
            p.gameVersion = gameVersion;
            p.mapPath = mapPath;
            p.early = early;
            p.Failures = 0;
            p.NextTry = 0f;
            SaveManifest(p);
            // Frames are the game's network frames, 10 a second of game time.
            Logger.LogInfo($"Ladder uploads: replay {p.fileName} copied " + (early ? "at the result" : "after the match") +
                           $": {bytes / 1024} KB, {frames} frames" + (cut > 0 ? $", {cut} byte(s) of an unfinished frame left off" : "") + ".");
            SetReplay(p.matchId, Tone.Busy, UploadStateOk(p) ? "Replay: uploading..." : "Replay: uploads once you're in the menu or a lobby");
        }

        // The game's own reader (main thread: it allocates Allocator.Temp
        // native memory), after a sanity check of the length prefix. Sharing
        // writes: a live stream reads the header while the game records.
        private bool TryReadHeader(string path, out string gameVersion, out string mapPath)
        {
            gameVersion = mapPath = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var prefix = new byte[4];
                    if (fs.Read(prefix, 0, 4) != 4) return false;
                    var length = BitConverter.ToInt32(prefix, 0);
                    if (length <= 0 || length > 64 * 1024) return false;
                    fs.Position = 0;
                    if (!ReplayFile.TryReadHeader(fs, out var header)) return false;
                    gameVersion = header.gameVersion;
                    mapPath = header.mapPath;
                    return !string.IsNullOrEmpty(gameVersion) && !string.IsNullOrEmpty(mapPath);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads: couldn't read the replay header: {e.Message}");
                return false;
            }
        }

        // ---- uploading -----------------------------------------------------

        private JObject SlotRequest(PendingReplay p) => new JObject
        {
            ["sizeBytes"] = p.sizeBytes,
            ["sha256"] = p.sha256,
            ["gameVersion"] = p.gameVersion,
            ["buildId"] = p.buildId,
            ["mapPath"] = p.mapPath,
            ["fileName"] = p.fileName,
            ["sidecarBytes"] = p.sidecarBytes,
        };

        private IEnumerator UploadReplayRoutine(PendingReplay p)
        {
            _replayUploading = true;
            p.Busy = true;
            try
            {
                foreach (var step in UploadReplay(p)) yield return step;
            }
            finally
            {
                _replayUploading = false;
                p.Busy = false;
            }
        }

        private IEnumerable UploadReplay(PendingReplay p)
        {
            if (p.dryRun)
            {
                WriteDryRun(p.matchId + ".replay.json", SlotRequest(p).ToString(Formatting.Indented));
                SetReplay(p.matchId, Tone.Quiet, "Replay: written to a file, not sent (dry run)", true);
                Drop(p, null);
                yield break;
            }
            if (_cfgDryRun.Value)
            {
                // A real game's replay, queued before DryRun went on: hold it.
                p.NextTry = Time.realtimeSinceStartup + 60f;
                yield break;
            }
            if (!File.Exists(CopyPath(p)))
            {
                Drop(p, "its copy is gone");
                yield break;
            }
            p.attempts++;
            SaveManifest(p);
            var result = new ApiResult();
            var basePath = $"/api/mm/match/{p.matchId}/replay";

            // 1. A slot, unless one handed out earlier is still good.
            if (p.uploadUrl == null || p.uploadExpiresUtc == null || p.uploadExpiresUtc.Value < DateTime.UtcNow.AddMinutes(5))
            {
                ClearSlot(p);
                foreach (var step in ApiPost(basePath, SlotRequest(p).ToString(Formatting.None), result)) yield return step;
                if (!result.Ok)
                {
                    ApiFailure(p, result, "slot request");
                    yield break;
                }
                JObject reply = null;
                try { reply = JObject.Parse(result.Text ?? ""); }
                catch (Exception e) { Logger.LogWarning($"Ladder uploads: slot reply unreadable: {e.Message}"); }
                if (reply?["skip"] != null && reply["skip"].Type != JTokenType.Null)
                {
                    Drop(p, $"the ladder already has it ({reply["skip"]})", told: true);
                    SetReplay(p.matchId, Tone.Good, "Replay uploaded (your opponent's copy got there first)", true);
                    yield break;
                }
                var retryAfter = reply?["retryAfterS"];
                if (retryAfter != null && (retryAfter.Type == JTokenType.Integer || retryAfter.Type == JTokenType.Float))
                {
                    // Someone else's upload is in progress: not a failure.
                    var seconds = Mathf.Clamp((float)retryAfter, 30f, 24f * 3600f);
                    p.NextTry = Time.realtimeSinceStartup + seconds;
                    Logger.LogInfo($"Ladder uploads: another upload of match {p.matchId} is in progress; trying again in {Mathf.CeilToInt(seconds / 60f)} min.");
                    SetReplay(p.matchId, Tone.Busy, "Replay: your opponent is uploading it; checking again in " + Mathf.CeilToInt(seconds / 60f).ToString(CultureInfo.InvariantCulture) + " min");
                    yield break;
                }
                var upload = reply?["upload"] as JObject;
                var url = TokenString(upload?["url"]);
                if (!IsHttpUrl(url))
                {
                    Fail(p, "the slot reply has no upload URL");
                    yield break;
                }
                p.uploadUrl = url;
                var sidecarUrl = TokenString(upload["sidecarUrl"]);
                p.sidecarUrl = IsHttpUrl(sidecarUrl) ? sidecarUrl : null;
                p.uploadExpiresUtc = reply["expiresAt"] != null &&
                                     DateTime.TryParse(TokenString(reply["expiresAt"]), CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expires)
                    ? expires
                    : DateTime.UtcNow.AddHours(1);
                p.uploaded = false;
                SaveManifest(p);
            }

            // 2. The bytes, straight to storage.
            if (!p.uploaded)
            {
                Logger.LogInfo($"Ladder uploads: uploading replay {p.fileName} ({p.sizeBytes / 1024} KB)...");
                SetReplay(p.matchId, Tone.Busy, "Replay: uploading...");
                var put = new PutResult();
                foreach (var step in PutFile(p, p.uploadUrl, CopyPath(p), put)) yield return step;
                if (!put.Paused && put.Ok && p.sidecarUrl != null && p.sidecarBytes > 0 && File.Exists(SidecarCopyPath(p)))
                {
                    foreach (var step in PutFile(p, p.sidecarUrl, SidecarCopyPath(p), put)) yield return step;
                }
                if (put.Paused)
                {
                    // A game is loading: drop the transfer, try again once
                    // the player is back in the menu. Not a failure.
                    p.attempts--;
                    SaveManifest(p);
                    p.NextTry = Time.realtimeSinceStartup + 30f;
                    Logger.LogInfo("Ladder uploads: a game is loading; the replay upload stops and resumes in the menu.");
                    SetReplay(p.matchId, Tone.Busy, "Replay: the upload resumes in the menu");
                    yield break;
                }
                if (!put.Ok)
                {
                    ClearSlot(p);
                    Fail(p, $"the upload failed ({put.Describe()})");
                    yield break;
                }
                p.uploaded = true;
                SaveManifest(p);
            }

            // 3. Tell the site it's there.
            foreach (var step in ApiPost(basePath + "/done", "{}", result)) yield return step;
            if (result.Ok)
            {
                Logger.LogInfo($"Ladder uploads: replay uploaded for match {p.matchId}.");
                SetReplay(p.matchId, Tone.Good, "Replay uploaded", true);
                Drop(p, null);
                yield break;
            }
            if (result.Status == 409)
            {
                // The stored object didn't match and the site released the
                // slot: start again from a new one.
                ClearSlot(p);
                Fail(p, $"the ladder didn't accept the stored file ({result.Describe()})");
                yield break;
            }
            ApiFailure(p, result, "completion");
        }

        // A non-success from one of the site's endpoints: the site's own
        // refusals (4xx) drop the item; anything else is retried later. A
        // 401 has already been retried after signing in again. 408 and 429
        // are "later", not "no".
        private void ApiFailure(PendingReplay p, ApiResult result, string what)
        {
            var s = result.Status;
            if (s >= 400 && s < 500 && s != 401 && s != 408 && s != 429)
            {
                Drop(p, $"the ladder refused the {what} ({result.Describe()})");
                return;
            }
            Fail(p, $"{what} failed ({result.Describe()})");
        }

        private void Fail(PendingReplay p, string why)
        {
            p.Failures++;
            float wait;
            switch (p.Failures)
            {
                case 1: wait = 60f; break;
                case 2: wait = 300f; break;
                case 3: wait = 1800f; break;
                default: wait = float.PositiveInfinity; break;   // the next game launch
            }
            p.NextTry = Time.realtimeSinceStartup + wait;
            SaveManifest(p);
            Logger.LogWarning($"Ladder uploads: replay for match {p.matchId}: {why}; " +
                              (float.IsPositiveInfinity(wait) ? "trying again next time the game starts." : $"trying again in {Mathf.RoundToInt(wait / 60f)} min."));
            SetReplay(p.matchId, Tone.Bad, "Replay upload failed: trying again " +
                (float.IsPositiveInfinity(wait) ? "next time the game starts" : "in " + Mathf.RoundToInt(wait / 60f).ToString(CultureInfo.InvariantCulture) + " min"), true);
        }

        private static void ClearSlot(PendingReplay p)
        {
            p.uploadUrl = null;
            p.sidecarUrl = null;
            p.uploadExpiresUtc = null;
            p.uploaded = false;
        }

        private sealed class PutResult
        {
            public bool Ok, Paused;
            public int Status;
            public string Error;

            public string Describe() => Status > 0 ? Status.ToString(CultureInfo.InvariantCulture) : Error ?? "no answer";
        }

        // PUTs a file to a pre-signed storage URL, streamed from disk. No
        // auth header: the signature is in the URL. Aborted (Paused) the
        // moment the game leaves the menu or lobby.
        private IEnumerable PutFile(PendingReplay p, string url, string path, PutResult result)
        {
            result.Ok = result.Paused = false;
            result.Status = 0;
            result.Error = null;
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT)
            {
                uploadHandler = new UploadHandlerFile(path) { contentType = "application/octet-stream" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 1800,
            };
            _inFlight.Add(req);
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (!UploadStateOk(p))
                {
                    req.Abort();
                    result.Paused = true;
                    break;
                }
                if (path == CopyPath(p))
                {
                    var percent = Mathf.FloorToInt(Mathf.Clamp01(req.uploadProgress) * 100f);
                    SetReplay(p.matchId, Tone.Busy, "Replay: uploading " + percent.ToString(CultureInfo.InvariantCulture) + "%");
                }
                yield return null;
            }
            _inFlight.Remove(req);
            if (!result.Paused)
            {
                result.Status = (int)req.responseCode;
                result.Ok = req.result == UnityWebRequest.Result.Success;
                result.Error = req.error;
            }
            req.Dispose();
        }

        private static string TokenString(JToken t) =>
            t == null || t.Type == JTokenType.Null ? null : t.Type == JTokenType.Date ? ((DateTime)t).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : t.ToString();

        private static bool IsHttpUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

        // ---- bookkeeping ---------------------------------------------------

        private void SaveManifest(PendingReplay p)
        {
            try
            {
                Directory.CreateDirectory(PendingDir);
                File.WriteAllText(ManifestPath(p), JsonConvert.SerializeObject(p, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads: couldn't save the pending replay list: {e.Message}");
            }
        }

        /// Forgets an item and deletes its copy. `why` is logged; null for
        /// a finished one.
        private void Drop(PendingReplay p, string why, bool told = false)
        {
            _pending.Remove(p);
            if (why != null) Logger.LogInfo($"Ladder uploads: not uploading the replay of match {p.matchId}: {why}.");
            if (why != null && !told) SetReplay(p.matchId, Tone.Bad, $"Replay not uploaded: {why}", true);
            TryDelete(CopyPath(p));
            TryDelete(SidecarCopyPath(p));
            TryDelete(ManifestPath(p));
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Ladder uploads: couldn't delete {Path.GetFileName(path)}: {e.Message}");
            }
        }
    }
}
