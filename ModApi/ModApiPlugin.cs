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
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static bool MatchWasUnderway;

        private static ConfigEntry<string> _cfgDefaultSelection;
        private Harmony _harmony;
        private float _scanAccum;

        /// Gameplay mods selected when you host a lobby, in apply order.
        internal static IReadOnlyList<string> DefaultSelection =>
            (_cfgDefaultSelection?.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(ValidId).Distinct().ToList();

        internal static void SetDefaultSelection(IEnumerable<string> ids)
        {
            if (_cfgDefaultSelection != null) _cfgDefaultSelection.Value = string.Join(";", ids.Where(ValidId).Distinct());
        }

        internal static bool ValidId(string id) => ModManifest.IsValidId(id);

        private void Awake()
        {
            Log = Logger;
            _cfgDefaultSelection = Config.Bind("Lobby", "DefaultSelection", "",
                "Gameplay mod ids (semicolon-separated, in apply order) picked when you host a lobby. " +
                "Ladder lobbies always start vanilla.");

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
