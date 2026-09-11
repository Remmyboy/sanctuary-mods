using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Unity.Collections;

namespace SanctuaryHud
{
    // Lets Lua's Engine.GetFileContent see files inside the loaded map's folder.
    //
    // A .sanmap can reference blueprints under "map/..." the same way it
    // references textures, and a converted map uses that to carry its own
    // decal blueprints. The asset pipeline resolves those fine - Data.PathToID
    // rewrites "map/..." against Data.LoadedMapPath, and Data.InitMapFiles has
    // already registered every file in the map folder. But Lua's file access
    // goes through EM.Lua.FilesCache instead, and that is a dictionary built
    // once at startup from LJ/lua, the .sanmap files, and Environment.sanpack.
    // Nothing from any map folder is ever in it, so a map-local .sandecal
    // comes back to Lua as an empty string and the decal loader dies on
    // json.decode of nothing.
    //
    // The fix is a lazy fallback on the miss path only: if the cache says no
    // and the path starts with "map/", resolve it against the loaded map's
    // folder and serve it from disk. The hit path is untouched, so shipped
    // content behaves exactly as before.
    [BepInPlugin("com.sanctuarydb.maplocalfiles", "Map-Local Files", "0.1.1")]
    public class MapLocalFilesPlugin : BaseUnityPlugin
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private Harmony _harmony;

        // Decal and prop blueprints are a few KB. A map-folder file anywhere
        // near this is not what the fallback is for, and would otherwise be
        // copied whole into native memory.
        private const long MaxFileBytes = 8L * 1024 * 1024;

        // Keyed by resolved on-disk path. The arrays are native
        // (Allocator.Persistent) and nothing frees them for us, so only the
        // current map's files are kept: they are disposed when a different
        // map's file is asked for, and when the plugin unloads. Disposing is
        // safe because the game never holds on to the buffer - its generated
        // GetFileContent wrapper copies it into a Lua string (ffi.string)
        // before returning.
        private static readonly Dictionary<string, NativeArray<byte>> _mapFileCache =
            new Dictionary<string, NativeArray<byte>>(StringComparer.OrdinalIgnoreCase);
        private static string _cachedMapRoot;
        private static readonly object _cacheLock = new object();

        private void Awake()
        {
            _log = Logger;
            try
            {
                var target = AccessTools.Method(typeof(EM.Lua.FilesCache), nameof(EM.Lua.FilesCache.TryGetFileContent));
                if (target == null) throw new MissingMethodException("EM.Lua.FilesCache.TryGetFileContent not found");
                _harmony = new Harmony("com.sanctuarydb.maplocalfiles." + Guid.NewGuid().ToString("N").Substring(0, 8));
                _harmony.Patch(target, postfix: new HarmonyMethod(typeof(MapLocalFilesPlugin), nameof(FileContentPostfix)));
                _log.LogInfo("Map-local file fallback: FilesCache.TryGetFileContent patched.");
            }
            catch (Exception e)
            {
                _log.LogError($"Map-local file fallback failed (map-carried decals will not load): {e}");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            lock (_cacheLock) DisposeCache();
        }

        private static void DisposeCache()
        {
            foreach (var array in _mapFileCache.Values)
            {
                if (array.IsCreated) array.Dispose();
            }
            _mapFileCache.Clear();
            _cachedMapRoot = null;
        }

        private static void FileContentPostfix(string path, ref NativeArray<byte> fileContent, ref bool __result)
        {
            if (__result) return;
            if (path == null || !path.StartsWith("map/", StringComparison.OrdinalIgnoreCase)) return;

            string mapDir;
            try { mapDir = EM.Gamedata.Data.LoadedMapPath; }
            catch { return; }
            if (string.IsNullOrEmpty(mapDir)) return;

            string root, full;
            try
            {
                root = Path.GetFullPath(mapDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                full = Path.GetFullPath(Path.Combine(root, path.Substring(4)));
            }
            catch { return; }

            // The rewritten path must stay inside the map folder; "map/../"
            // escaping anywhere else is refused.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

            lock (_cacheLock)
            {
                // A different map: the previous one's files are never asked
                // for again, so their native memory goes now.
                if (!string.Equals(_cachedMapRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    DisposeCache();
                    _cachedMapRoot = root;
                }

                if (_mapFileCache.TryGetValue(full, out var cached))
                {
                    fileContent = cached;
                    __result = true;
                    return;
                }

                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) return;
                    if (info.Length > MaxFileBytes)
                    {
                        _log.LogWarning($"Map-local file {path} is {info.Length:N0} bytes, over the {MaxFileBytes / (1024 * 1024)} MB limit; not served.");
                        return;
                    }
                    var array = new NativeArray<byte>(File.ReadAllBytes(full), Allocator.Persistent);
                    _mapFileCache[full] = array;
                    fileContent = array;
                    __result = true;
                }
                catch (Exception e)
                {
                    _log.LogWarning($"Map-local file read failed for {path}: {e.Message}");
                }
            }
        }
    }
}
