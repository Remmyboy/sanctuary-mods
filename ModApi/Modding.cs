using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using EM.Network;

namespace Sanctuary.ModApi
{
    /// The front door for mod authors: where your mod is, what state the game
    /// is in, and which gameplay mods are live. See also <see cref="ModEvents"/>,
    /// <see cref="ModLua"/>, <see cref="Lobby"/> and <see cref="ModCatalog"/>.
    public static class Modding
    {
        /// The API's version, "major.minor.patch". Mods built against 1.x
        /// run on any 1.y.
        public const string ApiVersion = ModApiPlugin.Version;

        /// Your mod's catalog entry: its folder, manifest and files. Pass
        /// your plugin (`this`). Null if the plugin wasn't loaded from
        /// SanctuaryMods (for example, BepInEx loaded it from plugins\).
        public static ModInfo Self(BaseUnityPlugin plugin) => plugin == null ? null : ModOf(plugin.GetType());

        /// The catalog entry for the mod a hot-loaded plugin type came from.
        public static ModInfo ModOf(Type pluginType)
        {
            if (pluginType == null) return null;
            var path = LoaderBridge.PathOf(pluginType);
            if (string.IsNullOrEmpty(path)) return null;
            var folder = Path.GetDirectoryName(path);
            // A DLL in a subfolder (bin\) still belongs to the mod above it.
            while (!string.IsNullOrEmpty(folder))
            {
                var mod = ModCatalog.FindByFolder(folder);
                if (mod != null) return mod;
                var parent = Path.GetDirectoryName(folder);
                if (parent == null || string.Equals(Path.GetFullPath(parent).TrimEnd('\\'), Path.GetFullPath(ModCatalog.ModsRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
                folder = parent;
            }
            return null;
        }

        /// Your mod's folder, for data files shipped beside the DLL. Null
        /// as for <see cref="Self"/>.
        public static string FolderOf(BaseUnityPlugin plugin) => Self(plugin)?.Folder;

        /// In a lobby, or a match started from one.
        public static bool InLobby => LobbyManager.IsInLobby;

        /// This machine hosts the lobby, and so runs the simulation.
        public static bool IsHost => LobbyManager.IsCurrentUserHost();

        /// A match (not a replay) is loading or running.
        public static bool InMatch => LobbyManager.IsInLobby && LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby;

        /// Watching a replay.
        public static bool InReplay => NetworkManager.IsReplayPlayback;

        /// The gameplay mods whose files are live on this machine right now,
        /// in apply order. Empty means vanilla.
        public static IReadOnlyList<ModInfo> ActiveGameplayMods => Overlay.Applied;

        /// True when the given gameplay mod is live.
        public static bool IsActive(string modId) => Overlay.Applied.Any(m => m.Id == modId);

        /// The game's Lua hash with no gameplay mods, and as it is now.
        public static string VanillaLuaHash => Overlay.VanillaHash;
        public static string CurrentLuaHash => Overlay.CurrentHash;

        /// Gameplay mods pre-picked when this player hosts.
        public static IReadOnlyList<string> DefaultSelection => ModApiPlugin.DefaultSelection;

        public static void SetDefaultSelection(IEnumerable<string> ids) => ModApiPlugin.SetDefaultSelection(ids);
    }
}
