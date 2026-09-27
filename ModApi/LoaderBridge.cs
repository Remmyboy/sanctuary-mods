using System;
using System.Linq;
using System.Reflection;

namespace Sanctuary.ModApi
{
    /// The mod loader's registry, reached by reflection: the loader and this
    /// API ship and update separately, and either may be older than the other.
    internal static class LoaderBridge
    {
        private static bool _looked;
        private static Func<Type, string> _pathOf;
        private static Action<string[]> _setActive;
        private static string[] _lastActive = new string[0];

        private static void Resolve()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                var loader = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("SanctuaryModLoader.LoaderPlugin", false))
                    .FirstOrDefault(t => t != null);
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                var pathOf = loader?.GetMethod("PathOf", flags, null, new[] { typeof(Type) }, null);
                var setActive = loader?.GetMethod("SetActiveGameplayFolders", flags, null, new[] { typeof(string[]) }, null);
                if (pathOf != null) _pathOf = (Func<Type, string>)Delegate.CreateDelegate(typeof(Func<Type, string>), pathOf);
                if (setActive != null) _setActive = (Action<string[]>)Delegate.CreateDelegate(typeof(Action<string[]>), setActive);
                if (_setActive == null)
                    ModApiPlugin.Log.LogWarning("The mod loader is older than 1.4: gameplay mods' DLLs can't follow the lobby selection " +
                                                "(Lua and .santp still do). Update ModLoader.");
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Mod loader registry unusable: {e.Message}");
            }
        }

        /// The DLL a hot-loaded plugin type came from, or null.
        internal static string PathOf(Type type)
        {
            Resolve();
            try { return _pathOf?.Invoke(type); }
            catch { return null; }
        }

        /// Tells the loader which gameplay mod folders' DLLs should run.
        internal static void SetActiveGameplayFolders(string[] folders)
        {
            Resolve();
            if (folders.SequenceEqual(_lastActive, StringComparer.OrdinalIgnoreCase)) return;
            _lastActive = folders;
            try { _setActive?.Invoke(folders); }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Couldn't switch gameplay DLLs: {e.Message}"); }
        }
    }
}
