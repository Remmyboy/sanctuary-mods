using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace SanctuaryModLoader
{
    // Hot-reload host for every mod DLL under engine\SanctuaryMods. Sanctuary
    // destroys foreign root GameObjects (which is why BepInEx needs
    // HideManagerGameObject and why ScriptEngine's visible host object silently
    // dies), so this loader attaches reloaded plugins to its own gameObject —
    // BepInEx's protected, hidden manager — instead of creating a new one.
    //
    // Mods live outside the BepInEx tree, one folder each, next to the Lua
    // mods the mod API overlays — so a single folder is the whole of a mod,
    // whether it ships a DLL, Lua files, or both. This loader is the one piece
    // that has to sit in BepInEx\plugins, because BepInEx loads it (with the
    // mod API beside it, which mods compile against).
    //
    // Each DLL is watched and reloaded independently about a second after every
    // rebuild; F6 forces a reload of everything. A DLL deleted from the folder
    // has its plugins destroyed on the next poll.
    //
    // Reloading is not unloading. Mono can't remove one assembly from the
    // running AppDomain, so a reload destroys the old plugin components (their
    // OnDestroy undoes what they set up) and loads a fresh copy of the assembly
    // beside the old one. The old copy's code and static state, and anything
    // its OnDestroy failed to undo (static event handlers, threads), stay in
    // the process until the game exits.
    //
    // Two kinds of plugin. A UI mod's DLL is the player's own: it runs unless
    // switched off on the Mods page. A gameplay mod's DLL (mod.json says
    // "kind": "gameplay") belongs to the lobby: it runs only while the lobby
    // host has picked that mod, which the mod API tells the loader through
    // SetActiveGameplayFolders, and it is never reloaded under a running match.
    //
    // The loader is also the registry the Mod Manager reads. A plugin switched
    // off on the Mods page is held back before it is ever created, so none of
    // its code runs, and the manager lists, starts and stops plugins through
    // the static methods at the bottom rather than adding components itself.
    [BepInPlugin(LoaderGuid, "Sanctuary Mod Loader", "1.4.0")]
    [BepInDependency(ModApiGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class LoaderPlugin : BaseUnityPlugin
    {
        private const string LoaderGuid = "com.sanctuarydb.modloader";
        private const string ManagerGuid = "com.sanctuarydb.modmanager";
        private const string ModApiGuid = "com.sanctuarydb.modapi";

        /// One plugin type from a DLL on disk: running, or held back.
        private sealed class Managed
        {
            public Type Type;
            public string Guid;
            public string Name;
            public string Path;
            public bool Gameplay;
            public BaseUnityPlugin Instance;
        }

        // Libraries the game or BepInEx already has. An author who ships one
        // beside their DLL would otherwise get a second, renamed copy loaded,
        // which breaks both.
        private static readonly string[] LibraryDlls =
        {
            "Sanctuary.ModApi.dll", "0Harmony.dll", "0Harmony20.dll", "HarmonyXInterop.dll",
            "Mono.Cecil.dll", "MonoMod.Utils.dll", "MonoMod.RuntimeDetour.dll", "Newtonsoft.Json.dll",
        };

        private static readonly Regex GameplayKind = new Regex("\"kind\"\\s*:\\s*\"gameplay\"", RegexOptions.IgnoreCase);

        private static LoaderPlugin _instance;

        private string _modsDir;
        private readonly Dictionary<string, DateTime> _loadedStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Managed>> _live = new Dictionary<string, List<Managed>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _loadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeGameplayFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skippedLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _deferredLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float _pollAccum;

        private void Awake()
        {
            _instance = this;
            _modsDir = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "SanctuaryMods")).TrimEnd('\\', '/');
            Logger.LogInfo($"Mods loader ready; watching {_modsDir} for mod DLLs (auto-reload on change, F6 forces).");
            LoadChanged(force: true);
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                LoadChanged(force: true);
                return;
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < 1f) return;
            _pollAccum = 0f;
            LoadChanged(force: false);
        }

        /// A match or replay is running: gameplay DLLs stay exactly as they
        /// started, since swapping one would change the simulation mid-game.
        private static bool GameplayFrozen()
        {
            try
            {
                return EM.Network.LobbyManager.IsInLobby &&
                       EM.Network.LobbyManager.lobbyGameStatus != EM.Network.LobbyManager.LobbyGameStatus.lobby ||
                       EM.Network.NetworkManager.IsReplayPlayback;
            }
            catch { return false; }
        }

        private static bool IsLibrary(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                   LibraryDlls.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        }

        /// The mod folder a DLL belongs to: the top-level folder under
        /// SanctuaryMods it sits in (or the root, for a loose DLL).
        private string ModFolderOf(string dllPath)
        {
            var rel = dllPath.Substring(_modsDir.Length).TrimStart('\\', '/');
            var slash = rel.IndexOfAny(new[] { '\\', '/' });
            return Path.GetFullPath(slash < 0 ? _modsDir : Path.Combine(_modsDir, rel.Substring(0, slash))).TrimEnd('\\', '/');
        }

        private bool IsGameplayDll(string dllPath)
        {
            var folder = ModFolderOf(dllPath);
            if (string.Equals(folder, _modsDir, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var manifest = Path.Combine(folder, "mod.json");
                return File.Exists(manifest) && GameplayKind.IsMatch(File.ReadAllText(manifest));
            }
            catch { return false; }
        }

        private void LoadChanged(bool force)
        {
            if (!Directory.Exists(_modsDir)) return;

            // One folder per mod is the convention, but a DLL dropped anywhere
            // under SanctuaryMods is picked up — no silent no-shows.
            var onDisk = new List<string>();
            foreach (var path in Directory.GetFiles(_modsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (!IsLibrary(path)) { onDisk.Add(path); continue; }
                if (_skippedLibraries.Add(path))
                    Logger.LogWarning($"{path.Substring(_modsDir.Length).TrimStart('\\')}: a library the game already has; not loaded. Mods reference it, they don't ship it.");
            }

            var frozen = GameplayFrozen();

            // A DLL removed from the folder takes its plugins with it.
            foreach (var gone in _loadedStamps.Keys.Except(onDisk, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (frozen && _live.TryGetValue(gone, out var was) && was.Any(p => p.Gameplay && p.Instance != null)) continue;
                TearDown(gone);
                _loadedStamps.Remove(gone);
                Logger.LogInfo($"{Path.GetFileName(gone)} removed; its plugin(s) destroyed.");
            }

            // Every changed assembly is loaded before any plugin is created, so
            // the held-back decision sees a Mod Manager arriving in this same
            // pass (at start-up, that is all of them).
            var loaded = new List<string>();
            foreach (var path in onDisk)
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!force && _loadedStamps.TryGetValue(path, out var was) && was == stamp) continue;
                // Gameplay DLLs wait for the match to end before a reload.
                if (frozen && _live.TryGetValue(path, out var running) && running.Any(p => p.Gameplay))
                {
                    if (_deferredLogged.Add(path))
                        Logger.LogInfo($"{Path.GetFileName(path)} changed; it's a gameplay mod, so it reloads when the match ends.");
                    continue;
                }
                _deferredLogged.Remove(path);
                if (TryLoadAssembly(path, stamp)) loaded.Add(path);
            }
            if (loaded.Count == 0) return;

            var vanilla = false;
            var heldBack = ManagerListsHeldBackPlugins()
                ? ReadDisabledGuids(out vanilla)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in loaded)
            {
                var started = 0;
                var held = new List<string>();
                var waiting = new List<string>();
                foreach (var plugin in _live[path])
                {
                    if (plugin.Gameplay)
                    {
                        if (_activeGameplayFolders.Contains(ModFolderOf(plugin.Path)) && Start(plugin)) started++;
                        else waiting.Add(plugin.Name);
                    }
                    else if (vanilla || heldBack.Contains(plugin.Guid)) held.Add(plugin.Name);
                    else if (Start(plugin)) started++;
                }
                var copies = _loadCounts[path];
                Logger.LogInfo($"Hot-loaded {started} plugin(s) from {Path.GetFileName(path)} (built {_loadedStamps[path]:HH:mm:ss} UTC)" +
                               (held.Count > 0 ? $"; held back {string.Join(", ", held)}, " + (vanilla ? "playing vanilla" : "switched off on the Mods page") : "") +
                               (waiting.Count > 0 ? $"; {string.Join(", ", waiting)} is a gameplay mod and starts when a lobby host picks it" : "") +
                               (copies > 1
                                   ? $". Copy {copies} of this assembly this session: earlier copies can't be unloaded and stay in memory until the game exits."
                                   : "."));
            }
        }

        private void TearDown(string path)
        {
            if (!_live.TryGetValue(path, out var plugins)) return;
            // Their OnDestroy handlers drop the Harmony patches they applied.
            foreach (var plugin in plugins)
            {
                if (plugin.Instance != null) Destroy(plugin.Instance);
                plugin.Instance = null;
            }
            _live.Remove(path);
        }

        /// Loads a fresh copy of the assembly and records its plugin types,
        /// destroying the previous copy's plugins first. Creates nothing:
        /// LoadChanged decides what starts.
        private bool TryLoadAssembly(string path, DateTime stamp)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return false; // mid-copy; the poll picks it up next second
            }

            TearDown(path);

            // Record the stamp even if the load fails, so a broken build logs
            // one error instead of one per second; F6 retries on demand.
            _loadedStamps[path] = stamp;

            try
            {
                // Mono caches byte-loaded assemblies by identity, so an
                // unchanged name would silently give us back the old code.
                // Rewrite the assembly name per load (same trick ScriptEngine
                // uses) to force a fresh load every time.
                using (var ms = new MemoryStream())
                {
                    using (var input = new MemoryStream(bytes, false))
                    using (var asmDef = Mono.Cecil.AssemblyDefinition.ReadAssembly(input))
                    {
                        asmDef.Name.Name = $"{asmDef.Name.Name}-{DateTime.UtcNow.Ticks}";
                        asmDef.Write(ms);
                    }
                    bytes = ms.ToArray();
                }

                var assembly = Assembly.Load(bytes);
                _loadCounts[path] = _loadCounts.TryGetValue(path, out var count) ? count + 1 : 1;

                var gameplay = IsGameplayDll(path);
                var plugins = new List<Managed>();
                foreach (var type in GetTypesSafe(assembly)
                             .Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    BepInPlugin meta = null;
                    try { meta = type.GetCustomAttributes(typeof(BepInPlugin), false).OfType<BepInPlugin>().FirstOrDefault(); }
                    catch (Exception e) { Logger.LogWarning($"{type.FullName}: unreadable [BepInPlugin] ({e.Message})."); }
                    // A copy of the loader under SanctuaryMods would load
                    // itself from its own Awake, without end; the API is
                    // loaded by BepInEx and must stay a single copy.
                    if (meta?.GUID == LoaderGuid || meta?.GUID == ModApiGuid)
                    {
                        Logger.LogWarning($"{Path.GetFileName(path)}: {meta.Name} belongs in BepInEx\\plugins, not SanctuaryMods; skipped.");
                        continue;
                    }
                    plugins.Add(new Managed
                    {
                        Type = type, Guid = meta?.GUID ?? type.FullName, Name = meta?.Name ?? type.Name,
                        Path = path, Gameplay = gameplay && meta?.GUID != ManagerGuid,
                    });
                }
                _live[path] = plugins;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"Hot reload of {Path.GetFileName(path)} failed: {e}");
                return false;
            }
        }

        private bool Start(Managed plugin)
        {
            if (plugin.Instance != null) return true;
            try
            {
                plugin.Instance = (BaseUnityPlugin)gameObject.AddComponent(plugin.Type);
            }
            catch (Exception e)
            {
                Logger.LogError($"Starting {plugin.Name} failed: {e}");
                plugin.Instance = null;
            }
            return plugin.Instance != null;
        }

        // A held-back plugin has no component, so only a Mod Manager that reads
        // this registry can list it and switch it back on. An older manager
        // lists components alone; under one of those nothing is held back, and
        // disabled plugins keep the old start-then-stop behaviour.
        private bool ManagerListsHeldBackPlugins() =>
            _live.Values.SelectMany(l => l).Any(p =>
                p.Guid == ManagerGuid &&
                p.Type.GetField("ListsLoaderRegistry", BindingFlags.Public | BindingFlags.Static) != null);

        // The Mods page's switched-off list, straight from the manager's config
        // file ([Plugins] Disabled = guid;guid, and VanillaMode = true to hold
        // back every UI mod). Read on every pass, so a change made on the page
        // applies to the next load. The loader and the manager are never held
        // back, or nothing could undo it.
        private HashSet<string> ReadDisabledGuids(out bool vanilla)
        {
            vanilla = false;
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(Paths.ConfigPath, ManagerGuid + ".cfg");
            try
            {
                if (!File.Exists(path)) return guids;
                var section = "";
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (line.StartsWith("#") || !string.Equals(section, "Plugins", StringComparison.OrdinalIgnoreCase)) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "VanillaMode", StringComparison.OrdinalIgnoreCase))
                    {
                        vanilla = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!string.Equals(key, "Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var guid in value.Split(';'))
                    {
                        if (guid.Trim().Length > 0) guids.Add(guid.Trim());
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't read the Mods page's disabled list ({e.Message}); starting every plugin.");
                guids.Clear();
                vanilla = false;
            }
            guids.Remove(LoaderGuid);
            guids.Remove(ManagerGuid);
            return guids;
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
        }

        // ---- registry, for the Mod Manager and the mod API --------------------
        // Reached by reflection, never a compile-time reference: the manager is
        // hot-loaded under a per-load assembly name and has to keep running
        // against an older loader without these. Only framework, Unity and
        // BepInEx types cross the boundary.

        /// Every plugin type from a DLL currently in SanctuaryMods (its latest
        /// load), running or held back. Types from a deleted DLL, or from a
        /// copy that has since been reloaded, are not in it.
        public static Type[] PluginTypes() =>
            _instance == null
                ? new Type[0]
                : _instance._live.Values.SelectMany(l => l).Select(p => p.Type).ToArray();

        /// The running instance of a type from PluginTypes, or null while it
        /// is held back or switched off.
        public static BaseUnityPlugin InstanceOf(Type type)
        {
            var plugin = _instance == null ? null : _instance.Find(type);
            return plugin != null && plugin.Instance != null ? plugin.Instance : null;
        }

        /// The DLL a type from PluginTypes was loaded from, or null.
        public static string PathOf(Type type) => _instance?.Find(type)?.Path;

        /// True for a type from a gameplay mod's DLL: the lobby starts and
        /// stops it, not the player.
        public static bool IsGameplay(Type type) => _instance?.Find(type)?.Gameplay ?? false;

        /// Starts or destroys a type from PluginTypes and returns its running
        /// instance (null when off). A type the loader no longer manages is
        /// refused and nothing is created, so a deleted DLL can't come back
        /// from memory. Gameplay plugins are refused too: the lobby owns them.
        public static BaseUnityPlugin SetPluginEnabled(Type type, bool enabled)
        {
            var loader = _instance;
            var plugin = loader == null ? null : loader.Find(type);
            if (plugin == null) return null;
            if (plugin.Gameplay) return plugin.Instance != null ? plugin.Instance : null;
            if (enabled)
            {
                loader.Start(plugin);
            }
            else if (plugin.Instance != null)
            {
                Destroy(plugin.Instance); // its OnDestroy drops its Harmony patches
                plugin.Instance = null;
            }
            return plugin.Instance != null ? plugin.Instance : null;
        }

        /// The mod folders (full paths) whose gameplay DLLs should run: the
        /// lobby host's pick. Called by the mod API whenever it changes, and
        /// with nothing when the lobby or match ends. Plugins from other
        /// gameplay folders are stopped.
        public static void SetActiveGameplayFolders(string[] folders)
        {
            var loader = _instance;
            if (loader == null) return;
            loader._activeGameplayFolders.Clear();
            foreach (var f in folders ?? new string[0])
                loader._activeGameplayFolders.Add(Path.GetFullPath(f).TrimEnd('\\', '/'));

            foreach (var plugin in loader._live.Values.SelectMany(l => l).Where(p => p.Gameplay))
            {
                var want = loader._activeGameplayFolders.Contains(loader.ModFolderOf(plugin.Path));
                if (want && plugin.Instance == null)
                {
                    if (loader.Start(plugin)) loader.Logger.LogInfo($"Gameplay mod plugin '{plugin.Name}' started for this lobby.");
                }
                else if (!want && plugin.Instance != null)
                {
                    Destroy(plugin.Instance);
                    plugin.Instance = null;
                    loader.Logger.LogInfo($"Gameplay mod plugin '{plugin.Name}' stopped.");
                }
            }
        }

        private Managed Find(Type type) =>
            type == null ? null : _live.Values.SelectMany(l => l).FirstOrDefault(p => p.Type == type);
    }
}
