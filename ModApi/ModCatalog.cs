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
        /// bytes), the options' keys, types and ranges, and, for gameplay
        /// DLLs, the DLL bytes. The rest of mod.json is left out, so a
        /// reworded description is still the same mod. Empty for a mod with
        /// nothing to hash.
        public string ContentHash { get; internal set; } = "";
        public string ShortHash => ContentHash.Length > 8 ? ContentHash.Substring(0, 8) : ContentHash;

        /// Things wrong with the folder that didn't stop it being listed.
        public IReadOnlyList<string> Problems { get; internal set; } = Array.Empty<string>();

        public string LuaRootPath => Manifest.LuaRoot == "." ? Folder : Path.Combine(Folder, Manifest.LuaRoot);

        internal string Signature;
        /// The id mod.json gave (or the folder's), before the catalog settles
        /// a clash with another folder, and the problems found in the
        /// folder's own files. The catalog works Id and Problems out from
        /// these on every rescan.
        internal string DeclaredId;
        internal IReadOnlyList<string> ScanProblems = Array.Empty<string>();

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

        private static IReadOnlyList<string> _notices = Array.Empty<string>();

        /// Things wrong with how mods were installed rather than with any one
        /// mod: an archive nobody extracted, say. For the Mods page to show.
        public static IReadOnlyList<string> Notices => _notices;

        private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

        /// The folders holding a mod.json up to three levels inside a
        /// top-level folder that has none of its own: a zip extracted with
        /// its own folder around the mod, or a pack of several mods.
        internal static List<string> NestedModFolders(string dir)
        {
            var found = new List<string>();
            void Walk(string d, int depth)
            {
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    if (Path.GetFileName(sub).StartsWith(".")) continue;
                    if (File.Exists(Path.Combine(sub, ModManifest.FileName))) found.Add(Path.GetFullPath(sub).TrimEnd('\\', '/'));
                    else if (depth < 3) Walk(sub, depth + 1);
                }
            }
            Walk(dir, 1);
            return found;
        }

        /// Looks at every folder again. Returns true when anything changed.
        public static bool Rescan()
        {
            var old = _mods.ToDictionary(m => m.Folder, StringComparer.OrdinalIgnoreCase);
            var next = new List<ModInfo>();
            var notices = new List<string>();
            // Notes that depend on the other folders, not on the mod's own
            // files. Worked out afresh every pass, never added to the ModInfo
            // Scan kept, so one goes away when its reason does.
            var extra = new Dictionary<ModInfo, List<string>>();
            if (Directory.Exists(ModsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(ModsRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    try
                    {
                        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                        // A folder around the mod(s) rather than the mod
                        // itself: take the mods from inside it, as the loader
                        // does for their DLLs.
                        var nested = File.Exists(Path.Combine(full, ModManifest.FileName)) ? null : NestedModFolders(full);
                        if (nested == null || nested.Count == 0)
                        {
                            var info = Scan(full, old);
                            if (info != null) next.Add(info);
                            continue;
                        }
                        foreach (var inner in nested)
                        {
                            var info = Scan(inner, old);
                            if (info == null) continue;
                            if (nested.Count == 1)
                            {
                                var rel = inner.Substring(ModsRoot.Length).TrimStart('\\', '/');
                                AddNote(extra, info, $"installed one folder too deep ({rel}). It works, but moving '{Path.GetFileName(inner)}' straight into SanctuaryMods keeps things tidy");
                            }
                            next.Add(info);
                        }
                    }
                    catch (Exception e)
                    {
                        ModApiPlugin.Log?.LogWarning($"Mod folder '{name}' unreadable: {e.Message}");
                    }
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(ModsRoot))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!ArchiveExtensions.Contains(ext)) continue;
                        var stem = Path.GetFileNameWithoutExtension(file);
                        // Extracted already, the archive left beside it: fine.
                        if (Directory.Exists(Path.Combine(ModsRoot, stem))) continue;
                        notices.Add($"{Path.GetFileName(file)} hasn't been extracted. Extract it here, so its folder sits in SanctuaryMods beside the others; the archive itself does nothing.");
                    }
                    foreach (var file in Directory.EnumerateFiles(ModsRoot, "*.dll"))
                        if (!IsLibraryDll(file))
                            notices.Add($"{Path.GetFileName(file)} sits loose in SanctuaryMods. It loads, but give it a folder of its own (with its mod.json, if it came with one) so it can be told apart from other mods.");
                }
                catch (Exception e) { ModApiPlugin.Log?.LogWarning($"SanctuaryMods listing failed: {e.Message}"); }
            }
            var noticesChanged = !notices.SequenceEqual(_notices);
            _notices = notices;

            // Two folders claiming one id would make the lobby ambiguous: the
            // first by folder name keeps it, the next takes its folder name
            // (or the id plus a code made from the folder name). Only names
            // go into it, never the install path, so two players whose
            // folders are named alike end up with the same ids.
            var ids = new Dictionary<ModInfo, string>();
            var seen = new HashSet<string>(next.Select(m => m.DeclaredId), StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in next.OrderBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                if (claimed.Add(m.DeclaredId)) { ids[m] = m.DeclaredId; continue; }
                AddNote(extra, m, $"id '{m.DeclaredId}' is already used by another folder");
                var alt = m.FolderName.ToLowerInvariant();
                if (!ModManifest.IsValidId(alt) || seen.Contains(alt) || !claimed.Add(alt))
                {
                    var stem = m.DeclaredId.Length > 55 ? m.DeclaredId.Substring(0, 55) : m.DeclaredId;
                    alt = stem + "-" + ShortCode(m.FolderName.ToLowerInvariant());
                    claimed.Add(alt);
                }
                ids[m] = alt;
            }

            // Kept entries take this pass's id and notes; a change to either
            // counts as a change, so screens showing them redraw.
            var relabelled = false;
            foreach (var m in next)
            {
                var id = ids[m];
                var problems = extra.TryGetValue(m, out var more) ? m.ScanProblems.Concat(more).ToList() : m.ScanProblems;
                if (m.Manifest.Id != id) { m.Manifest.Id = id; relabelled = true; }
                if (!m.Problems.SequenceEqual(problems)) { m.Problems = problems; relabelled = true; }
            }

            next.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var changed = noticesChanged || relabelled || next.Count != _mods.Count ||
                          next.Any(m => !old.TryGetValue(m.Folder, out var was) || !ReferenceEquals(was, m));
            _mods = next;
            if (changed) _version++;
            return changed;
        }

        private static void AddNote(Dictionary<ModInfo, List<string>> extra, ModInfo m, string note)
        {
            if (!extra.TryGetValue(m, out var list)) extra[m] = list = new List<string>();
            if (!list.Contains(note)) list.Add(note);
        }

        /// Eight hex digits that stand for a name the same way on every machine.
        private static string ShortCode(string s)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(4).Select(b => b.ToString("x2")));
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
            foreach (var (field, script) in new[] { ("hostScript", manifest.HostScript), ("clientScript", manifest.ClientScript) })
            {
                if (script.Length == 0) continue;
                if (!overlay.Any(r => string.Equals(r.Replace('\\', '/'), script, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{field} '{script}' isn't in the mod's {manifest.LuaRoot} folder, so nothing runs");
                else if (IsAppendPath(script))
                    problems.Add($"{field} '{script}' is an append; a script is a file of its own, outside append\\");
            }
            if (manifest.IsForOtherGameVersion(UnityEngine.Application.version))
                problems.Add($"made for game version {manifest.GameVersion}; this is {UnityEngine.Application.version}. If it misbehaves, look for an update");
            if (info.Kind != ModKind.Gameplay && manifest.Options.Count > 0)
                problems.Add("options only apply to gameplay mods (the lobby host picks them); a UI mod's settings go in Config.Bind");
            if (overlay.Any(r => r.Replace('\\', '/').StartsWith("modoptions/", StringComparison.OrdinalIgnoreCase)))
                problems.Add("lua\\modoptions\\ is where the Mod API writes options files; files of yours there may be replaced");

            info.ContentHash = HashContent(info);
            info.DeclaredId = manifest.Id;
            info.ScanProblems = problems;
            info.Problems = problems;
            return info;
        }

        private static bool IsAppendPath(string rel) => Overlay.IsAppend(rel, out _);

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
                // The options decide what the generated options file can
                // say, so they're part of what the match runs. Only for mods
                // that have options: other mods' hashes stay as they were.
                if (info.Manifest.Options.Count > 0)
                {
                    var sig = Encoding.UTF8.GetBytes("options\n" + string.Join("\n", info.Manifest.Options.Select(o => o.Signature())));
                    sha.TransformBlock(sig, 0, sig.Length, null, 0);
                }
                // So are the scripts the framework imports for it.
                if (info.Manifest.HostScript.Length > 0 || info.Manifest.ClientScript.Length > 0)
                {
                    var sig = Encoding.UTF8.GetBytes("scripts\n" + info.Manifest.HostScript + "\n" + info.Manifest.ClientScript);
                    sha.TransformBlock(sig, 0, sig.Length, null, 0);
                }
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
