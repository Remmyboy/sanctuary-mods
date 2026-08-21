using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace SanctuaryHudLoader
{
    // Hot-reload host for SanctuaryHud.dll. Sanctuary destroys foreign root
    // GameObjects (which is why BepInEx needs HideManagerGameObject and why
    // ScriptEngine's visible host object silently dies), so this loader
    // attaches reloaded plugins to its own gameObject — BepInEx's protected,
    // hidden manager — instead of creating a new one.
    //
    // Watches BepInEx\scripts\SanctuaryHud.dll and reloads it automatically
    // about a second after every rebuild. F6 forces a reload.
    [BepInPlugin("com.sanctuarydb.hudloader", "SanctuaryDB HUD Loader", "1.0.0")]
    public class LoaderPlugin : BaseUnityPlugin
    {
        private string _dllPath;
        private DateTime _loadedStamp;
        private readonly List<Component> _live = new List<Component>();
        private float _pollAccum;

        private void Awake()
        {
            _dllPath = Path.Combine(Path.Combine(Paths.BepInExRootPath, "scripts"), "SanctuaryHud.dll");
            Logger.LogInfo($"HUD loader ready; watching {_dllPath} (auto-reload on change, F6 forces).");
            TryLoad();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                TryLoad();
                return;
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < 1f) return;
            _pollAccum = 0f;

            if (File.Exists(_dllPath) && File.GetLastWriteTimeUtc(_dllPath) != _loadedStamp)
            {
                TryLoad();
            }
        }

        private void TryLoad()
        {
            if (!File.Exists(_dllPath))
            {
                Logger.LogWarning($"Not found: {_dllPath}");
                return;
            }

            byte[] bytes;
            var stamp = File.GetLastWriteTimeUtc(_dllPath);
            try
            {
                bytes = File.ReadAllBytes(_dllPath);
            }
            catch (IOException)
            {
                return; // mid-copy; the poll picks it up next second
            }

            // Tear down the previous instance (its OnDestroy unpatches Harmony).
            foreach (var component in _live.Where(c => c != null))
            {
                Destroy(component);
            }
            _live.Clear();

            // Record the stamp even if the load fails, so a broken build logs
            // one error instead of one per second; F6 retries on demand.
            _loadedStamp = stamp;

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
                foreach (var type in GetTypesSafe(assembly)
                             .Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    _live.Add(gameObject.AddComponent(type));
                }
                Logger.LogInfo($"Hot-loaded {_live.Count} plugin(s) from SanctuaryHud.dll (built {stamp:HH:mm:ss} UTC).");
            }
            catch (Exception e)
            {
                Logger.LogError($"Hot reload failed: {e}");
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
