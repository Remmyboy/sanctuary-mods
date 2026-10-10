using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using EM.DOTS.Engine.Loader;
using EM.Engine;
using EM.Lua;
using EM.Network;
using EM.Network.Replay;
using EM.UI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Live replays, the watching half (the streaming half is LiveStream.cs).
    // The live page's "Watch in game" reaches the mod over the local bridge
    // (POST /watch); from there the mod polls the site for the stream's
    // chunks, which the site only hands out once they are older than its
    // delay, appends them to a file under Replays\Live, and plays that file
    // with the game's own replay player while it grows.
    //
    // The game's player (ReplayClientSockets) is made for finished files: it
    // opens them sharing reads only, and the first read that comes up short
    // ends the replay. For the live file only, two patches change that:
    //
    //   TryOpen   opens the file sharing writes too, so chunks can still be
    //             appended while it plays.
    //   Receive   feeds frames the same way, paced by the sim speed, but only
    //             frames already written whole; at the end of what has come
    //             so far it waits instead of ending. Once the site says the
    //             stream is over and the last chunk is in, the game's own
    //             Receive runs again and ends the replay as usual.
    //
    // A viewer starts at the beginning of the game (the game has no way to
    // join a stream part-way) and can catch up with ReplayManager's speed
    // controls. Everything else, pause and speed included, is the game's
    // replay player and ReplayManager, unchanged.
    public partial class LadderReporterPlugin
    {
        private const float WatchPollSeconds = 10f;

        private sealed class LiveIn
        {
            public string Id;
            public string Path;
            public int NextSeq;
            public bool Complete;           // the site said nothing more will come
            public bool Busy;               // a poll (and its downloads) is running
            public float NextPoll;
            public bool Started, SawPlayback;
            public bool WaitingForMenu;     // closing a replay first
            public InterfaceManager OldUi;
            public float QuitAt;
            public FileStream Writer;
            // For the bridge: waiting (for the delay), loading, playing, over
            // (all of it is in), failed (with Error, a sentence for the page).
            public string Phase = "loading";
            public string Error;
        }

        private LiveIn _liveIn;

        // Read by the socket patches; one live file at a time.
        private static string _livePath;
        private static long _liveCommitted;
        private static volatile bool _liveComplete;

        // ---- the request ---------------------------------------------------

        private static string LiveDir => Path.Combine(ReplayFile.ReplayDirectory, "Live");

        /// Main thread, from the bridge: watch this stream, replacing whatever
        /// was being watched.
        private void StartWatch(string id)
        {
            StopWatch(null);
            try
            {
                // Earlier streams aren't kept: the site has them for a day.
                Directory.CreateDirectory(LiveDir);
                foreach (var f in Directory.GetFiles(LiveDir)) TryDelete(f);
            }
            catch (Exception e) { Logger.LogWarning($"Live: couldn't tidy {LiveDir}: {e.Message}"); }

            var w = new LiveIn { Id = id, Path = Path.Combine(LiveDir, id + ReplayFile.Extension) };
            if (NetworkManager.IsReplayPlayback)
            {
                // The game's own quit path, then the menu, then this.
                w.WaitingForMenu = true;
                w.OldUi = InterfaceManager.Instance;
                w.QuitAt = Time.realtimeSinceStartup;
                EngineLoader.isGameRestartRequested = true;
            }
            _liveIn = w;
            Logger.LogInfo($"Live: watching stream {id}.");
        }

        private void StopWatch(string why)
        {
            var w = _liveIn;
            if (w == null) return;
            if (why != null) Logger.LogInfo($"Live: stopped watching {w.Id}: {why}.");
            CloseWriter(w);
            _liveIn = null;
            _livePath = null;
            _liveComplete = false;
            Interlocked.Exchange(ref _liveCommitted, 0);
        }

        private void FailWatch(LiveIn w, string error)
        {
            Logger.LogWarning($"Live: can't watch {w.Id}: {error}");
            w.Error = error;
            w.Phase = "failed";
            CloseWriter(w);
            if (_livePath == w.Path && !w.Started) _livePath = null;
        }

        private void CloseWriter(LiveIn w)
        {
            try { w.Writer?.Dispose(); }
            catch (IOException e) { Logger.LogWarning($"Live: closing {w.Path}: {e.Message}"); }
            w.Writer = null;
        }

        // ---- every frame ---------------------------------------------------

        private void WatchStep()
        {
            var w = _liveIn;
            if (w == null || w.Phase == "failed") return;
            var now = Time.realtimeSinceStartup;

            if (w.WaitingForMenu)
            {
                var ui = InterfaceManager.Instance;
                if (!NetworkManager.IsReplayPlayback && ui != null && ui != w.OldUi && EngineLoader.Instance != null &&
                    now - w.QuitAt > 1f)
                {
                    w.WaitingForMenu = false;
                    w.OldUi = null;
                }
                else if (now - w.QuitAt > 30f)
                {
                    FailWatch(w, "The replay that was playing didn't close.");
                }
                return;
            }

            if (w.Started)
            {
                if (NetworkManager.IsReplayPlayback) w.SawPlayback = true;
                else if (w.SawPlayback)
                {
                    StopWatch("you left it");
                    return;
                }
            }
            else if (w.NextSeq > 0 && CurrentState() == "menu")
            {
                BeginPlayback(w);
            }

            if (!w.Busy && !w.Complete && now >= w.NextPoll) StartCoroutine(PollStreamRoutine(w));
        }

        private void BeginPlayback(LiveIn w)
        {
            var ui = InterfaceManager.Instance;
            if (ui == null || EngineLoader.Instance == null) return;
            w.Started = true;
            w.Phase = w.Complete ? "over" : "playing";
            ui.TransitionTo(InterfaceManager.Window.GameLoading);
            if (!NetworkManager.StartReplayPlayback(w.Path, out var error))
            {
                ui.TransitionTo(InterfaceManager.Window.Home);
                FailWatch(w, error ?? "The game wouldn't play it.");
                return;
            }
            Logger.LogInfo($"Live: playing stream {w.Id} ({w.NextSeq} chunk(s) in so far).");
        }

        // ---- downloading -----------------------------------------------------

        private string LiveApi(string id, int from) =>
            $"{_cfgMmBaseUrl.Value.TrimEnd('/')}/api/live/{id}?from={from}";

        private IEnumerator PollStreamRoutine(LiveIn w)
        {
            w.Busy = true;
            try
            {
                var req = UnityWebRequest.Get(LiveApi(w.Id, w.NextSeq));
                req.timeout = 15;
                _inFlight.Add(req);
                yield return req.SendWebRequest();
                _inFlight.Remove(req);
                var ok = req.result == UnityWebRequest.Result.Success;
                var status = (int)req.responseCode;
                var text = req.downloadHandler?.text;
                var err = req.error;
                req.Dispose();
                if (_liveIn != w) yield break;
                if (status == 404)
                {
                    FailWatch(w, "The site has no such stream (it may be over a day old).");
                    yield break;
                }
                w.NextPoll = Time.realtimeSinceStartup + WatchPollSeconds;
                if (!ok)
                {
                    Logger.LogWarning($"Live: polling {w.Id} failed ({(status > 0 ? status.ToString() : err)}); trying again.");
                    yield break;
                }
                JObject poll;
                try { poll = JObject.Parse(text ?? ""); }
                catch (Exception e)
                {
                    Logger.LogWarning($"Live: the site's answer is unreadable: {e.Message}");
                    yield break;
                }

                if (w.Writer == null && !OpenLiveFile(w, poll)) yield break;

                var startsIn = poll["startsInS"];
                if (startsIn != null && startsIn.Type == JTokenType.Integer)
                {
                    w.Phase = "waiting";
                    w.NextPoll = Time.realtimeSinceStartup + Mathf.Clamp((int)startsIn, 1, 120);
                }

                foreach (var c in (poll["chunks"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var seq = c["seq"]?.Type == JTokenType.Integer ? (int)c["seq"] : -1;
                    var url = TokenString(c["url"]);
                    if (seq != w.NextSeq || !IsHttpUrl(url)) break;
                    var get = UnityWebRequest.Get(url);
                    get.timeout = 30;
                    _inFlight.Add(get);
                    yield return get.SendWebRequest();
                    _inFlight.Remove(get);
                    var bytes = get.result == UnityWebRequest.Result.Success ? get.downloadHandler.data : null;
                    var why = get.error;
                    get.Dispose();
                    if (_liveIn != w || w.Writer == null) yield break;
                    var size = c["sizeBytes"]?.Type == JTokenType.Integer ? (int)c["sizeBytes"] : -1;
                    if (bytes == null || bytes.Length != size)
                    {
                        Logger.LogWarning($"Live: chunk {seq} of {w.Id} didn't download ({why ?? "wrong size"}); trying again.");
                        yield break;
                    }
                    w.Writer.Write(bytes, 0, bytes.Length);
                    w.Writer.Flush();
                    Interlocked.Exchange(ref _liveCommitted, w.Writer.Length);
                    w.NextSeq++;
                    if (!w.Started && w.Phase != "playing") w.Phase = "loading";
                }

                if (poll["complete"]?.Type == JTokenType.Boolean && (bool)poll["complete"] &&
                    (poll["chunks"] as JArray)?.Count == 0)
                {
                    w.Complete = true;
                    w.Phase = "over";
                    _liveComplete = true;
                    Logger.LogInfo($"Live: stream {w.Id} is over; {w.NextSeq} chunk(s), {w.Writer.Length / 1024} KB in all.");
                }
                // A full page (the site lists 60 at a time): more are waiting
                // already, a game well under way. Carry straight on.
                else if ((poll["chunks"] as JArray)?.Count >= 60)
                {
                    w.NextPoll = 0f;
                }
            }
            finally { w.Busy = false; }
        }

        // The first answer: refuse a stream this game can't play, then the
        // empty file the chunks go into (and the mod list beside it).
        private bool OpenLiveFile(LiveIn w, JObject poll)
        {
            var version = TokenString(poll["gameVersion"]);
            var sidecar = poll["sidecar"] as JObject;
            // A modded game's version is checked by the Mod API when playback
            // starts (it applies the mods first); a vanilla one is checked
            // here, the way the game's replay list does.
            if (sidecar == null)
            {
                var mine = Application.version + "#" + FilesCache.ComputeLuaHashString();
                if (version != mine)
                {
                    FailWatch(w, $"It was streamed from game version {version}, and this game is {mine}.");
                    return false;
                }
            }
            try
            {
                Directory.CreateDirectory(LiveDir);
                if (sidecar != null) File.WriteAllText(w.Path + ".mods.json", sidecar.ToString(Formatting.Indented));
                w.Writer = new FileStream(w.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e)
            {
                FailWatch(w, $"Couldn't write {w.Path}: {e.Message}");
                return false;
            }
            _livePath = w.Path;
            _liveComplete = false;
            Interlocked.Exchange(ref _liveCommitted, 0);
            return true;
        }

        // ---- the game's replay socket ----------------------------------------

        private static AccessTools.FieldRef<ReplayClientSockets, string> _rcsPath;
        private static AccessTools.FieldRef<ReplayClientSockets, FileStream> _rcsStream;
        private static AccessTools.FieldRef<ReplayClientSockets, ReplayFile.Header> _rcsHeader;
        private static AccessTools.FieldRef<ReplayClientSockets, double> _rcsNextRead;
        private static AccessTools.FieldRef<ReplayClientSockets, bool> _rcsLoaded;
        private static AccessTools.FieldRef<ReplayClientSockets, bool> _rcsEnd;
        private static AccessTools.FieldRef<ReplayClientSockets, bool> _rcsLaunchSent;
        private static Func<ReplayClientSockets, NativeList<byte>, bool> _rcsTryReadFrame;

        private void ApplyLivePatches()
        {
            var t = typeof(ReplayClientSockets);
            _rcsPath = AccessTools.FieldRefAccess<ReplayClientSockets, string>("filePath");
            _rcsStream = AccessTools.FieldRefAccess<ReplayClientSockets, FileStream>("stream");
            _rcsHeader = AccessTools.FieldRefAccess<ReplayClientSockets, ReplayFile.Header>("header");
            _rcsNextRead = AccessTools.FieldRefAccess<ReplayClientSockets, double>("nextFrameReadTime");
            _rcsLoaded = AccessTools.FieldRefAccess<ReplayClientSockets, bool>("hasClientLoaded");
            _rcsEnd = AccessTools.FieldRefAccess<ReplayClientSockets, bool>("hasReachedEnd");
            _rcsLaunchSent = AccessTools.FieldRefAccess<ReplayClientSockets, bool>("hasSentLaunchMessages");
            _rcsTryReadFrame = AccessTools.MethodDelegate<Func<ReplayClientSockets, NativeList<byte>, bool>>(
                AccessTools.Method(t, "TryReadFrame") ?? throw new MissingMethodException("ReplayClientSockets.TryReadFrame"));

            _harmony.Patch(AccessTools.Method(t, nameof(ReplayClientSockets.TryOpen)),
                prefix: new HarmonyMethod(typeof(LadderReporterPlugin), nameof(LiveTryOpenPrefix)));
            // After ReplayManager's pause prefix, which this respects.
            _harmony.Patch(AccessTools.Method(t, nameof(ReplayClientSockets.Receive)),
                prefix: new HarmonyMethod(typeof(LadderReporterPlugin), nameof(LiveReceivePrefix)) { priority = Priority.Low });
        }

        private static bool IsLive(string path) =>
            _livePath != null && path != null && string.Equals(path, _livePath, StringComparison.OrdinalIgnoreCase);

        // The game's TryOpen, but sharing writes: the mod appends to the file
        // while the game reads it.
        private static bool LiveTryOpenPrefix(ReplayClientSockets __instance, string replayPath, ref string error, ref bool __result)
        {
            if (!IsLive(replayPath)) return true;
            FileStream fs = null;
            try
            {
                fs = new FileStream(replayPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!ReplayFile.TryReadHeader(fs, out var header) || header.formatVersion != ReplayFile.FormatVersion)
                {
                    fs.Dispose();
                    error = "The live replay's header isn't one this game reads.";
                    __result = false;
                    return false;
                }
                _rcsPath(__instance) = replayPath;
                _rcsStream(__instance) = fs;
                _rcsHeader(__instance) = header;
                error = null;
                __result = true;
                _log?.LogInfo($"Live: the game is playing {Path.GetFileName(replayPath)}, map {header.mapPath}.");
            }
            catch (Exception e)
            {
                fs?.Dispose();
                error = "Could not open the live replay: " + e.Message;
                __result = false;
            }
            return false;
        }

        private static bool _warnedReceive;

        // The game's Receive loop for the live file, reading only frames that
        // are already whole, and waiting at the end of them rather than
        // ending. Before the client has loaded (the launch messages), and
        // once the stream is complete, the game's own Receive runs.
        private static bool LiveReceivePrefix(ReplayClientSockets __instance, NativeHashMap<PlayerID, NetworkConnection> connections,
            bool __runOriginal)
        {
            if (!__runOriginal) return false;   // paused by ReplayManager
            try
            {
                if (_liveComplete || !IsLive(_rcsPath(__instance))) return true;
                if (!_rcsLaunchSent(__instance) || !_rcsLoaded(__instance) || _rcsEnd(__instance)) return true;
                if (!connections.TryGetValue(ReplayClientSockets.ServerEndpoint, out var item)) return false;

                var fs = _rcsStream(__instance);
                var committed = Interlocked.Read(ref _liveCommitted);
                var simSpeed = ClientEngine.Data.globalEngineData.globalTime.simSpeed;
                ref var next = ref _rcsNextRead(__instance);
                next = Math.Max(next, Time.unscaledTimeAsDouble - Time.unscaledDeltaTime);
                var queued = NetworkManager.ClientData.Data.receivedHostData.bufferedCommunicatorDatas.Length;
                while (queued < 32 && Time.unscaledTimeAsDouble >= next)
                {
                    if (!WholeFrameAhead(fs, committed)) break;
                    if (!_rcsTryReadFrame(__instance, item.receivedDataBuffer)) break;
                    queued++;
                    next += 0.1f / simSpeed;
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_warnedReceive)
                {
                    _warnedReceive = true;
                    _log?.LogError($"Live: the replay feed failed, handing back to the game's: {e}");
                }
                return true;
            }
        }

        // Whether a whole frame starts at the stream's position, within what
        // has been written; the position is left where it was.
        private static readonly byte[] FrameHead = new byte[5];

        private static bool WholeFrameAhead(FileStream fs, long committed)
        {
            var pos = fs.Position;
            if (pos + 5 > committed) return false;
            var n = ReadFully(fs, FrameHead, 5);
            fs.Position = pos;
            if (n != 5) return false;
            var length = BitConverter.ToInt32(FrameHead, 1);
            return length >= 0 && pos + 5 + length <= committed;
        }
    }
}
