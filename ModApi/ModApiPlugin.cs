using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EM.Network;
using HarmonyLib;

namespace Sanctuary.ModApi
{
    // The stable core of the mod framework. BepInEx loads it from plugins\
    // under a fixed identity, so it never hot-reloads, and that is the point:
    // mods reference it, and it holds what must outlive their reloads (and the
    // Mod Manager's) — the gameplay-mod overlay a running match reads, and
    // the lobby protocol that agrees on it.
    [BepInPlugin(Guid, "Sanctuary Mod API", Version)]
    public class ModApiPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.sanctuarydb.modapi";
        public const string Version = "1.2.1";

        private static ManualLogSource _log;

        /// The plugin's logger, or a stand-in when a mod calls the API before
        /// (or without) the plugin starting: the DLL can be loaded as a mere
        /// reference, e.g. a newer Mod Manager dropped in beside a game that
        /// started without the API, and a null logger then turned a harmless
        /// warning into a NullReferenceException.
        internal static ManualLogSource Log
        {
            get => _log ?? (_log = BepInEx.Logging.Logger.CreateLogSource("Sanctuary Mod API"));
            private set => _log = value;
        }
        internal static bool MatchWasUnderway;

        private Harmony _harmony;
        private float _scanAccum;

        internal static bool ValidId(string id) => ModManifest.IsValidId(id);

        private static ConfigEntry<string> _cfgOptions;

        /// The option values this player last hosted a mod with, or null.
        internal static IReadOnlyDictionary<string, string> RememberedOptions(string modId)
        {
            try
            {
                var all = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(_cfgOptions?.Value ?? "");
                return all != null && all.TryGetValue(modId, out var v) ? v : null;
            }
            catch { return null; }
        }

        internal static void RememberOptions(string modId, IReadOnlyDictionary<string, string> values)
        {
            if (_cfgOptions == null || !ValidId(modId)) return;
            Dictionary<string, Dictionary<string, string>> all = null;
            try { all = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(_cfgOptions.Value ?? ""); }
            catch { }
            all = all ?? new Dictionary<string, Dictionary<string, string>>();
            all[modId] = values.ToDictionary(kv => kv.Key, kv => kv.Value);
            // Sorted, so unchanged values make an unchanged string and the
            // config file isn't rewritten for nothing.
            var text = Newtonsoft.Json.JsonConvert.SerializeObject(
                all.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary(
                    m => m.Key, m => m.Value.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value)));
            if (text != _cfgOptions.Value) _cfgOptions.Value = text;
        }

        private void Awake()
        {
            Log = Logger;
            // Every lobby starts with no gameplay mods; the host switches
            // them on. An earlier test build picked some by default: drop
            // that setting.
            var oldDefaults = Config.Bind("Lobby", "DefaultSelection", "", "");
            Config.Remove(oldDefaults.Definition);
            _cfgOptions = Config.Bind("Lobby", "Options", "",
                "The gameplay mod options you last hosted with, as JSON ({\"mod.id\": {\"key\": \"value\"}}). " +
                "Set them in the lobby's Mods panel; they come back when you switch the mod on again.");
            Config.Save();

            try { System.IO.Directory.CreateDirectory(ModCatalog.ModsRoot); }
            catch (Exception e) { Log.LogWarning($"Could not create {ModCatalog.ModsRoot}: {e.Message}"); }
            ModCatalog.Rescan();

            _harmony = new Harmony(Guid);
            GamePatches.Apply(_harmony);
            Replays.Apply(_harmony);
            LobbyManager.OnLobbyStatusChanged += OnLobbyStatusChanged;

            Log.LogInfo($"Mod API {Version} ready: {ModCatalog.Mods.Count} mod folder(s), " +
                        $"{ModCatalog.GameplayMods.Count()} gameplay. Gameplay mods are picked by the lobby host; outside a lobby the game is vanilla.");
        }

        private void OnDestroy()
        {
            // Only at game exit: the API never hot-reloads.
            LobbyManager.OnLobbyStatusChanged -= OnLobbyStatusChanged;
            _harmony?.UnpatchSelf();
        }

        private static void OnLobbyStatusChanged(LobbyManager.LobbyGameStatus status)
        {
            try
            {
                if (status != LobbyManager.LobbyGameStatus.loading) return;
                MatchWasUnderway = true;
                Lobby.ClientMatchLoading();
                ModEvents.RaiseMatchStarting();
            }
            catch (Exception e) { Log.LogError($"Match start: {e}"); }
        }

        private void Update()
        {
            // The cache is built in a BeforeSceneLoad callback; the vanilla
            // hash is taken once it exists, before anything is overlaid.
            if (Overlay.CacheReady && Overlay.VanillaHash.Length == 0) Overlay.CaptureVanillaHash();

            if (Overlay.CacheWasRebuilt)
            {
                Log.LogWarning("The game rebuilt its file cache; putting the gameplay mods back.");
                Overlay.Reestablish();
            }

            // Mods are hot: added, edited and removed folders are seen within
            // a couple of seconds. Only folders whose files changed are hashed
            // again. A running match keeps the files it started with.
            _scanAccum += UnityEngine.Time.unscaledDeltaTime;
            if (_scanAccum >= 2f)
            {
                _scanAccum = 0f;
                try { ModCatalog.Rescan(); }
                catch (Exception e) { Log.LogWarning($"Mod rescan failed: {e.Message}"); }
            }

            try { Lobby.Tick(); }
            catch (Exception e) { Log.LogError($"Lobby mods: {e}"); }
        }
    }
}
