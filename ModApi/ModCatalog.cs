using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace Sanctuary.ModApi
{
    /// One folder under SanctuaryMods, as the catalog sees it.
    public sealed class ModInfo
    {
        public ModManifest Manifest { get; internal set; }
        public string Id => Manifest.Id;
        public string Name => Manifest.Name;
        public string Version => Manifest.Version;
        /// Full path of the mod's folder.
        public string Folder { get; internal set; }
        public string FolderName => Path.GetFileName(Folder);

        /// Gameplay when mod.json says so, or when the folder has Lua/.santp
        /// to overlay (which only a lobby can apply).
        public ModKind Kind { get; internal set; }
        /// The folder's DLLs belong to the lobby selection rather than to the
        /// player: only when mod.json says "kind": "gameplay". A folder
        /// without a manifest keeps its DLL personal, as before manifests.
        public bool DllsAreGameplay { get; internal set; }

        /// Overlay files, relative to the mod's luaRoot (and so to LJ\lua).
        public IReadOnlyList<string> OverlayFiles { get; internal set; } = Array.Empty<string>();
        public int LuaCount { get; internal set; }
        public int SantpCount { get; internal set; }
        public IReadOnlyList<string> Dlls { get; internal set; } = Array.Empty<string>();

        /// SHA-256 over what a match runs: every overlay file (path and
        /// bytes) and, for gameplay DLLs, the DLL bytes. mod.json is left out,
        /// so a reworded description is still the same mod. Empty for a mod
        /// with nothing to hash.
        public string ContentHash { get; internal set; } = "";
        public string ShortHash => ContentHash.Length > 8 ? ContentHash.Substring(0, 8) : ContentHash;

        /// Things wrong with the folder that didn't stop it being listed.
        public IReadOnlyList<string> Problems { get; internal set; } = Array.Empty<string>();

        public string LuaRootPath => Manifest.LuaRoot == "." ? Folder : Path.Combine(Folder, Manifest.LuaRoot);

        internal string Signature;

        public override string ToString() => $"{Name} {Version} ({Id}, {ShortHash})";
    }

    /// Every mod folder under engine\SanctuaryMods. Rescanned on demand and
    /// by the API plugin every couple of seconds; hashes are recomputed only
    /// for folders whose files changed.
    public static class ModCatalog
    {
        public static string ModsRoot => Path.Combine(Paths.GameRootPath, "SanctuaryMods");

        /// DLLs an author may ship by accident; the game or BepInEx already
        /// has them, and loading a second copy breaks both.
        internal static readonly string[] LibraryDlls =
        {
            "Sanctuary.ModApi.dll", "0Harmony.dll", "0Harmony20.dll", "HarmonyXInterop.dll",
            "Mono.Cecil.dll", "MonoMod.Utils.dll", "MonoMod.RuntimeDetour.dll", "Newtonsoft.Json.dll",
        };

        internal static bool IsLibraryDll(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                   LibraryDlls.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ModInfo> _mods = new List<ModInfo>();
        private static int _version;

        /// Every mod, sorted by name. The list is replaced, never changed in
        /// place, so holding on to it is safe.
        public static IReadOnlyList<ModInfo> Mods => _mods;

        /// Bumped whenever a rescan found a change (a mod added, removed, or
        /// any of its files edited).
        public static int Version => _version;

        public static IEnumerable<ModInfo> GameplayMods => _mods.Where(m => m.Kind == ModKind.Gameplay);

        public static ModInfo Find(string id) =>
            _mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

        public static ModInfo FindByFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            return _mods.FirstOrDefault(m => string.Equals(m.Folder, full, StringComparison.OrdinalIgnoreCase));
        }

        /// Looks at every folder again. Returns true when anything changed.
        public static bool Rescan()
        {
            var old = _mods.ToDictionary(m => m.Folder, StringComparer.OrdinalIgnoreCase);
            var next = new List<ModInfo>();
            if (Directory.Exists(ModsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(ModsRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    try
                    {
                        var info = Scan(Path.GetFullPath(dir).TrimEnd('\\', '/'), old);
                        if (info != null) next.Add(info);
                    }
                    catch (Exception e)
                    {
                        ModApiPlugin.Log?.LogWarning($"Mod folder '{name}' unreadable: {e.Message}");
                    }
                }
            }

            // Two folders claiming one id would make the lobby ambiguous;
            // the second keeps its folder name.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in next.OrderBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(m.Id)) continue;
                var clash = $"id '{m.Id}' is already used by another folder";
                if (!m.Problems.Contains(clash)) m.Problems = m.Problems.Concat(new[] { clash }).ToList();
                var alt = m.FolderName.ToLowerInvariant();
                m.Manifest.Id = ModManifest.IsValidId(alt) && seen.Add(alt) ? alt : m.Id + "-" + Math.Abs(m.Folder.GetHashCode());
            }

            next.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var changed = next.Count != _mods.Count ||
                          next.Any(m => !old.TryGetValue(m.Folder, out var was) || !ReferenceEquals(was, m));
            _mods = next;
            if (changed) _version++;
            return changed;
        }

        private static ModInfo Scan(string dir, Dictionary<string, ModInfo> old)
        {
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            // Cheap change detection: names, sizes and write times.
            var sig = new StringBuilder();
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                sig.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            var signature = sig.ToString();
            if (old.TryGetValue(dir, out var prev) && prev.Signature == signature) return prev;

            var name = Path.GetFileName(dir);
            var problems = new List<string>();
            ModManifest manifest = null;
            var manifestPath = Path.Combine(dir, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                try { manifest = ModManifest.Parse(File.ReadAllText(manifestPath), name, problems); }
                catch (Exception e) { problems.Add($"mod.json unreadable ({e.Message}); treated as a folder without one"); }
            }
            if (manifest == null)
            {
                var id = name.ToLowerInvariant();
                if (!ModManifest.IsValidId(id)) id = System.Text.RegularExpressions.Regex.Replace(id, "[^a-z0-9._-]", "_");
                if (id.Length > 64) id = id.Substring(0, 64);
                manifest = new ModManifest
                {
                    Id = id, Name = name, Version = "", Author = "", Description = "",
                    LuaRoot = ".", Url = "", Synthesised = true, KindUnset = true,
                };
            }

            var info = new ModInfo { Manifest = manifest, Folder = dir, Signature = signature };
            var luaRoot = info.LuaRootPath;
            var overlay = Directory.Exists(luaRoot)
                ? files.Where(f => f.StartsWith(luaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".santp", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Substring(luaRoot.Length).TrimStart('\\', '/'))
                    .OrderBy(r => r.ToLowerInvariant(), StringComparer.Ordinal)
                    .ToList()
                : new List<string>();
            info.OverlayFiles = overlay;
            info.LuaCount = overlay.Count(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            info.SantpCount = overlay.Count - info.LuaCount;

            var dlls = files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var lib in dlls.Where(IsLibraryDll)) problems.Add($"{Path.GetFileName(lib)} is a library the game already has; ignored");
            info.Dlls = dlls.Where(d => !IsLibraryDll(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

            info.DllsAreGameplay = !manifest.KindUnset && manifest.Kind == ModKind.Gameplay;
            info.Kind = info.DllsAreGameplay || overlay.Count > 0 ? ModKind.Gameplay : ModKind.Ui;
            if (!manifest.KindUnset && manifest.Kind == ModKind.Ui && overlay.Count > 0)
                problems.Add("kind is \"ui\" but the folder has Lua/.santp files: those can only be picked in a lobby, so the mod is listed as gameplay");
            if (manifest.KindUnset && !manifest.Synthesised && overlay.Count > 0 && info.Dlls.Count > 0)
                problems.Add("no kind given: the DLL stays a personal UI mod and only the Lua half is picked in the lobby");
            if (info.Kind == ModKind.Gameplay && overlay.Count == 0 && info.Dlls.Count == 0)
                problems.Add($"nothing to apply: no .lua or .santp under '{manifest.LuaRoot}' and no DLL");

            info.ContentHash = HashContent(info);
            info.Problems = problems;
            return info;
        }

        private static string HashContent(ModInfo info)
        {
            if (info.Kind != ModKind.Gameplay) return "";
            using (var sha = SHA256.Create())
            {
                void Add(string label, string path)
                {
                    var head = Encoding.UTF8.GetBytes(label.Replace('\\', '/').ToLowerInvariant() + "\n");
                    sha.TransformBlock(head, 0, head.Length, null, 0);
                    var bytes = File.ReadAllBytes(path);
                    var len = BitConverter.GetBytes((long)bytes.Length);
                    sha.TransformBlock(len, 0, len.Length, null, 0);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }

                foreach (var rel in info.OverlayFiles) Add("lua:" + rel, Path.Combine(info.LuaRootPath, rel));
                if (info.DllsAreGameplay)
                {
                    foreach (var dll in info.Dlls) Add("dll:" + dll.Substring(info.Folder.Length).TrimStart('\\', '/'), dll);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }
    }
}
