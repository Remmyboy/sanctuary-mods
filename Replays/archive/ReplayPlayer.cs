using System;
using System.Collections.Generic;
using EM.Components;
using EM.DOTS.Engine.Loader;
using EM.GameUtils;
using EM.Map;
using EM.Network;
using EM.UI;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.Replays
{
    // Plays a recording back through the game's own client. The client never
    // simulates on its own: it applies the host's packet for each tick, and
    // its rate manager only steps once a packet for that tick is buffered.
    // So playback is just the normal client start-up with no socket, plus a
    // feeder that pushes recorded packets into the receive buffer at the
    // chosen speed. Pause is "feed nothing"; fast-forward is "feed ahead" and
    // the client catches up as fast as it can simulate.
    //
    // Rewind is the one thing the client can't do, since it keeps no
    // snapshots: going back means leaving through the game's own quit path
    // (which reloads the scene), starting the client again once the menu is
    // back, and fast-forwarding to the target tick.
    internal static class ReplayPlayer
    {
        internal enum Stage { Idle, Loading, Running, Finished }

        internal static Stage Current { get; private set; } = Stage.Idle;
        internal static bool Active => Current != Stage.Idle;
        internal static ReplayHeader Header { get; private set; }
        internal static string FilePath { get; private set; }
        internal static int PovClientId { get; private set; }
        internal static float Speed = 1f;
        internal static bool Paused;

        /// Tick the player is fast-forwarding to, or -1. Cleared once the
        /// client has caught up.
        internal static int SeekTarget { get; private set; } = -1;

        /// True from a rewind being requested until the client is running
        /// again; the panel keeps showing through the restart.
        internal static bool Restarting => _restart != null;

        private static List<ReplayFrame> _frames;
        private static int _fed;
        private static double _clock;         // playback position, in ticks
        private static string _savedMapPref;
        private static float _loadingSince;

        private sealed class PendingRestart
        {
            public string Path;
            public int PovClientId;
            public int TargetTick;
            public float Speed;
            public bool Paused;
            public InterfaceManager OldInterface;
            public EngineLoader OldLoader;
            public float StoppedAt;
            public bool WaitingForScene;
        }

        private static PendingRestart _restart;

        // Packets waiting in the client's buffer each hold a 1 MB rewindable
        // allocator, so feeding far ahead costs memory rather than time.
        private const int MaxBuffered = 30;
        private const float LoadTimeoutSeconds = 600f;

        internal static int TotalTicks => _frames?.Count ?? 0;
        internal static int CurrentTick => Math.Max(0, _fed - Buffered);

        private static int Buffered
        {
            get
            {
                try
                {
                    ref var net = ref NetworkManager.ClientData.Data;
                    return net.isCreated ? net.receivedHostData.bufferedCommunicatorDatas.Length : 0;
                }
                catch { return 0; }
            }
        }

        internal static void ApplyPatches(Harmony harmony)
        {
            // The client start-up coroutine reports "Waiting for players" at
            // 100% right before it would tell a host it is loaded. That is the
            // moment the receive buffer may start filling.
            var report = AccessTools.Method(typeof(InterfaceManager), nameof(InterfaceManager.SetLoadingStatusReport),
                             new[] { typeof(string), typeof(float) })
                         ?? throw new MissingMethodException("InterfaceManager.SetLoadingStatusReport(string, float)");
            harmony.Patch(report, postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(LoadingStatusPostfix)));
        }

        private static void LoadingStatusPostfix(float percentage)
        {
            if (Current == Stage.Loading && percentage >= 0.999f) OnClientLoaded();
        }

        /// Starts playback from the main menu. Returns null on success, else a
        /// reason for the UI.
        internal static string Start(string path, int povClientId)
        {
            if (InMatch) return "leave the match first";
            return Launch(path, povClientId);
        }

        private static string Launch(string path, int povClientId)
        {
            if (Active) return "a replay is already playing";
            if (LobbyManager.IsInLobby) return "leave the lobby first";
            if (InterfaceManager.Instance == null || EngineLoader.Instance == null) return "the game UI is not ready";

            List<ReplayFrame> frames;
            ReplayHeader header;
            try
            {
                frames = ReplayFile.ReadFrames(path, out header);
            }
            catch (Exception e)
            {
                return "could not read the replay: " + e.Message;
            }
            if (frames.Count == 0) return "the replay holds no ticks";
            if (string.IsNullOrEmpty(header.Map)) return "the replay has no map path";
            if (header.GameVersion != Application.version)
            {
                _log.LogWarning($"Replay was recorded on game version '{header.GameVersion}', this is '{Application.version}'; it may not play back cleanly.");
            }

            try
            {
                // A client context with no socket: the send side returns early
                // without a server connection, and the receive side is what
                // the feeder fills.
                NetworkManager.CreateClient(NetworkTransport.LAN);
                ref var net = ref NetworkManager.ClientData.Data;
                net.ownClientID = (byte)povClientId;

                // SelectedMapPath is a PlayerPref; put the user's pick back
                // once the map has loaded.
                _savedMapPref = MapManager.SelectedMapPath;
                MapManager.SelectedMapPath = header.Map;

                InterfaceManager.Instance.TransitionTo(InterfaceManager.Window.Loading);
                InterfaceManager.Instance.SetLoadingStatusReport("Loading replay...", 0f);
                EngineLoader.Instance.CreateClient();
                EngineLoader.Instance.LaunchClient();
            }
            catch (Exception e)
            {
                _log.LogError($"Replay: client start failed: {e}");
                RestoreMapPref();
                return "the client could not be started: " + e.Message;
            }

            _frames = frames;
            Header = header;
            FilePath = path;
            PovClientId = povClientId;
            _fed = 0;
            _clock = 0;
            Paused = false;
            Speed = 1f;
            SeekTarget = -1;
            _loadingSince = Time.realtimeSinceStartup;
            Current = Stage.Loading;
            _log.LogInfo($"Replay: playing {path} ({frames.Count} ticks) as client {povClientId}");
            return null;
        }

        private static void OnClientLoaded()
        {
            RestoreMapPref();
            Current = Stage.Running;
            _log.LogInfo("Replay: client loaded, feeding.");
        }

        internal static void Update(float dt)
        {
            if (!Active)
            {
                if (_restart != null) ContinueRestart();
                return;
            }

            bool created;
            try { created = NetworkManager.ClientData.Data.isCreated; }
            catch { created = false; }
            if (!created)
            {
                // The game tore the client down (quit to menu) without going
                // through CleanUpGame first.
                Stop();
                return;
            }

            if (Current == Stage.Loading)
            {
                if (Time.realtimeSinceStartup - _loadingSince > LoadTimeoutSeconds)
                {
                    _log.LogError("Replay: the client never finished loading; giving up.");
                    _restart = null;
                    Quit();
                }
                return;
            }
            if (Current != Stage.Running) return;

            if (!Paused) _clock += dt * 10.0 * Speed;

            int minDelay = 0;
            try { minDelay = DebugManager.data.miscelenous.minimumSimTickDelayBetweenHostAndClient; }
            catch { }

            // The client runs to (latest buffered tick - minDelay), so stay
            // that far ahead of the clock.
            int target = (int)_clock + minDelay + 1;
            int buffered = Buffered;
            while (_fed < _frames.Count && _fed < target && buffered < MaxBuffered)
            {
                if (!Feed(_frames[_fed]))
                {
                    Current = Stage.Finished;
                    return;
                }
                _fed++;
                buffered++;
            }
            // Don't let the clock run away while the client is still catching
            // up, or a pause would take seconds to bite. A seek is the one
            // time the clock is meant to be far ahead: hold it at the target
            // until the client gets there.
            if (SeekTarget >= 0 && CurrentTick >= SeekTarget) SeekTarget = -1;
            if (_clock > _fed + MaxBuffered)
            {
                _clock = Math.Max(_fed + MaxBuffered, SeekTarget >= 0 ? SeekTarget : 0);
            }
            if (_fed >= _frames.Count && Buffered == 0) Current = Stage.Finished;
        }

        // A decoded frame is put back into the wire layout the client's own
        // deserialiser expects: brotli(simTick) int int brotli(types)
        // brotli(data) brotli(hash). Fastest quality; it's undone a moment
        // later and only has to be a valid stream.
        private static byte[] Encode(ReplayFrame f)
        {
            var d = f.Data;
            int typeCount = BitConverter.ToInt32(d, 16);
            int dataCount = BitConverter.ToInt32(d, 20);
            using (var ms = new System.IO.MemoryStream(d.Length / 2 + 64))
            {
                Block(ms, BitConverter.GetBytes(f.Tick), 0, 4);
                ms.Write(d, 16, 8);
                Block(ms, d, 24, typeCount);
                Block(ms, d, 24 + typeCount, dataCount);
                Block(ms, d, 0, 16);
                return ms.ToArray();
            }
        }

        private static void Block(System.IO.Stream ms, byte[] src, int offset, int count)
        {
            using (var brotli = Brotli.Compress(ms, System.IO.Compression.CompressionLevel.Fastest))
            {
                brotli.Write(src, offset, count);
            }
        }

        private static bool Feed(ReplayFrame f)
        {
            ref var net = ref NetworkManager.ClientData.Data;
            var wire = f.Kind == ReplayFrame.KindDecoded ? Encode(f) : f.Data;
            var payload = new NativeArray<byte>(wire, Allocator.Temp);
            try
            {
                // Same three steps NetworkManager takes for a HostData message.
                var alloc = net.GetRewindableAllocator();
                var packet = default(HostToClientCommunicatorDataSingleton);
                if (packet.DeserializeNetworkData(payload, alloc.Allocator.ToAllocator, out var read) && read == payload.Length)
                {
                    net.receivedHostData.bufferedCommunicatorDatas.Add(in packet);
                    net.receivedHostData.allocatorsInUse.Add(in alloc);
                    return true;
                }
                net.ReturnAllocator(alloc);
                _log.LogError($"Replay: tick {f.Tick} would not decode ({read} of {payload.Length} bytes); stopping here.");
                return false;
            }
            finally
            {
                payload.Dispose();
            }
        }

        /// Moves to a tick. Forward is a fast-forward: the clock jumps and the
        /// client catches up. Backward restarts the client and fast-forwards
        /// from the beginning.
        internal static void SeekTo(int tick)
        {
            if (Current != Stage.Running && Current != Stage.Finished) return;
            tick = Math.Max(0, Math.Min(tick, TotalTicks - 1));
            if (tick >= CurrentTick)
            {
                if (Current == Stage.Finished) return;
                _clock = Math.Max(_clock, tick);
                SeekTarget = tick;
                return;
            }

            _restart = new PendingRestart
            {
                Path = FilePath,
                PovClientId = PovClientId,
                TargetTick = tick,
                Speed = Speed,
                Paused = Paused,
                OldInterface = InterfaceManager.Instance,
                OldLoader = EngineLoader.Instance,
            };
            SeekTarget = tick;
            _log.LogInfo($"Replay: rewinding to tick {tick}, restarting the client.");
            // The game's own quit path: CleanUpGame (our Stop) then a scene reload.
            EngineLoader.isGameRestartRequested = true;
        }

        internal static void SkipBy(int seconds)
        {
            SeekTo(CurrentTick + seconds * 10);
        }

        // Runs while idle with a rewind pending: wait for the scene reload to
        // hand us fresh UI and loader instances, then launch again.
        private static void ContinueRestart()
        {
            var r = _restart;
            if (!r.WaitingForScene) return;
            if (Time.realtimeSinceStartup - r.StoppedAt < 1f) return;
            var ui = InterfaceManager.Instance;
            var loader = EngineLoader.Instance;
            if (ui == null || loader == null || ui == r.OldInterface || loader == r.OldLoader)
            {
                if (Time.realtimeSinceStartup - r.StoppedAt > 30f)
                {
                    _log.LogError("Replay: the menu never came back after the rewind; giving up.");
                    _restart = null;
                    SeekTarget = -1;
                }
                return;
            }

            _restart = null;
            var error = Launch(r.Path, r.PovClientId);
            if (error != null)
            {
                _log.LogError($"Replay: rewind failed: {error}");
                SeekTarget = -1;
                return;
            }
            _clock = r.TargetTick;
            Speed = r.Speed;
            Paused = r.Paused;
            SeekTarget = r.TargetTick;
        }

        /// Leaves via the game's own quit path, which ends in CleanUpGame.
        internal static void Quit()
        {
            if (!Active) return;
            _restart = null;
            EngineLoader.isGameRestartRequested = true;
        }

        internal static void Stop()
        {
            if (!Active) return;
            Current = Stage.Idle;
            _frames = null;
            Header = null;
            FilePath = null;
            _fed = 0;
            RestoreMapPref();
            if (_restart != null)
            {
                _restart.WaitingForScene = true;
                _restart.StoppedAt = Time.realtimeSinceStartup;
            }
            else
            {
                SeekTarget = -1;
            }
        }

        private static void RestoreMapPref()
        {
            if (_savedMapPref == null) return;
            try { MapManager.SelectedMapPath = _savedMapPref; } catch { }
            _savedMapPref = null;
        }
    }
}
