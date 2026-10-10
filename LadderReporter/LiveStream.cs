using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using EM.Network.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Live replays, the streaming half (Live.StreamLadder and
    // Live.StreamOther, both off by default). While
    // the player is in a game, the game's own recording is sent to the site
    // as it grows, so anyone can watch it in game a delay behind (the
    // watching half is LiveWatch.cs; the site's is docs/live-replays.md).
    //
    // The recording is whole network frames, flushed one by one (see
    // ReplayUpload.cs), and readable while the game writes it. Every 15 s
    // the frames written since the last chunk are read on a worker thread,
    // cut after the last whole one, and posted as the next chunk; the first
    // chunk carries the header. Leaving the game sends what is left and
    // closes the stream. The site holds every chunk back by its delay
    // before any viewer can have it, so the delay doesn't depend on this
    // side behaving.
    //
    // A chunk is a few tens of KB and the requests are asynchronous, so this
    // costs the game nothing noticeable. A ladder game is one this mod
    // launched for a ladder match (LaunchedThisGame, the same test as
    // reporting); every other game, custom, skirmish or observed, is
    // "other", with a setting of its own.
    public partial class LadderReporterPlugin
    {
        private const float LiveChunkSeconds = 15f;
        // The site's cap. A game's opening frame is its whole starting state,
        // 1.7 MB on The Forge, so the first chunk can be that big.
        private const int LiveChunkMaxBytes = 4 * 1024 * 1024;

        private ConfigEntry<bool> _cfgLiveLadder;
        private ConfigEntry<bool> _cfgLiveOther;

        private sealed class LiveOut
        {
            public string SourcePath;       // the game's recording
            public string Id;               // the site's stream id, once opened
            public string Url;
            public int Seq;                 // the next chunk to send
            public readonly List<long> ChunkStarts = new List<long> { 0 };   // byte offset of chunk n
            public bool Busy;
            public bool Leaving;            // the player left: last chunk, then /end
            public bool Over;               // nothing more to do
            public float NextTry;
            public int Failures;
            public long Offset => ChunkStarts[Seq];
        }

        private LiveOut _liveOut;
        private string _liveDonePath;      // a recording already streamed (or given up on)
        private float _liveAccum;

        internal string LiveStreamUrl => _liveOut != null && !_liveOut.Over ? _liveOut.Url : null;

        private void AwakeLive()
        {
            _cfgLiveLadder = Config.Bind("Live", "StreamLadder", false,
                "Stream your ladder games live to sanctuarydb.net/live while they run, so anyone can watch them in " +
                "game, three minutes behind. Each game is listed on that page while it runs and for a day after.");
            _cfgLiveOther = Config.Bind("Live", "StreamOther", false,
                "Stream every other game you play or observe (custom lobbies, skirmishes) live to " +
                "sanctuarydb.net/live in the same way.");
        }

        private void UpdateLive()
        {
            WatchStep();
            _liveAccum += Time.unscaledDeltaTime;
            if (_liveAccum < 1f) return;
            _liveAccum = 0f;

            var o = _liveOut;
            if (o == null)
            {
                if (_cfgLiveLadder.Value || _cfgLiveOther.Value) TryOpenLive();
                return;
            }
            if (o.Over)
            {
                _liveOut = null;
                return;
            }
            if (o.Busy) return;
            // The game closes the recording when the player leaves the match.
            if (!o.Leaving && (!InMatch || NetworkManager.IsReplayPlayback || !SameFile(RecordingPath(), o.SourcePath)))
            {
                o.Leaving = true;
                o.NextTry = 0f;
            }
            if (Time.realtimeSinceStartup < o.NextTry) return;
            if (o.Id == null) StartCoroutine(OpenLiveRoutine(o));
            else ReadNextChunk(o);
        }

        private static bool SameFile(string a, string b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ---- opening -------------------------------------------------------

        private void TryOpenLive()
        {
            if (!InMatch || NetworkManager.IsReplayPlayback || !LuaReady) return;
            // Checked every second, so switching a setting on mid-game
            // streams the game from its start.
            var ladder = LaunchedThisGame;
            if (!(ladder ? _cfgLiveLadder.Value : _cfgLiveOther.Value)) return;
            var path = RecordingPath();
            if (string.IsNullOrEmpty(path) || SameFile(path, _liveDonePath) || !File.Exists(path)) return;
            _liveDonePath = path;
            _liveOut = new LiveOut { SourcePath = path };
            Logger.LogInfo($"Live: streaming this {(ladder ? "ladder game" : "game")} ({Path.GetFileName(path)}) to the site.");
        }

        private IEnumerator OpenLiveRoutine(LiveOut o)
        {
            o.Busy = true;
            try
            {
                if (!TryReadHeader(o.SourcePath, out var gameVersion, out var mapPath))
                {
                    GiveUpLive(o, "the recording's header isn't readable");
                    yield break;
                }
                var body = new JObject
                {
                    ["mapPath"] = mapPath,
                    ["gameVersion"] = gameVersion,
                    ["buildId"] = AppBuildId(),
                    ["fileName"] = Path.GetFileName(o.SourcePath),
                    ["players"] = LivePlayers(),
                    ["sidecar"] = ReadSidecar(o.SourcePath),
                };
                var result = new ApiResult();
                foreach (var step in ApiPost("/api/mm/live", body.ToString(Formatting.None), result)) yield return step;
                if (!result.Ok)
                {
                    LiveFailure(o, "opening the stream", result);
                    yield break;
                }
                JObject reply = null;
                try { reply = JObject.Parse(result.Text ?? ""); }
                catch (Exception e) { Logger.LogWarning($"Live: the site's answer is unreadable: {e.Message}"); }
                var id = TokenString(reply?["id"]);
                if (UploadId(id) == null)
                {
                    GiveUpLive(o, "the site sent no stream id");
                    yield break;
                }
                o.Id = id;
                o.Url = TokenString(reply["url"]);
                o.Failures = 0;
                o.NextTry = 0f;
                var delay = reply["delayS"]?.Type == JTokenType.Integer ? (int)reply["delayS"] : 180;
                Logger.LogInfo($"Live: this game is live at {o.Url} (viewers are {delay} s behind).");
            }
            finally { o.Busy = false; }
        }

        // Who is in the game, from the lobby. A solo skirmish has none: the
        // page shows the map alone.
        private static JArray LivePlayers()
        {
            var list = new JArray();
            if (!LobbyManager.IsInLobby) return list;
            var players = LobbyManager.CurrentState?.players;
            if (players == null) return list;
            foreach (var p in players.Where(p => p != null).Take(16))
            {
                var kind = p.type == PlayerType.AI ? "ai" : p.type == PlayerType.Observer || p.armyID < 1 ? "observer" : "player";
                var name = string.IsNullOrWhiteSpace(p.name) ? (kind == "ai" ? "AI" : "Player") : p.name;
                list.Add(new JObject { ["name"] = name, ["team"] = kind == "observer" ? -1 : p.team, ["kind"] = kind });
            }
            return list;
        }

        // A modded game's .mods.json, which a viewer needs to play it with the
        // same mods. Notes are left out: they are for after the match.
        private JObject ReadSidecar(string recording)
        {
            var path = recording + ".mods.json";
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 200 * 1024) return null;
                var o = JObject.Parse(File.ReadAllText(path));
                o.Remove("notes");
                return o;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Live: the game's mod list is unreadable, streaming without it: {e.Message}");
                return null;
            }
        }

        // ---- chunks --------------------------------------------------------

        // A chunk's bounds are fixed the first time it is read: a retry (or a
        // resend the site asks for) re-reads exactly those bytes, so the site
        // never holds two different versions of one chunk.
        private void ReadNextChunk(LiveOut o)
        {
            o.Busy = true;
            var seq = o.Seq;
            var from = o.Offset;
            var exactEnd = seq + 1 < o.ChunkStarts.Count ? o.ChunkStarts[seq + 1] : -1L;
            var path = o.SourcePath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] chunk = null;
                string error = null;
                try { chunk = ReadFrames(path, from, seq == 0, exactEnd); }
                catch (Exception e) { error = e.Message; }
                _uploadMainThread.Enqueue(() =>
                {
                    if (error != null)
                    {
                        o.Busy = false;
                        GiveUpLive(o, $"the recording can't be read ({error})");
                    }
                    else if (chunk == null || chunk.Length == 0)
                    {
                        o.Busy = false;
                        if (o.Leaving) StartCoroutine(EndLiveRoutine(o));
                        else o.NextTry = Time.realtimeSinceStartup + LiveChunkSeconds;
                    }
                    else
                    {
                        if (exactEnd < 0) o.ChunkStarts.Add(from + chunk.Length);
                        StartCoroutine(SendChunkRoutine(o, chunk));
                    }
                });
            });
        }

        // Worker thread: the whole frames from `from` on (the header too, for
        // the first chunk), up to the site's cap, or exactly up to `exactEnd`
        // when the chunk was read before. Null when there are none yet.
        // ReadWrite: the game still has the file open for writing.
        private static byte[] ReadFrames(string path, long from, bool first, long exactEnd)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (exactEnd >= 0)
                {
                    var again = new byte[exactEnd - from];
                    fs.Position = from;
                    if (ReadFully(fs, again, again.Length) != again.Length) throw new IOException("the recording got shorter");
                    return again;
                }
                var end = fs.Length;
                var head = new byte[5];
                var pos = from;
                if (first)
                {
                    fs.Position = 0;
                    if (ReadFully(fs, head, 4) != 4) return null;
                    pos = 4 + BitConverter.ToInt32(head, 0);
                    if (pos <= 4 || pos > end) return null;
                }
                while (pos + 5 <= end)
                {
                    fs.Position = pos;
                    if (ReadFully(fs, head, 5) != 5) break;
                    var length = BitConverter.ToInt32(head, 1);
                    if (head[0] != 100 || length < 0) throw new IOException($"not a replay frame at byte {pos}");
                    var next = pos + 5 + length;
                    if (next > end || (next - from > LiveChunkMaxBytes && pos > from)) break;
                    pos = next;
                }
                var size = pos - from;
                if (size <= 0 || (!first && size < 5)) return null;
                if (size > LiveChunkMaxBytes) throw new IOException("a single frame is bigger than a chunk may be");
                var bytes = new byte[size];
                fs.Position = from;
                if (ReadFully(fs, bytes, bytes.Length) != bytes.Length) return null;
                return bytes;
            }
        }

        private IEnumerator SendChunkRoutine(LiveOut o, byte[] chunk)
        {
            o.Busy = true;
            try
            {
                var seq = o.Seq;
                var result = new ApiResult();
                foreach (var step in ApiPost($"/api/mm/live/{o.Id}/chunk/{seq.ToString(CultureInfo.InvariantCulture)}",
                             chunk, "application/octet-stream", result)) yield return step;
                var next = NextFrom(result.Text);
                if (result.Ok || (result.Status == 409 && next != null))
                {
                    if (result.Ok) o.Seq = seq + 1;
                    // The site has a different count (a lost answer): carry on
                    // from the chunk it expects, if this side knows where it starts.
                    if (next != null && next.Value != o.Seq)
                    {
                        if (next.Value < o.ChunkStarts.Count) o.Seq = next.Value;
                        else
                        {
                            GiveUpLive(o, $"the site expects chunk {next.Value}, this side has sent {o.Seq}");
                            yield break;
                        }
                    }
                    o.Failures = 0;
                    // Leaving: straight on to whatever is left, then /end.
                    o.NextTry = o.Leaving ? 0f : Time.realtimeSinceStartup + LiveChunkSeconds;
                    yield break;
                }
                if (result.Status == 410)
                {
                    Logger.LogInfo("Live: the site has closed this stream (it went quiet for too long); not streaming the rest.");
                    o.Over = true;
                    yield break;
                }
                LiveFailure(o, $"chunk {seq}", result);
            }
            finally { o.Busy = false; }
        }

        private static int? NextFrom(string text)
        {
            try
            {
                var n = JObject.Parse(text ?? "")["next"];
                return n != null && n.Type == JTokenType.Integer ? (int?)(int)n : null;
            }
            catch (Exception) { return null; }
        }

        private IEnumerator EndLiveRoutine(LiveOut o)
        {
            o.Busy = true;
            try
            {
                var result = new ApiResult();
                foreach (var step in ApiPost($"/api/mm/live/{o.Id}/end", "{}", result)) yield return step;
                if (!result.Ok) Logger.LogWarning($"Live: couldn't close the stream ({result.Describe()}); the site closes it once it goes quiet.");
                else Logger.LogInfo($"Live: stream closed after {o.Seq} chunk(s), {o.Offset / 1024} KB.");
                o.Over = true;
            }
            finally { o.Busy = false; }
        }

        // The site's own refusals end the stream; anything else is tried
        // again on the next beat, the stream carrying on from the same chunk.
        // A long outage ends it from the site's side (410).
        private void LiveFailure(LiveOut o, string what, ApiResult result)
        {
            var s = result.Status;
            if (s >= 400 && s < 500 && s != 401 && s != 408 && s != 429)
            {
                GiveUpLive(o, $"the site refused {what} ({result.Describe()})");
                return;
            }
            o.Failures++;
            o.NextTry = Time.realtimeSinceStartup + Mathf.Min(60f, LiveChunkSeconds * o.Failures);
            if (o.Failures == 1 || o.Failures % 10 == 0)
                Logger.LogWarning($"Live: {what} failed ({result.Describe()}); trying again.");
            if (o.Leaving && o.Failures >= 5) GiveUpLive(o, "the site isn't answering");
        }

        private void GiveUpLive(LiveOut o, string why)
        {
            Logger.LogWarning($"Live: stopped streaming this game: {why}.");
            o.Over = true;
        }
    }
}
