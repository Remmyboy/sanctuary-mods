using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace SanctuaryHudLoader
{
    // Hot-reload host for every mod DLL under engine\SanctuaryMods. Sanctuary
    // destroys foreign root GameObjects (which is why BepInEx needs
    // HideManagerGameObject and why ScriptEngine's visible host object silently
    // dies), so this loader attaches reloaded plugins to its own gameObject —
    // BepInEx's protected, hidden manager — instead of creating a new one.
    //
    // Mods live outside the BepInEx tree, one folder each, next to the Lua
    // mods the mod manager overlays — so a single folder is the whole of a
    // mod, whether it ships a DLL, Lua files, or both. This loader is the one
    // piece that has to sit in BepInEx\plugins, because BepInEx loads it.
    //
    // Each DLL is watched and reloaded independently about a second after every
    // rebuild; F6 forces a reload of everything. A DLL deleted from the folder
    // has its plugins torn down on the next poll.
    [BepInPlugin("com.sanctuarydb.hudloader", "Sanctuary Mods Loader", "1.1.0")]
    public class LoaderPlugin : BaseUnityPlugin
    {
        private string _modsDir;
        private readonly Dictionary<string, DateTime> _loadedStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Component>> _live = new Dictionary<string, List<Component>>(StringComparer.OrdinalIgnoreCase);
        private float _pollAccum;

        private void Awake()
        {
            _modsDir = Path.Combine(Paths.GameRootPath, "SanctuaryMods");
            Logger.LogInfo($"Mods loader ready; watching {_modsDir} for mod DLLs (auto-reload on change, F6 forces).");
            LoadChanged(force: true);
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

        private void LoadChanged(bool force)
        {
            if (!Directory.Exists(_modsDir)) return;

            // One folder per mod is the convention, but a DLL dropped anywhere
            // under SanctuaryMods is picked up — no silent no-shows.
            var onDisk = Directory.GetFiles(_modsDir, "*.dll", SearchOption.AllDirectories);
            foreach (var path in onDisk)
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!force && _loadedStamps.TryGetValue(path, out var loaded) && loaded == stamp) continue;
                TryLoad(path, stamp);
            }

            // A DLL removed from the folder takes its plugins with it.
            foreach (var gone in _loadedStamps.Keys.Except(onDisk, StringComparer.OrdinalIgnoreCase).ToList())
            {
                TearDown(gone);
                _loadedStamps.Remove(gone);
                Logger.LogInfo($"{Path.GetFileName(gone)} removed; its plugin(s) unloaded.");
            }
        }

        private void TearDown(string path)
        {
            if (!_live.TryGetValue(path, out var components)) return;
            // Their OnDestroy handlers drop the Harmony patches they applied.
            foreach (var component in components.Where(c => c != null))
            {
                Destroy(component);
            }
            _live.Remove(path);
        }

        private void TryLoad(string path, DateTime stamp)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return; // mid-copy; the poll picks it up next second
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
                var components = new List<Component>();
                foreach (var type in GetTypesSafe(assembly)
                             .Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    components.Add(gameObject.AddComponent(type));
                }
                _live[path] = components;
                Logger.LogInfo($"Hot-loaded {components.Count} plugin(s) from {Path.GetFileName(path)} (built {stamp:HH:mm:ss} UTC).");
            }
            catch (Exception e)
            {
                Logger.LogError($"Hot reload of {Path.GetFileName(path)} failed: {e}");
            }
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
    }
}
