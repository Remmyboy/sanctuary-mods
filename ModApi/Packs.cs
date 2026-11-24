using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sanctuary.ModApi
{
    /// Gameplay mods' art: the .sanpack files in a mod's packs\ folder.
    ///
    /// Up to game build 26880003 (24 Nov 2026) the game read every model,
    /// material, texture and sprite through one table of .sanpack entries
    /// (Data.SanPackLoadedFiles), and the Mod API put a mod's packs into it
    /// for the match. That build replaced the table with Unity AssetBundles
    /// (EM.Content.AssetBundleLoader, which has mod bundle folders of its
    /// own), so .sanpack art can no longer be mounted: a mod that ships packs
    /// is told so, and the rest of it still loads.
    ///
    /// Nothing here may name a type the game has removed: a field of such a
    /// type made this whole class fail to load, which took the mod catalog
    /// (and with it the Mods page) down with it.
    public static class Packs
    {
        internal const string Folder = "packs";

        internal const string Unsupported =
            "has .sanpack art packs, which this game version no longer reads (its art comes from AssetBundles now); the mod's art is missing";

        /// The pack files in the game's asset table right now: always none.
        public static IReadOnlyList<string> Mounted { get; } = new List<string>();

        /// A mod's .sanpack files, full paths, in a fixed order.
        internal static List<string> Find(string modFolder, IEnumerable<string> files)
        {
            var root = Path.Combine(modFolder, Folder) + Path.DirectorySeparatorChar;
            return files.Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                                    f.EndsWith(".sanpack", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Substring(root.Length).ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
        }

        /// The unit ids whose portraits these packs hold: none, as packs
        /// are not mounted.
        internal static List<string> PortraitIds(IEnumerable<string> packs) => new List<string>();

        private static bool _warned;

        /// Was: make the asset table hold exactly these mods' packs. Now only
        /// says, once per session, that a picked mod's art can't be shown.
        internal static void Sync(IEnumerable<ModInfo> mods)
        {
            if (_warned) return;
            var with = mods.Where(m => m.Packs.Count > 0).Select(m => m.Name).ToList();
            if (with.Count == 0) return;
            _warned = true;
            ModApiPlugin.Log?.LogWarning($"Art packs are not supported on this game version; missing art for: {string.Join(", ", with)}.");
        }

        /// Nothing is ever mounted, so nothing to take out.
        internal static void Unmount() { }
    }
}
