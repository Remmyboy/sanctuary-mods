using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EM.Gamedata;
using ICSharpCode.SharpZipLib.Zip;

namespace Sanctuary.ModApi
{
    /// Gameplay mods' art: the .sanpack files in a mod's packs\ folder, put
    /// into the game's asset table while a match with the mod loads and runs,
    /// and taken out again before any other match loads.
    ///
    /// The game reads every model, material, texture and sprite through one
    /// table of pack entries keyed by path, and loads each lazily the first
    /// time it's asked for, mostly while a match loads its unit prefabs. So a
    /// pack goes in just before a match loads (the host's Start, a client's
    /// loading, a replay's playback) and the table is left alone until the
    /// next one. A pack entry whose path the game already has replaces the
    /// game's; the object the game built from the old one is dropped from its
    /// caches both ways, or it would keep showing.
    public static class Packs
    {
        internal const string Folder = "packs";

        private static readonly FieldInfo FilesField =
            typeof(Data).GetField("SanPackLoadedFiles", BindingFlags.NonPublic | BindingFlags.Static);

        // The Load caches below the table, all Dictionary<uint, T> by entry id.
        private static readonly string[] CacheNames =
        {
            "SanModelsDictionary", "SanSkeletonsDictionary", "SanMaterialsDictionary", "SanAnimationsDictionary",
            "LoadedTextures", "SanSpriteDictionary", "SanUIDictionary", "SanVfxDictionary", "SanBeamDictionary",
            "SanStratIconGraphicDictionary", "SanDecalsDictionary", "SanPropsDictionary", "SanUnitsDictionary",
            "SanAudiosDictionary",
        };

        private static IDictionary[] _caches;

        private static readonly List<(uint id, IGamedataEntry previous, bool had)> Undo = new List<(uint, IGamedataEntry, bool)>();
        private static readonly List<ZipFile> Open = new List<ZipFile>();
        private static List<string> _mounted = new List<string>();

        /// The pack files in the game's asset table right now, in the order
        /// they went in (a later one wins where two have the same path).
        public static IReadOnlyList<string> Mounted => _mounted;

        /// A mod's .sanpack files, full paths, in a fixed order.
        internal static List<string> Find(string modFolder, IEnumerable<string> files)
        {
            var root = Path.Combine(modFolder, Folder) + Path.DirectorySeparatorChar;
            return files.Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                                    f.EndsWith(".sanpack", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Substring(root.Length).ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
        }

        private const string PortraitFolder = "UI/Sprites/Icons/Units/";

        /// The unit ids whose portraits these packs hold. Reads only the
        /// packs' directories.
        internal static List<string> PortraitIds(IEnumerable<string> packs)
        {
            var ids = new List<string>();
            foreach (var path in packs)
            {
                try
                {
                    using (var zip = new ZipFile(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), false))
                    {
                        foreach (ZipEntry e in zip)
                        {
                            var name = e.Name.Replace('\\', '/');
                            if (e.IsFile && name.StartsWith(PortraitFolder, StringComparison.OrdinalIgnoreCase) &&
                                name.EndsWith(".sansprite", StringComparison.OrdinalIgnoreCase))
                                ids.Add(name.Substring(PortraitFolder.Length, name.Length - PortraitFolder.Length - ".sansprite".Length));
                        }
                    }
                }
                catch (Exception e)
                {
                    ModApiPlugin.Log?.LogWarning($"Art pack {Rel(path)} unreadable: {e.Message}");
                }
            }
            return ids.Where(id => id.IndexOf('/') < 0).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        /// Makes the asset table hold the game's packs plus exactly these
        /// mods' packs, in this order. Call only outside a running match.
        internal static void Sync(IEnumerable<ModInfo> mods)
        {
            var want = mods.SelectMany(m => m.Packs).ToList();
            if (want.SequenceEqual(_mounted, StringComparer.OrdinalIgnoreCase)) return;
            Unmount();
            if (want.Count > 0) Mount(want);
        }

        private static ConcurrentDictionary<uint, IGamedataEntry> Table =>
            FilesField?.GetValue(null) as ConcurrentDictionary<uint, IGamedataEntry>;

        private static void Mount(List<string> packs)
        {
            var table = Table;
            if (table == null)
            {
                ModApiPlugin.Log.LogError("The game's asset table wasn't found in this game version; gameplay mods' art packs are off.");
                return;
            }
            var mounted = new List<string>();
            var total = 0;
            var replaced = 0;
            foreach (var path in packs)
            {
                ZipFile zip = null;
                try
                {
                    zip = new ZipFile(File.OpenRead(path), false);
                    var entries = new List<Data.GamedataZipEntry>();
                    foreach (ZipEntry e in zip)
                        if (e.IsFile) entries.Add(new Data.GamedataZipEntry(zip, e));
                    foreach (var entry in entries)
                    {
                        var id = entry.ID();
                        var had = table.TryGetValue(id, out var previous);
                        Undo.Add((id, previous, had));
                        table[id] = entry;
                        if (had) { Evict(id); replaced++; }
                        total++;
                    }
                    Open.Add(zip);
                    mounted.Add(path);
                }
                catch (Exception e)
                {
                    try { zip?.Close(); } catch { }
                    ModApiPlugin.Log.LogError($"Art pack {Rel(path)} couldn't be read, so its art is missing: {e.Message}");
                }
            }
            _mounted = mounted;
            ModApiPlugin.Log.LogInfo($"Art packs: {mounted.Count} mounted ({string.Join(", ", mounted.Select(Rel))}), " +
                                     $"{total} file(s), {replaced} of them replacing the game's.");
        }

        /// Puts the game's own entries back and closes the packs.
        internal static void Unmount()
        {
            if (Undo.Count == 0 && Open.Count == 0) { _mounted = new List<string>(); return; }
            var table = Table;
            if (table != null)
            {
                for (var i = Undo.Count - 1; i >= 0; i--)
                {
                    var (id, previous, had) = Undo[i];
                    if (had) table[id] = previous;
                    else table.TryRemove(id, out _);
                    // New ids too: a rebuilt pack mounted next time must not
                    // meet the old pack's models in the caches.
                    Evict(id);
                }
            }
            foreach (var zip in Open)
            {
                try { zip.Close(); } catch { }
            }
            ModApiPlugin.Log.LogInfo($"Art packs: {_mounted.Count} unmounted.");
            Undo.Clear();
            Open.Clear();
            _mounted = new List<string>();
        }

        /// Drops what the game built from an entry, so the next request reads
        /// the table again. The objects themselves are left for the scene
        /// change to reclaim: the game may still hold some (shield materials
        /// sit in a list it never clears).
        private static void Evict(uint id)
        {
            if (_caches == null)
            {
                var load = typeof(Data).Assembly.GetType("EM.Gamedata.Load");
                _caches = CacheNames
                    .Select(n => load?.GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary)
                    .Where(d => d != null)
                    .ToArray();
                if (_caches.Length < CacheNames.Length)
                    ModApiPlugin.Log.LogWarning($"Art packs: only {_caches.Length} of the game's {CacheNames.Length} asset caches were found; " +
                                                "art a pack replaces may not show.");
            }
            foreach (var cache in _caches) cache.Remove(id);
        }

        private static string Rel(string path)
        {
            var root = ModCatalog.ModsRoot + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : Path.GetFileName(path);
        }
    }
}
