using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    // has its plugins destroyed on the next poll. A rebuild that fails to load
    // leaves the copy already running alone. Two DLLs carrying the same
    // plugin GUID run only the first; the second waits until the first goes.
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
    [BepInPlugin(LoaderGuid, "Sanctuary Mod Loader", "1.4.1")]
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
            // Not started because the same GUID runs from another DLL.
            public bool HeldAsTwin;
        }

        /// One search of the folder for DLLs, and the folders it couldn't read.
        private sealed class Search
        {
            public readonly List<string> Dlls = new List<string>();
            public readonly List<string> Problems = new List<string>();
        }

        // Libraries the game or BepInEx already has. An author who ships one
        // beside their DLL would otherwise get a second, renamed copy loaded,
        // which breaks both.
        private static readonly string[] LibraryDlls =
        {
            "Sanctuary.ModApi.dll", "0Harmony.dll", "0Harmony20.dll", "HarmonyXInterop.dll",
            "Mono.Cecil.dll", "MonoMod.Utils.dll", "MonoMod.RuntimeDetour.dll", "Newtonsoft.Json.dll",
        };

        private static LoaderPlugin _instance;

        private string _modsDir;
        private readonly Dictionary<string, DateTime> _loadedStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Managed>> _live = new Dictionary<string, List<Managed>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _loadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeGameplayFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skippedLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _deferredLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _problemsLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float _pollAccum;
        /// The DLLs found by the last search of the folder. Each second only
        /// these are checked for changes; the whole folder (hundreds of files,
        /// mostly Lua) is searched for new ones every few seconds, on a
        /// worker thread, so the search never stalls a frame. (A
        /// FileSystemWatcher is no cheaper here: the game's Mono implements
        /// it on Windows as a thread searching the whole tree every 750 ms.)
        private List<string> _knownDlls;
        private float _discoverAccum;
        private const float DiscoverEvery = 5f;
        // A finished background search, waiting for the next poll to take it.
        private Search _found;
        private volatile bool _searching;

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
            _discoverAccum += Time.unscaledDeltaTime;
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

        /// The mod folder a DLL belongs to, found exactly as the Mod API's
        /// catalog finds mods, or the two would disagree about which folder
        /// the lobby picked: the top-level folder under SanctuaryMods when it
        /// has a mod.json; else the outermost folder with a mod.json up to
        /// three levels inside it, on the way down to the DLL (a mod extracted
        /// inside a folder of its own, one of a pack); else the top-level
        /// folder. The root itself for a loose DLL.
        private string ModFolderOf(string dllPath)
        {
            var full = Path.GetFullPath(dllPath);
            var parts = full.Substring(_modsDir.Length).TrimStart('\\', '/').Split('\\', '/');
            if (parts.Length < 2) return _modsDir;
            var top = Path.Combine(_modsDir, parts[0]);
            try
            {
                if (File.Exists(Path.Combine(top, "mod.json"))) return top;
                var d = top;
                for (var depth = 1; depth <= 3 && depth < parts.Length - 1; depth++)
                {
                    if (parts[depth].StartsWith(".")) break;
                    d = Path.Combine(d, parts[depth]);
                    if (File.Exists(Path.Combine(d, "mod.json"))) return d;
                }
            }
            catch { /* a folder deleted or unreadable mid-check: the top-level folder stands in, as for no mod.json */ }
            return top;
        }

        private bool IsGameplayDll(string dllPath)
        {
            var folder = ModFolderOf(dllPath);
            if (string.Equals(folder, _modsDir, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var manifest = Path.Combine(folder, "mod.json");
                return File.Exists(manifest) && ManifestSaysGameplay(File.ReadAllText(manifest));
            }
            catch { return false; } // unreadable or not JSON: the Mod API reads it as no kind, a UI mod, too
        }

        /// The manifest's own top-level "kind", read as the Mod API reads it.
        /// A method of its own so that, were Newtonsoft ever missing, the
        /// failure lands in IsGameplayDll's catch rather than the load.
        private static bool ManifestSaysGameplay(string json)
        {
            var kind = Newtonsoft.Json.Linq.JObject.Parse(json)["kind"];
            return kind != null && kind.Type == Newtonsoft.Json.Linq.JTokenType.String &&
                   string.Equals(((string)kind).Trim(), "gameplay", StringComparison.OrdinalIgnoreCase);
        }

        private string Relative(string path) =>
            path.StartsWith(_modsDir, StringComparison.OrdinalIgnoreCase) ? path.Substring(_modsDir.Length).TrimStart('\\', '/') : path;

        /// Every DLL under the folder. A folder that can't be read, or goes
        /// mid-search, is skipped rather than ending the search; dot-folders
        /// (.git and the like) and build intermediates (obj, inside a mod's
        /// folder) are left out. Thread-safe: it touches no loader state.
        private static Search FindDlls(string root)
        {
            var search = new Search();
            void Walk(string dir, int depth)
            {
                string[] files, subs;
                try
                {
                    files = Directory.GetFiles(dir, "*.dll");
                    subs = Directory.GetDirectories(dir);
                }
                catch (DirectoryNotFoundException) { return; } // deleted mid-search
                catch (Exception e)
                {
                    search.Problems.Add($"{dir}: {e.Message}");
                    return;
                }
                // "*.dll" also matches longer extensions (Foo.dll_off), which
                // is how people switch a DLL off by hand.
                foreach (var file in files)
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) search.Dlls.Add(file);
                foreach (var sub in subs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || (depth > 0 && string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))) continue;
                    Walk(sub, depth + 1);
                }
            }
            Walk(root, 0);
            return search;
        }

        /// Starts a search on a worker thread; the next poll takes its result.
        private void SearchInBackground()
        {
            if (_searching) return;
            _searching = true;
            var root = _modsDir;
            try
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Interlocked.Exchange(ref _found, FindDlls(root)); }
                    catch { /* FindDlls catches per folder; the next search tries again */ }
                    finally { _searching = false; }
                });
            }
            catch (Exception e)
            {
                _searching = false;
                Interlocked.Exchange(ref _found, FindDlls(root)); // no worker to be had: search here, as before
                Logger.LogWarning($"Background search unavailable ({e.Message}); searching on the main thread.");
            }
        }

        private void LoadChanged(bool force)
        {
            if (!Directory.Exists(_modsDir)) return;

            // One folder per mod is the convention, but a DLL dropped anywhere
            // under SanctuaryMods is picked up — no silent no-shows. Between
            // searches, the DLLs already known are checked; one that has gone
            // drops out at once. At start-up and on F6 the search runs here
            // and now; otherwise a background one hands in its result.
            var search = force || _knownDlls == null ? FindDlls(_modsDir) : Interlocked.Exchange(ref _found, null);
            if (_discoverAccum >= DiscoverEvery)
            {
                _discoverAccum = 0f;
                SearchInBackground();
            }
            List<string> onDisk;
            if (search != null)
            {
                foreach (var problem in search.Problems)
                    if (_problemsLogged.Add(problem)) Logger.LogWarning($"Couldn't search {problem}; DLLs in it aren't loaded.");
                onDisk = new List<string>();
                foreach (var path in search.Dlls)
                {
                    if (!IsLibrary(path)) { onDisk.Add(path); continue; }
                    if (_skippedLibraries.Add(path))
                        Logger.LogWarning($"{Relative(path)}: a library the game already has; not loaded. Mods reference it, they don't ship it.");
                }
                _knownDlls = new List<string>(onDisk);
            }
            else onDisk = _knownDlls.Where(File.Exists).ToList();

            var frozen = GameplayFrozen();

            // A DLL removed from the folder takes its plugins with it.
            var removed = false;
            foreach (var gone in _loadedStamps.Keys.Except(onDisk, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (frozen && _live.TryGetValue(gone, out var was) && was.Any(p => p.Gameplay && p.Instance != null)) continue;
                TearDown(gone);
                _loadedStamps.Remove(gone);
                removed = true;
                Logger.LogInfo($"{Path.GetFileName(gone)} removed; its plugin(s) destroyed.");
            }
            // A second copy of a plugin held back for the one just removed
            // takes its place.
            if (removed) StartHeldTwins();

            // Every changed assembly is loaded before any plugin is created, so
            // the held-back decision sees a Mod Manager arriving in this same
            // pass (at start-up, that is all of them).
            var loaded = new List<string>();
            foreach (var path in onDisk)
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(path); }
                catch (Exception) { continue; } // unreadable for now; the next poll looks again
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

            var heldBack = HeldBackGuids();
            foreach (var path in loaded)
            {
                var started = 0;
                var held = new List<string>();
                var waiting = new List<string>();
                foreach (var plugin in _live[path])
                {
                    if (plugin.Gameplay)
                    {
                        if (_activeGameplayFolders.Contains(ModFolderOf(plugin.Path)) && StartPlugin(plugin)) started++;
                        else if (!plugin.HeldAsTwin) waiting.Add(plugin.Name);
                    }
                    else if (heldBack.Contains(plugin.Guid)) held.Add(plugin.Name);
                    else if (StartPlugin(plugin)) started++;
                }
                var copies = _loadCounts[path];
                Logger.LogInfo($"Hot-loaded {started} plugin(s) from {Path.GetFileName(path)} (built {_loadedStamps[path]:HH:mm:ss} UTC)" +
                               (held.Count > 0 ? $"; held back {string.Join(", ", held)}, switched off on the Mods page" : "") +
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
            // Destroyed now rather than at the end of the frame: the new copy
            // starts in this same pass, and an old OnDestroy running after
            // the new Awake would undo what the new copy just set up (patches
            // under the same Harmony id, statics, Lua globals). Only ever
            // called from the loader's own Awake and Update, never from inside
            // a plugin's callbacks, where destroying it immediately isn't safe.
            foreach (var plugin in plugins)
            {
                if (plugin.Instance != null)
                {
                    try { DestroyImmediate(plugin.Instance); }
                    catch (Exception e) { Logger.LogError($"Stopping {plugin.Name} failed: {e}"); }
                }
                plugin.Instance = null;
            }
            _live.Remove(path);
        }

        /// Loads a fresh copy of the assembly and records its plugin types,
        /// then destroys the previous copy's plugins. A copy that fails to load
        /// leaves the previous one running. Creates nothing: LoadChanged
        /// decides what starts.
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
            catch (UnauthorizedAccessException e)
            {
                // Locked by a virus scanner or a copy in progress, or not
                // ours to read: tried again each second, said once.
                if (_problemsLogged.Add(path)) Logger.LogWarning($"{Relative(path)} can't be read yet ({e.Message}); trying again.");
                return false;
            }
            _problemsLogged.Remove(path);

            // Record the stamp even if the load fails, so a broken build logs
            // one error instead of one per second; it is tried again when the
            // file changes, and F6 retries on demand.
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

                // The new copy is in hand: only now does the old one go.
                TearDown(path);
                _live[path] = plugins;
                _loadCounts[path] = _loadCounts.TryGetValue(path, out var count) ? count + 1 : 1;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"Hot reload of {Path.GetFileName(path)} failed" +
                                (_live.ContainsKey(path) ? "; the copy already running stays" : "") + $": {e}");
                return false;
            }
        }

        /// The switched-off GUIDs to hold back, when the manager can list them.
        private HashSet<string> HeldBackGuids() =>
            ManagerListsHeldBackPlugins()
                ? ReadDisabledGuids()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// The running copy of this plugin's GUID from a different DLL, if
        /// any: two copies of one plugin would both patch the game. A reload
        /// of the same DLL is not a twin.
        private Managed RunningTwin(Managed plugin)
        {
            foreach (var list in _live.Values)
            {
                foreach (var other in list)
                {
                    if (other != plugin && other.Instance != null && other.Guid == plugin.Guid &&
                        !string.Equals(other.Path, plugin.Path, StringComparison.OrdinalIgnoreCase))
                        return other;
                }
            }
            return null;
        }

        /// Second copies held back for a twin that has since gone start now,
        /// unless the Mods page or the lobby says otherwise.
        private void StartHeldTwins()
        {
            HashSet<string> heldBack = null;
            foreach (var plugin in _live.Values.SelectMany(l => l).Where(p => p.HeldAsTwin).ToList())
            {
                if (plugin.Instance != null || RunningTwin(plugin) != null) continue;
                plugin.HeldAsTwin = false;
                if (plugin.Gameplay)
                {
                    if (!_activeGameplayFolders.Contains(ModFolderOf(plugin.Path))) continue;
                }
                else
                {
                    if (heldBack == null) heldBack = HeldBackGuids();
                    if (heldBack.Contains(plugin.Guid)) continue;
                }
                if (StartPlugin(plugin)) Logger.LogInfo($"{plugin.Name} started from {Relative(plugin.Path)}, now that its other copy has gone.");
            }
        }

        // Not "Start": Unity takes a method of that name for its own message and
        // logs "Start() can not take parameters" at every launch.
        private bool StartPlugin(Managed plugin)
        {
            if (plugin.Instance != null) return true;
            var twin = RunningTwin(plugin);
            if (twin != null)
            {
                if (!plugin.HeldAsTwin)
                    Logger.LogWarning($"{Relative(plugin.Path)}: {plugin.Name} ({plugin.Guid}) is already running from {Relative(twin.Path)}, " +
                                      "so this second copy is held back. Keep one of the two.");
                plugin.HeldAsTwin = true;
                return false;
            }
            plugin.HeldAsTwin = false;
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
        // file ([Plugins] Disabled = guid;guid). Read on every pass, so a change
        // made on the page applies to the next load. The loader and the manager
        // are never held back, or nothing could undo it.
        private HashSet<string> ReadDisabledGuids()
        {
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
                    if (eq <= 0 || !string.Equals(line.Substring(0, eq).Trim(), "Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var guid in line.Substring(eq + 1).Split(';'))
                    {
                        if (guid.Trim().Length > 0) guids.Add(guid.Trim());
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't read the Mods page's disabled list ({e.Message}); starting every plugin.");
                guids.Clear();
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
        public static string PathOf(Type type) => _instance == null ? null : _instance.Find(type)?.Path;

        /// True for a type from a gameplay mod's DLL: the lobby starts and
        /// stops it, not the player.
        public static bool IsGameplay(Type type) => _instance != null && (_instance.Find(type)?.Gameplay ?? false);

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
                loader.StartPlugin(plugin);
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
                    if (loader.StartPlugin(plugin)) loader.Logger.LogInfo($"Gameplay mod plugin '{plugin.Name}' started for this lobby.");
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
