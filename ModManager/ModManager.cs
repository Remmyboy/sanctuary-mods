using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Unity.Collections;
using UnityEngine;

namespace SanctuaryHud
{
    // Standalone mod manager: overlays Lua mod files into the game's in-memory
    // FilesCache. The lobby hash (ComputeLuaHash) and every Lua VM read from
    // that cache, not from disk — so an overlay applied in the main menu takes
    // effect when the next match's VMs start, and the lobby hash shifts with
    // it. Players with the same mods enabled produce the same hash and can
    // play together; anyone else is refused at join, which makes mismatched
    // mod sets unable to desync a game.
    //
    // Mods live in <engine>\SanctuaryMods\<ModName>\, each mirroring the
    // LJ\lua tree (e.g. ExampleMod\common\colors.lua). Only *.lua and *.santp
    // are applied. NOTE: .santp files are loaded by the game but NOT covered
    // by the lobby hash, so template mods must be coordinated manually.
    //
    // Toggling is blocked while in a lobby or match: the VMs snapshot the
    // cache at match launch, and swapping content under a live session would
    // change the hash out from under the lobby's compatibility check.
    [BepInPlugin("com.sanctuarydb.modmanager", "Sanctuary Mod Manager", "0.5.0")]
    public class ModManagerPlugin : BaseUnityPlugin
    {
        /// Tells ModLoader 1.3+ that this manager lists the loader's registry,
        /// so switched-off plugins can be held back before they start: they
        /// still appear on the page and can be switched on again. The loader
        /// looks for the field by name; its value is never read.
        public const bool ListsLoaderRegistry = true;

        private static BepInEx.Logging.ManualLogSource _log;

        internal class ModEntry
        {
            public string Name;
            public string Dir;
            public int LuaCount;
            public int SantpCount;
            public bool Enabled;
        }

        private readonly List<ModEntry> _mods = new List<ModEntry>();

        // ---- what the menu page reads and drives ---------------------------
        internal IReadOnlyList<ModEntry> Mods => _mods;
        internal IReadOnlyList<PluginEntry> Plugins => _plugins;
        internal string HashNow => _hashNow;
        internal string HashVanilla => _hashVanilla;
        internal bool Locked => InLobbyOrMatch();

        internal void SetModEnabled(ModEntry mod, bool enabled)
        {
            if (mod.Enabled == enabled) return;
            if (Locked) return;
            mod.Enabled = enabled;
            Reapply();
        }

        internal void Rescan()
        {
            ScanMods();
            if (!Locked) Reapply();
            RefreshPlugins(applyDisabled: false);
        }

        internal void OpenModsFolder() => Application.OpenURL("file:///" + ModsRoot.Replace('\\', '/'));

        // ---- C# plugin toggles --------------------------------------------
        // Every plugin on this same hidden manager GameObject: the ones the
        // hot-reload loader manages, read from its registry, and any BepInEx
        // loaded itself. Switching one off destroys its component (OnDestroy
        // unpatches Harmony) and switching it on creates it again. That is a
        // component teardown, not an unload: .NET can't unload an assembly
        // from the running game, so the plugin's code and static state stay in
        // memory until the game exits. With ModLoader 1.3+ a switched-off
        // plugin is never started at all, and one whose DLL is deleted leaves
        // the list. C# plugins never enter the Lua hash, so unlike Lua mods
        // these are safe to toggle any time, even mid-match.
        internal class PluginEntry
        {
            public string Guid;
            public string Name;
            public Type Type;
            public BaseUnityPlugin Instance;
            // The loader created it, so the loader starts and stops it.
            public bool FromLoader;
            // Switched off on this page, as opposed to destroyed from outside
            // (an older loader tearing down a deleted DLL).
            public bool SwitchedOff;
            // The last instance's ConfigFile, kept after it is unloaded: the
            // entries stay bound and writes still go to the file, which the
            // next instance reads on load, so settings are editable while
            // the mod is off.
            public ConfigFile Config;
            public bool Enabled => Instance != null;
        }

        private readonly List<PluginEntry> _plugins = new List<PluginEntry>();

        private ConfigEntry<string> _cfgDisabledPlugins;
        private float _pluginScanAccum = 999f; // scan on the first Update

        // ---- injection tracking; rebuilt from scratch on every reapply ----
        // Original cache entries we replaced (never our own arrays), added keys
        // that had no original, and every array we allocated (disposed on
        // restore — including arrays a later mod's file overwrote in the dict).
        private readonly Dictionary<string, NativeArray<byte>> _pristine =
            new Dictionary<string, NativeArray<byte>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<NativeArray<byte>> _allocated = new List<NativeArray<byte>>();
        // The dictionary instance we injected into. CreateFileCache reassigns
        // the whole dictionary, so if the game rebuilds the cache our entries
        // are already gone and restoring into the new one would corrupt it.
        private Dictionary<string, NativeArray<byte>> _appliedToDict;

        // Folder listings come from a separate private index built from disk;
        // added files in new folders need their folder chain registered there
        // or Lua directory enumeration won't see them.
        private static readonly FieldInfo DirIndexField = typeof(EM.Lua.FilesCache)
            .GetField("directoryToSubFolderNames", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo RebuildDirIndexMi = typeof(EM.Lua.FilesCache)
            .GetMethod("RebuildDirectoryIndex", BindingFlags.NonPublic | BindingFlags.Static);

        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<string> _cfgEnabled;

        // The UI: a "Mods" entry in the front menu's sidebar opening a page
        // built from the game's own settings screen. The hotkey opens the
        // same page full-screen during a match.
        private ModsPage _page;
        private string _hashVanilla = "";
        private string _hashNow = "";
        private bool _pendingApply;

        private static string ModsRoot => Path.Combine(Paths.GameRootPath, "SanctuaryMods");
        private static string LuaRoot => Path.GetFullPath(Path.Combine(Paths.GameRootPath, "LJ", "lua"));

        private void Awake()
        {
            _log = Logger;
            _cfgToggleKey = Config.Bind("UI", "ToggleKey", KeyCode.F8, "Key that opens/closes the Mods page, in the front menu or during a match.");
            _cfgEnabled = Config.Bind("Mods", "Enabled", "",
                "Semicolon-separated mod folder names (under SanctuaryMods) applied at startup.");
            _cfgDisabledPlugins = Config.Bind("Plugins", "Disabled", "",
                "Semicolon-separated GUIDs of C# plugins switched off on the Mods page. ModLoader 1.3+ never starts " +
                "these; an older loader starts them and the manager stops them straight away.");

            try { Directory.CreateDirectory(ModsRoot); }
            catch (Exception e) { _log.LogWarning($"Could not create {ModsRoot}: {e.Message}"); }

            ScanMods();
            _page = new ModsPage(this, _log);
            // The cache is built in a BeforeSceneLoad callback that may not
            // have run yet (and would wipe an early overlay by reassigning the
            // dictionary), so the first apply waits for it in Update.
            _pendingApply = true;
            _log.LogInfo($"Mod manager ready: {_mods.Count} mod(s) in {ModsRoot}, " +
                         $"{_mods.Count(m => m.Enabled)} enabled. Mods is in the front menu's sidebar ({_cfgToggleKey.Value} also opens it).");
        }

        private void OnDestroy()
        {
            try { _page?.Destroy(); }
            catch (Exception e) { _log.LogWarning($"Mods page teardown failed: {e.Message}"); }
            // Hot reload tears us down; put the cache back so the next copy
            // starts from vanilla (its config re-applies the enabled set).
            try { RestoreAll(); }
            catch (Exception e) { _log.LogWarning($"Mod manager restore on unload failed: {e.Message}"); }
        }

        /// The hotkey: opens or closes the page from the front menu or a
        /// match, nothing from the lobby or loading screens.
        internal void ToggleUi()
        {
            if (_page != null && _page.CanOpen) _page.Toggle();
        }

        // ---- mod discovery -------------------------------------------------

        private void ScanMods()
        {
            var enabled = new HashSet<string>(
                (_cfgEnabled.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            _mods.Clear();
            if (!Directory.Exists(ModsRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(ModsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".")) continue;
                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
                var luaCount = files.Count(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                var santpCount = files.Count(f => f.EndsWith(".santp", StringComparison.OrdinalIgnoreCase));

                // Mod folders now hold UI mods (a DLL, loaded by the loader)
                // as well as Lua overlays, and a mod may ship both. Only the
                // Lua half belongs in this list.
                if (luaCount == 0 && santpCount == 0) continue;

                _mods.Add(new ModEntry
                {
                    Name = name,
                    Dir = dir,
                    LuaCount = luaCount,
                    SantpCount = santpCount,
                    Enabled = enabled.Contains(name),
                });
            }
        }

        // ---- overlay apply/restore ----------------------------------------

        private static bool InLobbyOrMatch()
        {
            try { return EM.Network.LobbyManager.IsInLobby; }
            catch { return false; }
        }

        private void Reapply()
        {
            var cache = EM.Lua.FilesCache.pathToFileContents;
            if (cache == null) { _pendingApply = true; return; }

            RestoreAll();
            _appliedToDict = cache;

            var applied = 0;
            foreach (var mod in _mods.Where(m => m.Enabled))
            {
                try
                {
                    applied += ApplyMod(mod, cache);
                }
                catch (Exception e)
                {
                    _log.LogError($"Applying mod '{mod.Name}' failed part-way: {e.Message}");
                }
            }

            RefreshHash();
            _cfgEnabled.Value = string.Join(";", _mods.Where(m => m.Enabled).Select(m => m.Name));
            var summary = applied > 0
                ? $"{applied} file(s) overlaid from {_mods.Count(m => m.Enabled)} mod(s)."
                : "No mods applied (vanilla).";
            _log.LogInfo($"Mod overlay: {summary} Lua hash {_hashNow}.");
        }

        /// Returns the number of files overlaid. Later mods win on conflicts
        /// (list order is alphabetical), and the loser's array stays tracked
        /// in _allocated for disposal at the next restore.
        private int ApplyMod(ModEntry mod, Dictionary<string, NativeArray<byte>> cache)
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(mod.Dir, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".santp", StringComparison.OrdinalIgnoreCase)) continue;

                var rel = file.Substring(mod.Dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(LuaRoot, rel));
                // Symlinks or ".." in a mod folder must not reach outside LJ\lua.
                if (!target.StartsWith(LuaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning($"Mod '{mod.Name}': skipped '{rel}' (resolves outside LJ\\lua).");
                    continue;
                }

                var arr = new NativeArray<byte>(File.ReadAllBytes(file), Allocator.Persistent);
                _allocated.Add(arr);

                if (cache.TryGetValue(target, out var existing))
                {
                    // Stash only the true original: a key another mod already
                    // touched has its pristine copy (or none, if added) stashed.
                    if (!_pristine.ContainsKey(target) && !_added.Contains(target))
                        _pristine[target] = existing;
                }
                else
                {
                    _added.Add(target);
                    RegisterFolders(rel);
                }
                cache[target] = arr;
                count++;
            }
            return count;
        }

        private void RestoreAll()
        {
            if (_appliedToDict != null && ReferenceEquals(_appliedToDict, EM.Lua.FilesCache.pathToFileContents))
            {
                foreach (var kv in _pristine) _appliedToDict[kv.Key] = kv.Value;
                foreach (var key in _added) _appliedToDict.Remove(key);
                // Drop our folder registrations by rebuilding the index from disk.
                try { RebuildDirIndexMi?.Invoke(null, null); }
                catch (Exception e) { _log.LogWarning($"Directory index rebuild failed: {e.Message}"); }
            }
            // If the game reassigned the cache since we applied, our entries
            // went with the old dictionary — nothing references these arrays.
            foreach (var arr in _allocated)
            {
                try { if (arr.IsCreated) arr.Dispose(); } catch { }
            }
            _pristine.Clear();
            _added.Clear();
            _allocated.Clear();
            _appliedToDict = null;
        }

        private static void RegisterFolders(string rel)
        {
            if (!(DirIndexField?.GetValue(null) is Dictionary<string, HashSet<string>> dirIndex)) return;
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = LuaRoot;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!dirIndex.TryGetValue(parent, out var set))
                    dirIndex[parent] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(parts[i]);
                parent = Path.Combine(parent, parts[i]);
            }
        }

        // ---- C# plugin load/unload ----------------------------------------

        private void RefreshPlugins(bool applyDisabled)
        {
            var disabled = DisabledGuids();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var registry = LoaderRegistry.Resolve(_log);

            // The loader's plugins, from DLLs on disk right now, running or
            // held back. The loader held the switched-off ones back before
            // they started, so there is nothing to apply after the fact.
            var fromLoader = new HashSet<Type>();
            if (registry != null)
            {
                foreach (var type in registry.PluginTypes())
                {
                    fromLoader.Add(type);
                    var entry = Track(type, seen);
                    if (entry == null) continue;
                    entry.FromLoader = true;
                    entry.Instance = registry.InstanceOf(type);
                    entry.SwitchedOff = entry.Instance == null;
                    if (entry.Instance != null && entry.Instance.Config != null) entry.Config = entry.Instance.Config;
                }
            }

            // Everything else on the manager object: plugins BepInEx loaded
            // itself, or every plugin under a loader older than 1.3. These can
            // only be switched off once they are already running.
            foreach (var comp in GetComponents<BaseUnityPlugin>())
            {
                if (fromLoader.Contains(comp.GetType())) continue;
                var entry = Track(comp.GetType(), seen);
                if (entry == null) continue;
                entry.FromLoader = false;
                entry.Instance = comp;
                entry.SwitchedOff = false;
                if (comp.Config != null) entry.Config = comp.Config;
                if (applyDisabled && disabled.Contains(entry.Guid)) SetPluginEnabled(entry, false, persist: false);
            }

            // An entry nothing vouched for this pass is gone. The registry is
            // the whole truth for the loader's plugins, so a deleted DLL's
            // entry goes, Type and all, and can't be created again from
            // memory. Anything else stays only while this page has it switched
            // off; otherwise it was destroyed from outside.
            _plugins.RemoveAll(p => !seen.Contains(p.Guid) && (p.FromLoader || !p.SwitchedOff));
        }

        /// The entry for a plugin type, created on first sight and marked
        /// seen. Null for the loader and the manager (switching either off
        /// would leave nothing to switch it back on), for a type without
        /// [BepInPlugin], and for a GUID already seen this pass.
        private PluginEntry Track(Type type, HashSet<string> seen)
        {
            var meta = type.GetCustomAttribute<BepInPlugin>();
            if (meta == null) return null;
            if (meta.GUID == "com.sanctuarydb.modloader" || meta.GUID == "com.sanctuarydb.modmanager") return null;
            if (!seen.Add(meta.GUID)) return null;
            var entry = _plugins.FirstOrDefault(p => string.Equals(p.Guid, meta.GUID, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new PluginEntry { Guid = meta.GUID, Name = meta.Name };
                _plugins.Add(entry);
            }
            entry.Type = type;
            return entry;
        }

        private HashSet<string> DisabledGuids() => new HashSet<string>(
            (_cfgDisabledPlugins.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        /// Everything the mod bound: from the running instance, or from the
        /// ConfigFile its last instance left behind while it is off.
        internal static ConfigEntryBase[] ConfigEntriesOf(PluginEntry plugin)
        {
#pragma warning disable CS0618 // GetConfigEntries is obsolete, but the Values replacement is not in this BepInEx.
            return (plugin.Instance?.Config ?? plugin.Config)?.GetConfigEntries() ?? Array.Empty<ConfigEntryBase>();
#pragma warning restore CS0618
        }

        internal void SetPluginEnabled(PluginEntry entry, bool enable, bool persist = true)
        {
            var registry = entry.FromLoader ? LoaderRegistry.Resolve(_log) : null;
            if (registry != null)
            {
                // The loader starts and stops its own plugins, and refuses a
                // type it no longer has a DLL for.
                var wasRunning = entry.Instance != null;
                entry.Instance = registry.SetPluginEnabled(entry.Type, enable);
                if (entry.Instance != null && entry.Instance.Config != null) entry.Config = entry.Instance.Config;
                if (enable && entry.Instance == null)
                    _log.LogWarning($"Plugin '{entry.Name}' could not be started; is its DLL still in SanctuaryMods?");
                else if (wasRunning != (entry.Instance != null))
                    _log.LogInfo($"Plugin '{entry.Name}' {(enable ? "started" : "stopped")}.");
            }
            else if (enable && entry.Instance == null && entry.Type != null)
            {
                try
                {
                    entry.Instance = (BaseUnityPlugin)gameObject.AddComponent(entry.Type);
                    if (entry.Instance.Config != null) entry.Config = entry.Instance.Config;
                    _log.LogInfo($"Plugin '{entry.Name}' started.");
                }
                catch (Exception e)
                {
                    _log.LogError($"Re-adding plugin '{entry.Name}' failed: {e}");
                }
            }
            else if (!enable && entry.Instance != null)
            {
                Destroy(entry.Instance); // its OnDestroy drops its Harmony patches
                entry.Instance = null;
                _log.LogInfo($"Plugin '{entry.Name}' stopped.");
            }
            entry.SwitchedOff = entry.Instance == null;
            if (persist)
            {
                // A GUID switched off here whose DLL is away for now stays
                // switched off when it comes back.
                var known = new HashSet<string>(_plugins.Select(p => p.Guid), StringComparer.OrdinalIgnoreCase);
                var off = DisabledGuids().Where(g => !known.Contains(g))
                    .Concat(_plugins.Where(p => !p.Enabled).Select(p => p.Guid))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _cfgDisabledPlugins.Value = string.Join(";", off);
            }
        }

        /// ModLoader 1.3+'s plugin registry, reached by reflection: the loader
        /// is a separate assembly this one never references, so an older
        /// loader without these methods leaves the manager on its component
        /// scan instead of failing to load.
        internal sealed class LoaderRegistry
        {
            private static LoaderRegistry _resolved;
            private static bool _looked;

            private readonly Func<Type[]> _pluginTypes;
            private readonly Func<Type, BaseUnityPlugin> _instanceOf;
            private readonly Func<Type, bool, BaseUnityPlugin> _setEnabled;

            private LoaderRegistry(Func<Type[]> pluginTypes, Func<Type, BaseUnityPlugin> instanceOf, Func<Type, bool, BaseUnityPlugin> setEnabled)
            {
                _pluginTypes = pluginTypes;
                _instanceOf = instanceOf;
                _setEnabled = setEnabled;
            }

            internal Type[] PluginTypes() => _pluginTypes() ?? new Type[0];
            internal BaseUnityPlugin InstanceOf(Type type) => _instanceOf(type);
            internal BaseUnityPlugin SetPluginEnabled(Type type, bool enabled) => _setEnabled(type, enabled);

            /// The registry, or null under an older loader. Looked up once per
            /// copy of the manager; the loader never reloads.
            internal static LoaderRegistry Resolve(BepInEx.Logging.ManualLogSource log)
            {
                if (_looked) return _resolved;
                _looked = true;
                try
                {
                    var loader = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("SanctuaryModLoader.LoaderPlugin", false))
                        .FirstOrDefault(t => t != null);
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                    var types = loader?.GetMethod("PluginTypes", flags, null, Type.EmptyTypes, null);
                    var instanceOf = loader?.GetMethod("InstanceOf", flags, null, new[] { typeof(Type) }, null);
                    var setEnabled = loader?.GetMethod("SetPluginEnabled", flags, null, new[] { typeof(Type), typeof(bool) }, null);
                    if (types != null && instanceOf != null && setEnabled != null)
                    {
                        _resolved = new LoaderRegistry(
                            (Func<Type[]>)Delegate.CreateDelegate(typeof(Func<Type[]>), types),
                            (Func<Type, BaseUnityPlugin>)Delegate.CreateDelegate(typeof(Func<Type, BaseUnityPlugin>), instanceOf),
                            (Func<Type, bool, BaseUnityPlugin>)Delegate.CreateDelegate(typeof(Func<Type, bool, BaseUnityPlugin>), setEnabled));
                        log.LogInfo("Mod manager: using the loader's plugin registry; switched-off plugins are never started.");
                    }
                    else
                    {
                        log.LogWarning("Mod manager: no ModLoader 1.3+ registry found, so switched-off plugins still start " +
                                       "before they are stopped, and a deleted plugin can stay listed until restart. " +
                                       "Updating ModLoader fixes both.");
                    }
                }
                catch (Exception e)
                {
                    _resolved = null;
                    log.LogWarning($"Mod manager: the loader's plugin registry is unusable ({e.Message}); using the component scan.");
                }
                return _resolved;
            }
        }

        private void RefreshHash()
        {
            try { _hashNow = EM.Lua.FilesCache.ComputeLuaHashString(); }
            catch (Exception e) { _hashNow = "?"; _log.LogWarning($"Hash compute failed: {e.Message}"); }
        }

        // ---- per-frame ----------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(_cfgToggleKey.Value)) ToggleUi();
            try { _page?.Tick(); }
            catch (Exception e) { _log.LogError($"Mods page: {e}"); }

            // Rescan periodically rather than once: each mod is its own
            // hot-reloadable DLL, so plugins appear and go at any time. With
            // ModLoader 1.3+ the loader holds switched-off plugins back itself;
            // under an older loader a reload re-adds them and this scan stops
            // them again. (Deferred off Awake anyway: the loader adds
            // components in one pass and ours can run first.)
            _pluginScanAccum += Time.unscaledDeltaTime;
            if (_pluginScanAccum >= 2f)
            {
                _pluginScanAccum = 0f;
                RefreshPlugins(applyDisabled: true);
            }

            if (_pendingApply && EM.Lua.FilesCache.pathToFileContents != null)
            {
                _pendingApply = false;
                try { _hashVanilla = EM.Lua.FilesCache.ComputeLuaHashString(); } catch { _hashVanilla = "?"; }
                Reapply();
                return;
            }

            // The game rebuilt the cache out from under us (dev file watcher /
            // future engine changes): our overlay is gone, so re-establish it.
            if (_appliedToDict != null && !ReferenceEquals(_appliedToDict, EM.Lua.FilesCache.pathToFileContents))
            {
                _log.LogWarning("FilesCache was rebuilt by the game; re-applying the mod overlay.");
                RestoreAll();
                Reapply();
            }
        }

        internal static string Short(string hash) =>
            string.IsNullOrEmpty(hash) ? "…" : (hash.Length > 16 ? hash.Substring(0, 16) : hash);
    }
}
