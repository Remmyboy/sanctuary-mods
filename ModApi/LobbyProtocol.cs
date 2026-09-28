using System;
using System.Collections.Generic;
using EM.Network;
using Newtonsoft.Json;
using Unity.Collections;

namespace Sanctuary.ModApi
{
    // The lobby's own messages travel on network channel 4 as a packed type
    // byte and then the fields (LobbyManager.BeginMessage). The game's
    // handlers switch on that byte with no default case, so a player without
    // this API silently drops a type it doesn't know: these messages are
    // invisible to vanilla players, in either direction.
    //
    // Every message here is: type byte, magic, protocol version, JSON.
    internal static class LobbyProtocol
    {
        internal const byte Hello = 0xF0;   // client -> host: "I can take mods"
        internal const byte Report = 0xF1;  // client -> host: what I have of the selection
        internal const byte ModSet = 0xF8;  // host -> client: the selection
        internal const byte Status = 0xF9;  // host -> client: everyone's state

        private const string Magic = "SSMF";
        internal const int ProtoVersion = 1;
        private const int MaxJson = 64 * 1024;

        internal static bool IsOurs(byte type) => type == Hello || type == Report || type == ModSet || type == Status;

        /// Reads the type byte without consuming anything the game needs.
        internal static bool TryPeekType(NativeArray<byte> payload, out byte type)
        {
            var offset = 0;
            try { return NetworkUtils.DeserializeStruct(payload, ref offset, out type); }
            catch { type = 0; return false; }
        }

        internal static bool TryRead<T>(NativeArray<byte> payload, out T message) where T : class
        {
            message = null;
            try
            {
                var offset = 0;
                if (!NetworkUtils.DeserializeStruct(payload, ref offset, out byte _)) return false;
                if (!NetworkUtils.Deserialize(payload, ref offset, out string magic) || magic != Magic) return false;
                if (!NetworkUtils.DeserializeStruct(payload, ref offset, out int version) || version < 1) return false;
                if (!NetworkUtils.Deserialize(payload, ref offset, out string json) || json == null || json.Length > MaxJson) return false;
                message = JsonConvert.DeserializeObject<T>(json);
                return message != null;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Ignored a malformed mod message: {e.Message}");
                return false;
            }
        }

        private static NativeList<byte> Build(byte type, object body)
        {
            var json = JsonConvert.SerializeObject(body);
            var list = new NativeList<byte>(Allocator.Temp);
            NetworkUtils.Pack(list, type);
            NetworkUtils.Pack(list, Magic);
            NetworkUtils.Pack(list, ProtoVersion);
            NetworkUtils.Pack(list, json);
            return list;
        }

        /// Client to host, exactly as the game's SendToServer does it.
        internal static void SendToHost(byte type, object body)
        {
            ref var data = ref NetworkManager.ClientData.Data;
            if (!data.isCreated) return;
            var list = Build(type, body);
            try { data.SendBytes(data.serverEndpoint, 4, list.AsArray()); }
            finally { list.Dispose(); }
        }

        /// Host to one player, as the game's SendToPlayer does it.
        internal static void SendToPlayer(PlayerID player, byte type, object body)
        {
            ref var data = ref NetworkManager.HostData.Data;
            if (!data.isCreated || !data.connections.ContainsKey(player)) return;
            var list = Build(type, body);
            try { data.SendBytes(player, 4, list.AsArray()); }
            finally { list.Dispose(); }
        }

        /// Host to every human in the lobby.
        internal static void Broadcast(byte type, object body)
        {
            var state = LobbyManager.hostState;
            if (state == null) return;
            ref var data = ref NetworkManager.HostData.Data;
            if (!data.isCreated) return;
            var list = Build(type, body);
            try
            {
                foreach (var p in state.players)
                {
                    if (p.type != EM.Core.PlayerType.Player && p.type != EM.Core.PlayerType.Observer) continue;
                    if (data.connections.ContainsKey(p.id)) data.SendBytes(p.id, 4, list.AsArray());
                }
            }
            finally { list.Dispose(); }
        }
    }

    // ---- message bodies --------------------------------------------------
    // Everything in here came off the network: lengths are capped and every
    // id is only ever looked up among the local catalog's mods, never used to
    // build a path.

    internal sealed class HelloMsg
    {
        public int api = LobbyProtocol.ProtoVersion;
        public string version = ModApiPlugin.Version;
    }

    internal sealed class WireMod
    {
        public string id;
        public string name;
        public string version;
        public string hash;
        public string url;
        /// The host's option values, key to canonical value; null for a mod
        /// without options.
        public Dictionary<string, string> options;
    }

    internal sealed class ModSetMsg
    {
        public int rev;
        public bool locked;
        public List<WireMod> mods = new List<WireMod>();
        /// The host's Lua hash with these mods applied.
        public string luaHash;
    }

    internal sealed class ReportedMod
    {
        public string id;
        /// ok, missing or different.
        public string state;
        public string version;
        public string hash;
    }

    internal sealed class ReportMsg
    {
        public int rev;
        public List<ReportedMod> mods = new List<ReportedMod>();
        public string luaHash;
        public bool applied;
    }

    internal sealed class PlayerStatusMsg
    {
        public string id;
        public string name;
        /// ok, vanilla, pending or problem.
        public string state;
        public string detail;
    }

    internal sealed class StatusMsg
    {
        public int rev;
        public bool ready;
        public string reason;
        public List<PlayerStatusMsg> players = new List<PlayerStatusMsg>();
    }
}
