using System;
using System.IO;
using EM.DOTS.Engine.Loader;
using EM.Lua.Client;
using EM.Network;
using EM.Network.Replay;
using EM.Network.Sockets;
using EM.UI;
using HarmonyLib;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.Replays
{
    // Drives the game's own replay playback. The game plays a `.sanreplay`
    // through ReplayClientSockets, a fake socket that reads recorded packets
    // into the client's receive buffer, paced by the sim speed, with at most
    // 32 ticks queued. The client only steps a tick once its packet is
    // buffered, so everything here is about that socket:
    //
    //   pause        a prefix on its Receive that feeds nothing
    //   speed        the client's own SetSimulationSpeed (0.1x to 16x)
    //   position     frames read (a postfix on TryReadFrame) minus queued
    //   length       a scan of the file's frame headers
    //   fast-forward speed 16x until the target tick is reached
    //   rewind       the game's quit path, then StartReplayPlayback again
    //
    // Nothing is recorded by the mod any more; the game writes the file.
    internal static class ReplayPlayer
    {
        internal enum Stage { Idle, Loading, Running, Finished }

        internal static Stage Current { get; private set; } = Stage.Idle;
        internal static bool Active => Current != Stage.Idle;
        internal static string FilePath { get; private set; }
        internal static ReplayFile.Header Header;
        internal static int TotalTicks { get; private set; }
        internal static int SeekTarget { get; private set; } = -1;
        internal static bool Restarting => _restart != null;

        private static float _speed = 1f;
        internal static float Speed
        {
            get => _speed;
            set
            {
                _speed = Mathf.Clamp(value, 0.1f, 16f);
                _speedDirty = true;
            }
        }
        private static bool _speedDirty;

        internal static bool Paused;

        private static object _socket;             // the ReplayClientSockets being driven
        private static int _fed;                   // frames the socket has read from the file
        private static float _seekSpeedBefore;
        private static bool _seekPausedBefore;

        private sealed class PendingRestart
        {
            public string Path;
            public int TargetTick;
            public float Speed;
            public bool Paused;
            public InterfaceManager OldInterface;
            public float StoppedAt;
            public bool WaitingForScene;
        }
        private static PendingRestart _restart;

        // Private members of the game's replay socket.
        private static AccessTools.FieldRef<object, string> _filePathRef;
        private static AccessTools.FieldRef<object, bool> _loadedRef;
        private static AccessTools.FieldRef<object, bool> _endRef;
        private static AccessTools.FieldRef<object, bool> _launchSentRef;
        private static Func<INetworkClientSockets> _clientSockets;

        internal static event Action OnLuaStartup;   // the client VM exists; install early hooks

        internal static void ApplyPatches(Harmony harmony)
        {
            var t = typeof(ReplayClientSockets);
            _filePathRef = AccessTools.FieldRefAccess<string>(t, "filePath");
            _loadedRef = AccessTools.FieldRefAccess<bool>(t, "hasClientLoaded");
            _endRef = AccessTools.FieldRefAccess<bool>(t, "hasReachedEnd");
            _launchSentRef = AccessTools.FieldRefAccess<bool>(t, "hasSentLaunchMessages");
            var socketsField = AccessTools.Field(typeof(NetworkManager), "clientSockets")
                               ?? throw new MissingFieldException("NetworkManager.clientSockets");
            _clientSockets = () => (INetworkClientSockets)socketsField.GetValue(null);

            harmony.Patch(AccessTools.Method(t, "Receive"),
                prefix: new HarmonyMethod(typeof(ReplayPlayer), nameof(ReceivePrefix)));
            harmony.Patch(AccessTools.Method(t, "TryReadFrame") ?? throw new MissingMethodException("ReplayClientSockets.TryReadFrame"),
                postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(TryReadFramePostfix)));
            harmony.Patch(AccessTools.Method(typeof(ClientLuaInterface), nameof(ClientLuaInterface.Startup)),
                postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(LuaStartupPostfix)));
        }

        // Pause: once the launch messages are through, a paused socket reads
        // nothing, and the client stops at the last buffered tick.
        private static bool ReceivePrefix(object __instance)
        {
            if (!Paused || !ReferenceEquals(__instance, _socket)) return true;
            try { if (!_launchSentRef(__instance)) return true; } catch { return true; }
            return false;
        }

        private static void TryReadFramePostfix(object __instance, bool __result)
        {
            if (__result && ReferenceEquals(__instance, _socket)) _fed++;
        }

        private static void LuaStartupPostfix()
        {
            if (!NetworkManager.IsReplayPlayback) return;
            try { OnLuaStartup?.Invoke(); }
            catch (Exception e) { _log.LogWarning($"Replay: early Lua hook failed: {e.Message}"); }
        }

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

        internal static int CurrentTick => Math.Max(0, _fed - Buffered);

        /// Called every frame. Notices the game starting a replay (from its
        /// own menu or a rewind), tracks it, and ends with it.
        internal static void Update()
        {
            if (!Active)
            {
                if (_restart != null) ContinueRestart();
                if (NetworkManager.IsReplayPlayback) Begin();
                return;
            }

            if (!NetworkManager.IsReplayPlayback)
            {
                Stop();
                return;
            }

            bool loaded = false, ended = false;
            try
            {
                loaded = _loadedRef(_socket);
                ended = _endRef(_socket);
            }
            catch { }

            if (Current == Stage.Loading && loaded) Current = Stage.Running;
            if (Current == Stage.Running)
            {
                if (ended && Buffered == 0) Current = Stage.Finished;
                if (SeekTarget >= 0 && (CurrentTick >= SeekTarget || Current == Stage.Finished)) EndSeek();
            }

            if (_speedDirty && LuaReady)
            {
                _speedDirty = !RunLua($"pcall(function() Engine.SetSimulationSpeed({_speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}) end)");
            }
        }

        private static void Begin()
        {
            _socket = _clientSockets();
            if (_socket == null) return;
            try { FilePath = _filePathRef(_socket); }
            catch { FilePath = null; }
            _fed = 0;
            Paused = false;
            _speedDirty = false;
            _speed = 1f;
            SeekTarget = -1;
            Header = default;
            TotalTicks = 0;
            if (FilePath != null) ReadFile(FilePath);
            Current = Stage.Loading;
            _log.LogInfo($"Replay: playback of {FilePath} ({TotalTicks} ticks) started; controls on.");
        }

        // The header the game wrote, and how many frames follow it: each is
        // a type byte and an int length, so counting is a seek per frame.
        private static void ReadFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!ReplayFile.TryReadHeader(fs, out Header)) return;
                    var head = new byte[5];
                    int count = 0;
                    while (fs.Read(head, 0, 5) == 5)
                    {
                        var len = BitConverter.ToInt32(head, 1);
                        if (len < 0 || fs.Position + len > fs.Length) break;
                        fs.Seek(len, SeekOrigin.Current);
                        count++;
                    }
                    TotalTicks = count;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"Replay: could not read {path}: {e.Message}");
            }
        }

        /// Moves to a tick. Forward runs the socket at 16x until the client
        /// gets there; backward restarts playback and does the same from 0.
        internal static void SeekTo(int tick)
        {
            if (Current != Stage.Running && Current != Stage.Finished) return;
            tick = Math.Max(0, Math.Min(tick, Math.Max(0, TotalTicks - 1)));
            if (tick >= CurrentTick)
            {
                if (Current == Stage.Finished) return;
                if (SeekTarget < 0)
                {
                    _seekSpeedBefore = _speed;
                    _seekPausedBefore = Paused;
                }
                SeekTarget = tick;
                Paused = false;
                Speed = 16f;
                return;
            }
            if (FilePath == null) return;

            _restart = new PendingRestart
            {
                Path = FilePath,
                TargetTick = tick,
                Speed = SeekTarget >= 0 ? _seekSpeedBefore : _speed,
                Paused = SeekTarget >= 0 ? _seekPausedBefore : Paused,
                OldInterface = InterfaceManager.Instance,
            };
            SeekTarget = tick;
            _log.LogInfo($"Replay: rewinding to tick {tick}, restarting playback.");
            EngineLoader.isGameRestartRequested = true;
        }

        private static void EndSeek()
        {
            SeekTarget = -1;
            Speed = _seekSpeedBefore;
            Paused = _seekPausedBefore;
        }

        internal static void SkipBy(int seconds) => SeekTo(CurrentTick + seconds * 10);

        // After the game's quit path has reloaded the scene, start the same
        // file again the way the replay menu does.
        private static void ContinueRestart()
        {
            var r = _restart;
            if (!r.WaitingForScene) return;
            if (Time.realtimeSinceStartup - r.StoppedAt < 1f) return;
            var ui = InterfaceManager.Instance;
            if (ui == null || ui == r.OldInterface || EngineLoader.Instance == null)
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
            ui.TransitionTo(InterfaceManager.Window.Loading);
            if (!NetworkManager.StartReplayPlayback(r.Path, out var error))
            {
                _log.LogError($"Replay: rewind failed: {error}");
                ui.TransitionTo(InterfaceManager.Window.Main);
                SeekTarget = -1;
                return;
            }
            Begin();
            _seekSpeedBefore = r.Speed;
            _seekPausedBefore = r.Paused;
            SeekTarget = r.TargetTick;
            Speed = 16f;
        }

        /// Leaves via the game's own quit path.
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
            _socket = null;
            FilePath = null;
            _fed = 0;
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
    }
}
