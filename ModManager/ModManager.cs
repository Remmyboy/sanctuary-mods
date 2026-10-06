using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Sanctuary.ModApi;
using UnityEngine;

namespace SanctuaryHud
{
    // The Mod Manager: the Mods page in the front menu (and on its hotkey in a
    // match), and the Mods panel in the lobby. It is the face of the mod
    // framework; the machinery is the mod API (Sanctuary.ModApi.dll, in
    // BepInEx\plugins), which this references and which never hot-reloads.
    //
    // Two kinds of mod, one folder each under <engine>\SanctuaryMods:
    //   UI mods (a DLL) are yours: switched on and off here at any time, even
    //   mid-match, and invisible to other players.
    //   Gameplay mods (Lua and .santp laid out like LJ\lua, maybe a DLL)
    //   change the match itself. The lobby host picks them and every player
    //   must hold identical copies; outside a lobby the game is vanilla. The
    //   API applies them, checks everyone, and holds Start until they match.
    //
    // This plugin hot-reloads like any mod; the gameplay files a running
    // match reads live in the API, so reloading it mid-match is harmless.
    [BepInPlugin("com.sanctuarydb.modmanager", "Sanctuary Mod Manager", "0.14.0")]
    [BepInDependency(ModApiPlugin.Guid)]
    public class ModManagerPlugin : BaseUnityPlugin
    {
        /// Tells ModLoader 1.3+ that this manager lists the loader's registry,
        /// so switched-off plugins can be held back before they start: they
        /// still appear on the page and can be switched on again. The loader
        /// looks for the field by name; its value is never read.
        public const bool ListsLoaderRegistry = true;

        private static BepInEx.Logging.ManualLogSource _log;

        // ---- what the menu page reads and drives ---------------------------
        internal IReadOnlyList<PluginEntry> Plugins => _plugins;

        internal void Rescan()
        {
            ModCatalog.Rescan();
            RefreshPlugins(applyDisabled: false);
        }

        internal void OpenModsFolder() => Application.OpenURL("file:///" + ModCatalog.ModsRoot.Replace('\\', '/'));

        // ---- updates --------------------------------------------------------
        internal Updates Updates { get; private set; }
        internal bool CheckUpdatesOnOpen => _cfgCheckUpdates.Value;
        private ConfigEntry<bool> _cfgCheckUpdates;

        /// The version installed in a mod folder, as the mod reports it: its
        /// plugin's [BepInPlugin] version, else its mod.json's. Null when the
        /// folder isn't installed or has no version of its own.
        internal string InstalledVersion(string folder)
        {
            if (string.Equals(folder, "ModManager", StringComparison.OrdinalIgnoreCase))
                return MetadataHelper.GetMetadata(this)?.Version?.ToString();
            var registry = LoaderRegistry.Resolve(_log);
            var types = registry != null ? registry.PluginTypes() : _plugins.Select(p => p.Type).Where(t => t != null).ToArray();
            foreach (var type in types)
            {
                if (!string.Equals(Modding.ModOf(type)?.FolderName, folder, StringComparison.OrdinalIgnoreCase)) continue;
                try { return MetadataHelper.GetMetadata(type)?.Version?.ToString(); }
                catch { /* no readable [BepInPlugin] */ }
            }
            var mod = ModCatalog.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folder, StringComparison.OrdinalIgnoreCase));
            return mod != null && !mod.Manifest.Synthesised ? mod.Version : null;
        }

        internal static string FolderPath(string folder) =>
            ModCatalog.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folder, StringComparison.OrdinalIgnoreCase))?.Folder
            ?? System.IO.Path.Combine(ModCatalog.ModsRoot, folder);

        // ---- C# plugin toggles --------------------------------------------
        // Every UI plugin on this same hidden manager GameObject: the ones the
        // hot-reload loader manages, read from its registry, and any BepInEx
        // loaded itself. Switching one off destroys its component (OnDestroy
        // unpatches Harmony) and switching it on creates it again. That is a
        // component teardown, not an unload: .NET can't unload an assembly
        // from the running game, so the plugin's code and static state stay in
        // memory until the game exits. With ModLoader 1.3+ a switched-off
        // plugin is never started at all, and one whose DLL is deleted leaves
        // the list. UI plugins never enter the lobby's check, so they are
        // safe to toggle any time, even mid-match. Gameplay mods' DLLs are
        // not listed here: the lobby starts and stops those.
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
            // Its folder's mod.json, when it has one.
            public ModInfo Mod;
            public bool Enabled => Instance != null;
        }

        private readonly List<PluginEntry> _plugins = new List<PluginEntry>();

        /// Bumped whenever the list above is refreshed or a plugin switched,
        /// so the page only looks for differences after one of those.
        internal int PluginsVersion { get; private set; }

        private ConfigEntry<string> _cfgDisabledPlugins;
        private float _pluginScanAccum = 999f; // scan on the first Update

        // What the last scan saw: the loader's plugin types and the plugin
        // components on the manager object, compared by identity. While the
        // page is closed the list is only rebuilt when one of them moves.
        private Type[] _lastTypes;
        private readonly List<BaseUnityPlugin> _components = new List<BaseUnityPlugin>();
        private readonly List<BaseUnityPlugin> _lastComponents = new List<BaseUnityPlugin>();

        // [BepInPlugin] per type, read once: a type's attributes never change.
        private readonly Dictionary<Type, BepInPlugin> _metaCache = new Dictionary<Type, BepInPlugin>();

        private ConfigEntry<KeyCode> _cfgToggleKey;

        // The UI: a "Mods" entry in the front menu's sidebar opening a page
        // built from the game's own settings screen, and the lobby's Mods
        // panel. The hotkey opens the page full-screen during a match.
        private ModsPage _page;

        private void Awake()
        {
            _log = Logger;
            _cfgToggleKey = Config.Bind("UI", "ToggleKey", KeyCode.F8, "Key that opens/closes the Mods page, in the front menu or during a match.");
            _cfgDisabledPlugins = Config.Bind("Plugins", "Disabled", "",
                "Semicolon-separated GUIDs of C# plugins switched off on the Mods page. ModLoader 1.3+ never starts " +
                "these; an older loader starts them and the manager stops them straight away.");
            _cfgCheckUpdates = Config.Bind("Updates", "CheckWhenOpened", true,
                "Look for newer releases of these mods on GitHub when the Mods page opens (at most every 30 minutes). " +
                "Check for Updates, at the bottom of the page, looks whatever this says.");
            MigrateLuaMods();
            Updates.ApplyPending(_log);
            Updates = new Updates(this, _log);

            _page = new ModsPage(this, _log);
            _log.LogInfo($"Mod manager ready: {ModCatalog.Mods.Count} mod folder(s) in {ModCatalog.ModsRoot}. " +
                         $"Mods is in the front menu's sidebar ({_cfgToggleKey.Value} also opens it) and in the lobby.");
        }

        /// Before 0.7 Lua mods were switched on globally from this page
        /// ([Mods] Enabled = folder;folder), which kept a player out of every
        /// vanilla lobby. Gameplay mods are the lobby host's pick now, and
        /// every lobby starts with none, so the old list just goes. So does
        /// [Plugins] VanillaMode, a switch the 0.7 test builds had.
        private void MigrateLuaMods()
        {
            var old = Config.Bind("Mods", "Enabled", "", "");
            if (!string.IsNullOrWhiteSpace(old.Value))
                _log.LogInfo($"Lua mods switched on before 0.7 ({old.Value}) are now picked per lobby by the host, in the lobby's Mods panel.");
            Config.Remove(old.Definition);
            Config.Remove(Config.Bind("Plugins", "VanillaMode", false, "").Definition);
            Config.Save();
        }

        private void OnDestroy()
        {
            try { _page?.Destroy(); }
            catch (Exception e) { _log.LogWarning($"Mods page teardown failed: {e.Message}"); }
        }

        /// The hotkey: opens or closes the page from the front menu or a
        /// match, nothing from the lobby or loading screens.
        internal void ToggleUi()
        {
            if (_page != null && _page.CanOpen) _page.Toggle();
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
                    if (registry.IsGameplay(type)) continue;
                    var entry = Track(type, seen);
                    if (entry == null) continue;
                    entry.FromLoader = true;
                    entry.Instance = registry.InstanceOf(type);
                    entry.SwitchedOff = entry.Instance == null;
                    entry.Mod = Modding.ModOf(type);
                    if (entry.Instance != null && entry.Instance.Config != null) entry.Config = entry.Instance.Config;
                }
            }

            // Everything else on the manager object: plugins BepInEx loaded
            // itself, or every plugin under a loader older than 1.3. These can
            // only be switched off once they are already running. BepInEx's own
            // (the API among them) are part of the framework, not mods.
            foreach (var comp in GetComponents<BaseUnityPlugin>())
            {
                if (fromLoader.Contains(comp.GetType())) continue;
                if (comp.Info?.Metadata?.GUID == ModApiPlugin.Guid) continue;
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
            PluginsVersion++;
        }

        /// The page is opening: its list should be the state right now.
        internal void RefreshPluginsNow() => RefreshPlugins(applyDisabled: true);

        /// True when the loader's plugin types or the plugin components on
        /// this object differ, by identity, from the last look. Cheap enough
        /// for every couple of seconds: no attribute reads, no strings.
        private bool LoadSetChanged()
        {
            var changed = false;
            var registry = LoaderRegistry.Resolve(_log);
            if (registry != null)
            {
                var types = registry.PluginTypes();
                if (_lastTypes == null || types.Length != _lastTypes.Length) changed = true;
                else
                {
                    for (var i = 0; i < types.Length && !changed; i++)
                        if (types[i] != _lastTypes[i]) changed = true;
                }
                _lastTypes = types;
            }
            // Components cover starts and stops too: every running plugin is
            // one, on this same object.
            GetComponents(_components);
            if (_components.Count != _lastComponents.Count) changed = true;
            else
            {
                for (var i = 0; i < _components.Count && !changed; i++)
                    if (!ReferenceEquals(_components[i], _lastComponents[i])) changed = true;
            }
            _lastComponents.Clear();
            _lastComponents.AddRange(_components);
            return changed;
        }

        /// The entry for a plugin type, created on first sight and marked
        /// seen. Null for the framework's own plugins (switching one off would
        /// leave nothing to switch it back on), for a type without
        /// [BepInPlugin], and for a GUID already seen this pass.
        private PluginEntry Track(Type type, HashSet<string> seen)
        {
            if (!_metaCache.TryGetValue(type, out var meta)) _metaCache[type] = meta = type.GetCustomAttribute<BepInPlugin>();
            if (meta == null) return null;
            if (meta.GUID == "com.sanctuarydb.modloader" || meta.GUID == "com.sanctuarydb.modmanager" || meta.GUID == ModApiPlugin.Guid) return null;
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
            var config = plugin.Instance != null && plugin.Instance.Config != null ? plugin.Instance.Config : plugin.Config;
            return config?.GetConfigEntries() ?? Array.Empty<ConfigEntryBase>();
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
            PluginsVersion++;
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
            private readonly Func<Type, bool> _isGameplay;

            private LoaderRegistry(Func<Type[]> pluginTypes, Func<Type, BaseUnityPlugin> instanceOf,
                Func<Type, bool, BaseUnityPlugin> setEnabled, Func<Type, bool> isGameplay)
            {
                _pluginTypes = pluginTypes;
                _instanceOf = instanceOf;
                _setEnabled = setEnabled;
                _isGameplay = isGameplay;
            }

            internal Type[] PluginTypes() => _pluginTypes() ?? new Type[0];
            internal BaseUnityPlugin InstanceOf(Type type) => _instanceOf(type);
            internal BaseUnityPlugin SetPluginEnabled(Type type, bool enabled) => _setEnabled(type, enabled);
            // A loader older than 1.4 has no gameplay plugins: all are UI.
            internal bool IsGameplay(Type type) => _isGameplay != null && _isGameplay(type);

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
                    var isGameplay = loader?.GetMethod("IsGameplay", flags, null, new[] { typeof(Type) }, null);
                    if (types != null && instanceOf != null && setEnabled != null)
                    {
                        _resolved = new LoaderRegistry(
                            (Func<Type[]>)Delegate.CreateDelegate(typeof(Func<Type[]>), types),
                            (Func<Type, BaseUnityPlugin>)Delegate.CreateDelegate(typeof(Func<Type, BaseUnityPlugin>), instanceOf),
                            (Func<Type, bool, BaseUnityPlugin>)Delegate.CreateDelegate(typeof(Func<Type, bool, BaseUnityPlugin>), setEnabled),
                            isGameplay == null ? null : (Func<Type, bool>)Delegate.CreateDelegate(typeof(Func<Type, bool>), isGameplay));
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
            // components in one pass and ours can run first.) The list itself
            // is only rebuilt when the plugins on hand changed, or while the
            // page is up to show it.
            _pluginScanAccum += Time.unscaledDeltaTime;
            if (_pluginScanAccum >= 2f)
            {
                _pluginScanAccum = 0f;
                var changed = LoadSetChanged();
                if (changed || (_page != null && _page.IsOpen)) RefreshPlugins(applyDisabled: true);
            }
        }

        internal static string Short(string hash) =>
            string.IsNullOrEmpty(hash) ? "…" : (hash.Length > 16 ? hash.Substring(0, 16) : hash);
    }
}
