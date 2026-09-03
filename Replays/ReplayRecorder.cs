using System;
using System.IO;
using System.Linq;
using System.Threading;
using EM.Core;
using EM.Lua;
using EM.Map;
using EM.Network;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.Replays
{
    // Captures the host-to-client stream as it arrives. Every packet the
    // client's network layer parses goes through NetworkManager.HandleMessage,
    // so a prefix there sees the raw payload before the game consumes it. The
    // host machine's own client is a socket client like any other, so this
    // records on the host too.
    //
    // A recording starts on the LaunchClient message (which carries the map
    // and arrives after the client ID and lobby state), and ends when the
    // game tears the client down (EngineLoader.CleanUpGame).
    internal static class ReplayRecorder
    {
        internal static bool Enabled = true;
        internal static string Folder;

        private static Stream _out;
        private static string _partPath;
        private static string _finalPath;
        private static ReplayHeader _header;
        private static int _frames;
        private static int _lastTick = -1;
        private static float _lastFlush;

        internal static bool Recording => _out != null;
        internal static int Frames => _frames;
        internal static int LastTick => _lastTick;
        internal static string LastSaved { get; private set; }

        internal static void ApplyPatches(Harmony harmony)
        {
            var handle = AccessTools.Method(typeof(NetworkManager), "HandleMessage")
                         ?? throw new MissingMethodException("NetworkManager.HandleMessage");
            harmony.Patch(handle,
                prefix: new HarmonyMethod(typeof(ReplayRecorder), nameof(HandleMessagePrefix)),
                postfix: new HarmonyMethod(typeof(ReplayRecorder), nameof(HandleMessagePostfix)));
        }

        // The packet is: brotli(simTick) int typeCount int dataCount
        // brotli(types) brotli(data) brotli(hash), each Brotli block a complete
        // stream. Decoding the blocks with the game's own private helper means
        // the file holds plain bytes, which the whole-file compressor can then
        // pack far tighter than the per-field blocks ever could.
        private delegate bool DecompressDelegate(NativeArray<byte> source, ref int offset, out NativeList<byte> decompressed);
        private static DecompressDelegate _decompress;
        private static bool _decodeUnavailable;

        private static bool TryDecode(NativeArray<byte> payload, out int tick, out byte[] decoded)
        {
            tick = -1;
            decoded = null;
            if (_decodeUnavailable) return false;
            if (_decompress == null)
            {
                var mi = AccessTools.Method(typeof(NetworkUtils), "DecompressData");
                if (mi == null)
                {
                    _decodeUnavailable = true;
                    _log.LogWarning("Replay recorder: NetworkUtils.DecompressData not found; storing packets as-is (larger files).");
                    return false;
                }
                _decompress = (DecompressDelegate)Delegate.CreateDelegate(typeof(DecompressDelegate), mi);
            }

            int off = 0;
            if (!NetworkUtils.DeserializeStruct<int>(payload, ref off, out tick)) return false;
            if (payload.Length - off < 8) return false;
            int typeCount = payload.ReinterpretLoad<int>(off);
            off += 4;
            int dataCount = payload.ReinterpretLoad<int>(off);
            off += 4;
            if (!_decompress(payload, ref off, out var types)) return false;
            if (!_decompress(payload, ref off, out var data)) return false;
            if (!_decompress(payload, ref off, out var hash)) return false;
            if (off != payload.Length || types.Length != typeCount || data.Length != dataCount || hash.Length != 16) return false;

            decoded = new byte[16 + 8 + typeCount + dataCount];
            // Copy each block out; the Temp lists die with the frame.
            var pos = 0;
            Buffer.BlockCopy(hash.AsArray().ToArray(), 0, decoded, pos, 16);
            pos += 16;
            Buffer.BlockCopy(BitConverter.GetBytes(typeCount), 0, decoded, pos, 4);
            pos += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(dataCount), 0, decoded, pos, 4);
            pos += 4;
            if (typeCount > 0) Buffer.BlockCopy(types.AsArray().ToArray(), 0, decoded, pos, typeCount);
            pos += typeCount;
            if (dataCount > 0) Buffer.BlockCopy(data.AsArray().ToArray(), 0, decoded, pos, dataCount);
            return true;
        }

        private static void HandleMessagePrefix(sbyte dataType, NativeArray<byte> payload, bool isHost)
        {
            if (isHost || _out == null || dataType != NetworkConstants.HostData) return;
            try
            {
                int tick;
                if (TryDecode(payload, out tick, out var decoded))
                {
                    ReplayFile.WriteFrame(_out, ReplayFrame.KindDecoded, tick, decoded);
                }
                else
                {
                    // Keep the packet as it came; playback feeds those through
                    // the same path as live traffic.
                    ReplayFile.WriteFrame(_out, ReplayFrame.KindWire, tick, payload.ToArray());
                }
                _frames++;
                if (tick > _lastTick) _lastTick = tick;
                if (Time.realtimeSinceStartup - _lastFlush > 2f)
                {
                    _lastFlush = Time.realtimeSinceStartup;
                    _out.Flush();
                }
            }
            catch (Exception e)
            {
                _log.LogError($"Replay recorder: write failed, recording abandoned: {e.Message}");
                Abandon();
            }
        }

        private static void HandleMessagePostfix(sbyte dataType, bool isHost)
        {
            if (isHost || dataType != NetworkConstants.LaunchClient) return;
            if (!Enabled || ReplayPlayer.Active) return;
            try
            {
                Start();
            }
            catch (Exception e)
            {
                _log.LogError($"Replay recorder: could not start: {e}");
                Abandon();
            }
        }

        private static void Start()
        {
            Stop();

            var header = new ReplayHeader
            {
                GameVersion = Application.version,
                LuaHash = SafeLuaHash(),
                Map = MapManager.SelectedMapPath,
                RecorderClientId = NetworkManager.ClientData.Data.ownClientID,
                RecordedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            };

            // Client IDs are handed out by the host in lobby order, one per
            // human player; mirror that so a replay can open from any seat.
            var state = LobbyManager.CurrentState;
            if (state != null)
            {
                int next = 0;
                foreach (var p in state.players)
                {
                    if (p.type == PlayerType.Empty) continue;
                    header.Players.Add(new ReplayPlayerInfo
                    {
                        Name = p.name,
                        Faction = p.faction.ToString(),
                        ArmyId = p.armyID,
                        Team = p.team,
                        Type = p.type.ToString(),
                        ClientId = p.type == PlayerType.Player ? next++ : 255,
                    });
                }
            }

            Directory.CreateDirectory(Folder);
            var mapName = Path.GetFileNameWithoutExtension(header.Map ?? "") ?? "";
            if (mapName.Length == 0) mapName = "unknown";
            // 202609031240_There_Is_Time_Remmy+Bob_vs_Skoub: date, map, then
            // the seats grouped by team.
            var teams = header.Players
                .Where(p => p.Type == "Player" || p.Type == "AI")
                .GroupBy(p => p.Team)
                .OrderBy(g => g.Key)
                .Select(g => string.Join("+", g.Select(p => Sanitise(p.Name))))
                .ToList();
            var who = teams.Count > 0 ? string.Join("_vs_", teams) : "unknown";
            if (who.Length > 80) who = who.Substring(0, 80);
            var baseName = $"{DateTime.Now:yyyyMMddHHmm}_{Sanitise(mapName)}_{who}";
            _finalPath = Path.Combine(Folder, baseName + ReplayFile.Extension);
            // Minute resolution can collide (a restart, a quick rematch).
            for (int n = 2; File.Exists(_finalPath) || File.Exists(_finalPath + ReplayFile.PartSuffix); n++)
            {
                _finalPath = Path.Combine(Folder, $"{baseName}_{n}{ReplayFile.Extension}");
            }
            _partPath = _finalPath + ReplayFile.PartSuffix;

            _out = new BufferedStream(new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.Read), 1 << 16);
            ReplayFile.WriteHeader(_out, header, finalised: false);
            _out.Flush();
            _header = header;
            _frames = 0;
            _lastTick = -1;
            _lastFlush = Time.realtimeSinceStartup;
            _log.LogInfo($"Replay recorder: recording {mapName} as {header.Players.Count} players, client {header.RecorderClientId} -> {_partPath}");
        }

        /// Closes the recording and gzips it into its final file off the main
        /// thread, so leaving a long match doesn't hitch.
        internal static void Stop()
        {
            if (_out == null) return;
            var output = _out;
            var part = _partPath;
            var final = _finalPath;
            var header = _header;
            var frames = _frames;
            var lastTick = _lastTick;
            _out = null;

            try { output.Flush(); output.Dispose(); }
            catch (Exception e) { _log.LogWarning($"Replay recorder: close failed: {e.Message}"); }

            if (frames == 0)
            {
                try { File.Delete(part); } catch { }
                _log.LogInfo("Replay recorder: no ticks were received, nothing saved.");
                return;
            }

            header.TickCount = lastTick + 1;
            LastSaved = final;
            var worker = new Thread(() =>
            {
                try
                {
                    ReplayFile.Finalise(part, final, header);
                    _log.LogInfo($"Replay saved: {final} ({frames} ticks, {header.TickCount / 10 / 60}m{header.TickCount / 10 % 60:00}s)");
                }
                catch (Exception e)
                {
                    _log.LogError($"Replay finalise failed; the raw recording is still at {part}: {e.Message}");
                }
            }) { IsBackground = true, Name = "ReplayFinalise" };
            worker.Start();
        }

        private static void Abandon()
        {
            var output = _out;
            _out = null;
            try { output?.Dispose(); } catch { }
        }

        private static string SafeLuaHash()
        {
            try { return FilesCache.ComputeLuaHashString(); }
            catch { return null; }
        }

        private static string Sanitise(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray();
            return new string(chars);
        }
    }
}
