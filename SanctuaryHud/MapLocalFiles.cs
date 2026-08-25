using System;
using System.Collections.Generic;
using System.IO;
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
    public partial class SanctuaryHudPlugin
    {
        // Keyed by resolved on-disk path, which includes the map folder name -
        // so two maps carrying a file of the same name can never collide.
        // NativeArrays are Persistent and live for the session, matching how
        // FilesCache itself holds content; a map's worth is a few hundred KB.
        private static readonly Dictionary<string, NativeArray<byte>> _mapFileCache =
            new Dictionary<string, NativeArray<byte>>(StringComparer.OrdinalIgnoreCase);

        private void PatchMapLocalFiles()
        {
            EnsureHarmony();
            var target = AccessTools.Method(typeof(EM.Lua.FilesCache), nameof(EM.Lua.FilesCache.TryGetFileContent));
            if (target == null) throw new MissingMethodException("EM.Lua.FilesCache.TryGetFileContent not found");
            _harmony.Patch(target, postfix: new HarmonyMethod(typeof(SanctuaryHudPlugin), nameof(FileContentPostfix)));
            _log.LogInfo("Map-local file fallback: FilesCache.TryGetFileContent patched.");
        }

        private static void FileContentPostfix(string path, ref NativeArray<byte> fileContent, ref bool __result)
        {
            if (__result) return;
            if (path == null || !path.StartsWith("map/", StringComparison.OrdinalIgnoreCase)) return;

            string mapDir;
            try { mapDir = EM.Gamedata.Data.LoadedMapPath; }
            catch { return; }
            if (string.IsNullOrEmpty(mapDir)) return;

            string full;
            try { full = Path.GetFullPath(Path.Combine(mapDir, path.Substring(4))); }
            catch { return; }

            // The rewritten path must stay inside the map folder; "map/../"
            // escaping anywhere else is refused.
            var root = Path.GetFullPath(mapDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

            if (_mapFileCache.TryGetValue(full, out var cached))
            {
                fileContent = cached;
                __result = true;
                return;
            }

            if (!File.Exists(full)) return;
            try
            {
                var arr = new NativeArray<byte>(File.ReadAllBytes(full), Allocator.Persistent);
                _mapFileCache[full] = arr;
                fileContent = arr;
                __result = true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Map-local file read failed for {path}: {e.Message}");
            }
        }
    }
}
