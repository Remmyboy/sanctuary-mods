using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using Unity.Collections;

namespace Sanctuary.ModApi
{
    /// The Lua overlay: gameplay mods' .lua and .santp files swapped into the
    /// game's in-memory FilesCache. Every Lua VM and the lobby's hash read
    /// from that cache, never from disk, and a VM reads it lazily, on every
    /// Import, for the whole match. So an overlay has to be in place before
    /// the match's VMs start and stay exactly as it was until the match is
    /// cleaned up. Nothing on disk is ever touched.
    ///
    /// This lives in the API rather than the Mod Manager so that hot-reloading
    /// the manager mid-match can't pull the files out from under the VMs.
    public static class Overlay
    {
        // Original cache entries we replaced (never our own arrays), added
        // keys that had no original, and every array we allocated (disposed on
        // restore, including arrays a later mod's file overwrote in the dict).
        private static readonly Dictionary<string, NativeArray<byte>> Pristine =
            new Dictionary<string, NativeArray<byte>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<NativeArray<byte>> Allocated = new List<NativeArray<byte>>();
        // CreateFileCache reassigns the whole dictionary, so if the game
        // rebuilds the cache our entries are already gone, and restoring into
        // the new one would corrupt it.
        private static Dictionary<string, NativeArray<byte>> _appliedToDict;

        private static List<ModInfo> _applied = new List<ModInfo>();
        private static string _vanillaHash = "";
        private static string _currentHash = "";

        // Folder listings come from a separate private index built from disk;
        // added files in new folders need their folder chain registered there
        // or Lua directory enumeration won't see them.
        private static readonly FieldInfo DirIndexField = typeof(EM.Lua.FilesCache)
            .GetField("directoryToSubFolderNames", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo RebuildDirIndexMi = typeof(EM.Lua.FilesCache)
            .GetMethod("RebuildDirectoryIndex", BindingFlags.NonPublic | BindingFlags.Static);

        private static string LuaRoot => Path.GetFullPath(Path.Combine(Paths.GameRootPath, "LJ", "lua"));

        /// The mods whose files are in the cache right now, in apply order.
        public static IReadOnlyList<ModInfo> Applied => _applied;

        public static bool IsVanilla => _applied.Count == 0;

        /// The game's Lua hash with no mods applied: what the lobby compares
        /// at join, and what a player without any mods has.
        public static string VanillaHash => _vanillaHash;

        /// The game's Lua hash as the cache stands now.
        public static string CurrentHash => _currentHash;

        /// True once the game has built its file cache.
        public static bool CacheReady => EM.Lua.FilesCache.pathToFileContents != null;

        internal static void CaptureVanillaHash()
        {
            if (!CacheReady || _vanillaHash.Length > 0) return;
            RestoreAll();
            _vanillaHash = SafeHash();
            _currentHash = _vanillaHash;
        }

        private const string AppendDir = "append";

        /// An overlay file under append\ adds to a game file instead of
        /// replacing it: append\common\colors.lua goes onto common\colors.lua.
        internal static bool IsAppend(string rel, out string target)
        {
            var norm = rel.Replace('/', '\\');
            if (norm.StartsWith(AppendDir + "\\", StringComparison.OrdinalIgnoreCase) && norm.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                target = norm.Substring(AppendDir.Length + 1);
                return true;
            }
            target = norm;
            return false;
        }

        /// Files that more than one of the given mods replace. The later mod
        /// in the list wins. Appends never conflict: they stack.
        public static IEnumerable<(string file, ModInfo[] mods)> Conflicts(IEnumerable<ModInfo> mods)
        {
            return mods
                .SelectMany(m => m.OverlayFiles.Where(f => !IsAppend(f, out _))
                    .Select(f => (file: f.Replace('/', '\\').ToLowerInvariant(), mod: m)))
                .GroupBy(x => x.file)
                .Where(g => g.Count() > 1)
                .Select(g => (g.Key, g.Select(x => x.mod).ToArray()));
        }

        /// The option values of each applied mod that has options, by mod id.
        private static Dictionary<string, Dictionary<string, string>> _appliedOptions =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// The AI seats the overlay routes to mods' AIs, as last applied.
        private static List<AiSeat> _appliedAis = new List<AiSeat>();

        internal static IReadOnlyList<AiSeat> AppliedAis => _appliedAis;

        /// The option values an applied mod runs with, or null when the mod
        /// isn't applied.
        public static IReadOnlyDictionary<string, string> OptionsOf(string modId)
        {
            if (!_applied.Any(m => m.Id == modId)) return null;
            return _appliedOptions.TryGetValue(modId, out var v) ? v : new Dictionary<string, string>();
        }

        /// Makes the cache hold vanilla plus exactly these mods, in this order.
        /// Replacements go first, a later mod's winning; then each mod's
        /// options file; then every append, in the same order, onto whatever
        /// the file has become. A mod missing from `options` runs with its
        /// defaults. `ais` are the AI seats playing a mod's AI; a seat whose
        /// mod isn't among `mods` plays the game's default. Returns false when
        /// the cache doesn't exist yet.
        internal static bool Apply(IList<ModInfo> mods, IReadOnlyDictionary<string, Dictionary<string, string>> options = null,
            IEnumerable<AiSeat> ais = null)
        {
            var cache = EM.Lua.FilesCache.pathToFileContents;
            if (cache == null) return false;

            var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var m in mods.Where(m => m.Manifest.Options.Count > 0))
            {
                Dictionary<string, string> given = null;
                options?.TryGetValue(m.Id, out given);
                values[m.Id] = OptionValues.Complete(m, given);
            }
            var seats = (ais ?? Enumerable.Empty<AiSeat>()).Where(s => s != null).ToList();

            // Unchanged: leave the arrays the VMs may already hold alone.
            if (_appliedToDict != null && ReferenceEquals(_appliedToDict, cache) &&
                mods.Count == _applied.Count &&
                mods.Zip(_applied, (a, b) => ReferenceEquals(a, b)).All(x => x) &&
                values.Count == _appliedOptions.Count &&
                values.All(kv => _appliedOptions.TryGetValue(kv.Key, out var was) && OptionValues.Signature(was) == OptionValues.Signature(kv.Value)) &&
                Ais.Signature(seats) == Ais.Signature(_appliedAis))
            {
                return true;
            }

            // A mod that fails part-way (a file unreadable, say) would leave
            // the files it had already written in the cache while not being
            // counted as applied, and nothing would ever take them out again.
            // So the cache is rebuilt without it: it holds exactly the mods
            // that went on whole, and the ones that didn't are in Failed.
            var failed = new List<ModInfo>();
            var files = 0;
            var applied = new List<ModInfo>();
            while (true)
            {
                RestoreAll();
                _appliedToDict = cache;
                var attempt = mods.Where(m => !failed.Contains(m)).ToList();
                var failedNow = TryApply(attempt, values, seats, cache, out files);
                if (failedNow.Count == 0) { applied = attempt; break; }
                failed.AddRange(failedNow);
            }
            _failed = failed;
            _applied = applied;
            Factions.OnApplied(applied);
            _appliedOptions = values.Where(kv => applied.Any(m => m.Id == kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            _appliedAis = seats;
            _currentHash = SafeHash();
            var withOptions = values.Count == 0 ? "" :
                " Options: " + string.Join("; ", values.Select(kv => kv.Key + " " + string.Join(", ", kv.Value.Select(o => o.Key + "=" + o.Value)))) + ".";
            var withAis = seats.Count == 0 || applied.Count == 0 ? "" :
                " AI seats: " + string.Join(", ", seats.OrderBy(s => s.army).Select(s => $"army {s.army} plays {s.mod}/{s.key}")) + ".";
            ModApiPlugin.Log.LogInfo(applied.Count == 0
                ? $"Lua overlay: vanilla (hash {Short(_currentHash)})."
                : $"Lua overlay: {files} file(s) from {string.Join(", ", applied)}; Lua hash {Short(_currentHash)}.{withOptions}{withAis}");
            return true;
        }

        /// One pass of Apply over a restored cache: replacements, then the
        /// framework and options files, then appends. Returns the mods that
        /// threw part-way (their files are already in the cache).
        private static List<ModInfo> TryApply(IList<ModInfo> mods, Dictionary<string, Dictionary<string, string>> values,
            List<AiSeat> seats, Dictionary<string, NativeArray<byte>> cache, out int files)
        {
            files = 0;
            var failed = new List<ModInfo>();
            foreach (var appends in new[] { false, true })
            {
                if (appends)
                {
                    // Between the two passes, so a mod's own appends can
                    // already Import its options and the match events.
                    if (mods.Count > 0) files += ApplyFramework(mods, seats, cache, failed);
                    foreach (var mod in mods.Where(m => values.ContainsKey(m.Id)))
                    {
                        try
                        {
                            PutFile(cache, OptionValues.LuaPath(mod.Id).Replace('/', '\\'), OptionValues.LuaFile(mod, values[mod.Id]));
                            files++;
                        }
                        catch (Exception e)
                        {
                            ModApiPlugin.Log.LogError($"Writing the options of '{mod.Name}' failed: {e.Message}");
                            if (!failed.Contains(mod)) failed.Add(mod);
                        }
                    }
                }
                foreach (var mod in mods)
                {
                    if (failed.Contains(mod)) continue;
                    try
                    {
                        files += ApplyMod(mod, cache, appends);
                    }
                    catch (Exception e)
                    {
                        ModApiPlugin.Log.LogError($"Applying gameplay mod '{mod.Name}' failed part-way, so it is left out: {e.Message}");
                        failed.Add(mod);
                    }
                }
            }
            return failed;
        }

        private static List<ModInfo> _failed = new List<ModInfo>();

        /// The mods the last Apply was asked for but couldn't put on whole
        /// (an unreadable file, say; the log says which). They are left out
        /// entirely, and are not in <see cref="Applied"/>.
        public static IReadOnlyList<ModInfo> Failed => _failed;

        internal static void Clear()
        {
            // Only ever called outside a match, so the art can go too.
            Packs.Unmount();
            if (_applied.Count == 0 && _appliedToDict == null) return;
            Apply(Array.Empty<ModInfo>());
        }

        /// The game rebuilt its cache (the debug file watcher, or a future
        /// engine change): our entries went with the old dictionary. Returns
        /// true when the overlay needs putting back.
        internal static bool CacheWasRebuilt =>
            _appliedToDict != null && !ReferenceEquals(_appliedToDict, EM.Lua.FilesCache.pathToFileContents);

        internal static void Reestablish()
        {
            var mods = _applied.ToList();
            var options = _appliedOptions;
            var ais = _appliedAis;
            ForgetDict();
            _applied = new List<ModInfo>();
            _appliedAis = new List<AiSeat>();
            Apply(mods, options, ais);
        }

        private static int ApplyMod(ModInfo mod, Dictionary<string, NativeArray<byte>> cache, bool appends)
        {
            var count = 0;
            var root = LuaRoot;
            foreach (var rel in mod.OverlayFiles)
            {
                if (IsAppend(rel, out var targetRel) != appends) continue;
                var target = Path.GetFullPath(Path.Combine(root, targetRel));
                // Symlinks or ".." in a mod folder must not reach outside LJ\lua.
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    ModApiPlugin.Log.LogWarning($"Mod '{mod.Name}': skipped '{rel}' (resolves outside LJ\\lua).");
                    continue;
                }

                var bytes = File.ReadAllBytes(mod.SourcePath(rel));
                var name = targetRel.Replace('\\', '/');
                if (appends)
                {
                    if (AppendTo(cache, targetRel, bytes, $"Mod '{mod.Name}': {rel}")) count++;
                    continue;
                }
                if (target.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    var error = LuaSyntax.Check(bytes, name);
                    if (error != null)
                        ModApiPlugin.Log.LogError($"Mod '{mod.Name}': {rel} doesn't compile: {error}. The match will fail when the game loads it.");
                }

                PutFile(cache, targetRel, bytes);
                count++;
            }
            return count;
        }

        /// Adds code to the end of a file already in the cache (see
        /// AppendLua), checking the result compiles. `who` names the source
        /// in the log. False when the file isn't there.
        private static bool AppendTo(Dictionary<string, NativeArray<byte>> cache, string targetRel, byte[] addition, string who)
        {
            var target = Path.GetFullPath(Path.Combine(LuaRoot, targetRel));
            var name = targetRel.Replace('\\', '/');
            if (!cache.TryGetValue(target, out var existing))
            {
                ModApiPlugin.Log.LogWarning($"{who} appends to {name}, which the game doesn't have; skipped.");
                return false;
            }
            var original = existing.ToArray();
            var combined = AppendLua(original, addition, beforeReturn: true);
            var error = LuaSyntax.Check(combined, name);
            if (error != null)
            {
                // A file whose closing return the scan misread: try the
                // plain end of the file before giving up.
                var atEnd = AppendLua(original, addition, beforeReturn: false);
                if (LuaSyntax.Check(atEnd, name) == null) { combined = atEnd; error = null; }
            }
            if (error != null)
                ModApiPlugin.Log.LogError($"{who} doesn't compile once appended to {name}: {error}. " +
                                          "The match will fail when the game loads that file.");
            PutFile(cache, targetRel, combined);
            return true;
        }

        // ---- the framework's own Lua ----------------------------------------

        internal const string EventsPath = "modapi/events.lua";

        private static readonly Dictionary<string, byte[]> Resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// One of the framework's Lua files, shipped inside this DLL so every
        /// player with the same API has the same bytes.
        internal static byte[] Resource(string name)
        {
            if (Resources.TryGetValue(name, out var cached)) return cached;
            using (var s = typeof(Overlay).Assembly.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                if (s == null) throw new InvalidOperationException($"{name} is missing from Sanctuary.ModApi.dll");
                s.CopyTo(ms);
                // LF whatever Git did on the machine that built it: players'
                // copies of the API must give the same bytes, or the same
                // mods would hash differently.
                var text = Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").TrimStart('﻿');
                return Resources[name] = Encoding.UTF8.GetBytes(text);
            }
        }

        private static byte[] EventsLua() => Resource("modapi.events.lua");

        internal const string UiPath = "modapi/ui.lua";

        /// Whether any picked mod's Lua names the UI file. It only goes in
        /// for those, so a match whose mods don't draw panels has the same
        /// files (and Lua hash, and replays) as before the UI existed.
        private static bool UsesUi(IList<ModInfo> mods)
        {
            foreach (var mod in mods)
                foreach (var rel in mod.OverlayFiles)
                {
                    if (!rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;
                    // An AI folder's files are listed under the folder they're overlaid at.
                    var prefix = mod.AiPrefix;
                    var onDisk = prefix != null && rel.StartsWith(prefix, StringComparison.Ordinal) ? rel.Substring(prefix.Length) : rel;
                    try
                    {
                        if (File.ReadAllText(Path.Combine(mod.LuaRootPath, onDisk)).Contains(UiPath)) return true;
                    }
                    catch { }
                }
            return false;
        }

        private const string HookPrefix = "modapi.hooks.";

        /// The framework's appends to game files, by group ("factions",
        /// "units"): (game file under LJ\lua, resource name).
        private static List<(string target, string resource)> Hooks(string group)
        {
            var prefix = HookPrefix + group + "/";
            return typeof(Overlay).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => (n.Substring(prefix.Length).Replace('/', '\\'), n))
                .ToList();
        }

        /// Faction support, whenever a picked mod adds a faction: the
        /// factions file, the hooks that read it, and the stock AI's faction
        /// tables (in the game's AI and in any AI a mod ships) pointed at it.
        private static int ApplyFactions(IList<ModInfo> mods, Dictionary<string, NativeArray<byte>> cache)
        {
            var layout = Factions.Layout(mods);
            if (layout.Count == 0) return 0;
            var count = 0;
            var lua = Factions.Lua(layout);
            var error = LuaSyntax.Check(lua, Factions.LuaPath);
            if (error != null) throw new InvalidOperationException($"{Factions.LuaPath} doesn't compile: {error}");
            PutFile(cache, Factions.LuaPath.Replace('/', '\\'), lua);
            count++;
            foreach (var (target, resource) in Hooks("factions"))
                if (AppendTo(cache, target, Resource(resource), "Mod API faction hooks")) count++;

            var aiRoot = Path.Combine(LuaRoot, "AI") + Path.DirectorySeparatorChar;
            var patched = new List<string>();
            foreach (var key in cache.Keys.Where(k => k.StartsWith(aiRoot, StringComparison.OrdinalIgnoreCase) &&
                                                      k.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var text = Factions.PatchAiTables(Encoding.UTF8.GetString(cache[key].ToArray()));
                if (text == null) continue;
                var rel = key.Substring(LuaRoot.Length + 1);
                PutFile(cache, rel, Encoding.UTF8.GetBytes(text));
                patched.Add(rel.Replace('\\', '/'));
                count++;
            }
            ModApiPlugin.Log.LogInfo($"Factions: {string.Join(", ", layout.Select(f => $"{f.Label} = {f.Value}"))}. " +
                                     $"AI faction tables patched in {patched.Count} file(s).");
            return count;
        }

        private static readonly System.Text.RegularExpressions.Regex UnitTemplatePath = new System.Text.RegularExpressions.Regex(
            @"^common[\\/]units[\\/]unitsTemplates[\\/]([A-Za-z0-9_-]+)[\\/]\1\.santp$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// The unit ids a mod ships templates for.
        private static IEnumerable<string> UnitIds(ModInfo mod) =>
            mod.OverlayFiles.Select(f => UnitTemplatePath.Match(f)).Where(m => m.Success).Select(m => m.Groups[1].Value);

        /// Support for mods' own units: modelTpId honoured by placement
        /// ghosts, portraits, wrecks and hierarchy maps.
        private static int ApplyUnits(IList<ModInfo> mods, Dictionary<string, NativeArray<byte>> cache)
        {
            if (!mods.Any(m => m.SantpCount > 0 || m.Manifest.Factions.Count > 0)) return 0;
            var count = 0;
            PutFile(cache, Factions.UnitsLuaPath.Replace('/', '\\'), Factions.UnitsLua(mods));
            count++;
            foreach (var (target, resource) in Hooks("units"))
                if (AppendTo(cache, target, Resource(resource), "Mod API unit hooks")) count++;

            // The AI builds only what AvailableUnits lists ("if the unit isn't
            // on this list, it's handled as restricted"). Mods' own units go
            // on it; a mod's own append runs after this one, so it can still
            // take a unit off.
            var ids = mods.SelectMany(UnitIds).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (ids.Count > 0)
            {
                var sb = new StringBuilder("-- Sanctuary Mod API: the picked gameplay mods' units, which the AI may build.\n");
                foreach (var id in ids)
                    sb.Append("if AvailableUnits[").Append(OptionValues.LuaString(id)).Append("] == nil then AvailableUnits[")
                        .Append(OptionValues.LuaString(id)).Append("] = true end\n");
                if (AppendTo(cache, @"common\units\availableUnits.lua", Encoding.UTF8.GetBytes(sb.ToString()), "Mod API unit list")) count++;
            }
            return count;
        }

        /// The end of host/hostMain.lua or client/clientMain.lua: hook the
        /// match events in, then import each picked mod's script for that
        /// side, in pick order. A script that fails to load is logged and
        /// the rest still load.
        internal static byte[] MainAppend(string side, IEnumerable<string> scripts)
        {
            var sb = new StringBuilder();
            sb.Append("-- Sanctuary Mod API: match events, and the picked gameplay mods' ").Append(side).Append(" scripts.\n");
            sb.Append("Import(\"").Append(EventsPath).Append("\").Events._Install(\"").Append(side).Append("\")\n");
            sb.Append("local function runModScript(path)\n");
            sb.Append("    local ok, err = xpcall(Import, debug.traceback, path)\n");
            sb.Append("    if not ok then Warn(\"[Mod API] \" .. path .. \" failed to load: \" .. tostring(err)) end\n");
            sb.Append("end\n");
            foreach (var s in scripts) sb.Append("runModScript(").Append(OptionValues.LuaString(s)).Append(")\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// AI support, whenever a picked mod brings an AI (its own, or a
        /// faction's): the shared AI functions those AIs expect from newer
        /// games, and when seats are given mods' AIs, the file saying which
        /// plays which and the hook that routes them.
        private static int ApplyAis(IList<ModInfo> mods, List<AiSeat> seats, Dictionary<string, NativeArray<byte>> cache)
        {
            if (!mods.Any(m => m.Manifest.Ais.Count > 0 || m.Manifest.Factions.Any(f => f.Ai != null))) return 0;
            var count = 0;
            foreach (var (target, resource) in Hooks("aicompat"))
                if (AppendTo(cache, target, Resource(resource), "Mod API AI compatibility")) count++;
            var resolved = Ais.Resolve(seats, mods);
            if (resolved.Count == 0) return count;
            var lua = Ais.Lua(resolved);
            var error = LuaSyntax.Check(lua, Ais.LuaPath);
            if (error != null) throw new InvalidOperationException($"{Ais.LuaPath} doesn't compile: {error}");
            PutFile(cache, Ais.LuaPath.Replace('/', '\\'), lua);
            count++;
            foreach (var (target, resource) in Hooks("ai"))
                if (AppendTo(cache, target, Resource(resource), "Mod API AI seat hooks")) count++;
            return count;
        }

        /// The framework's files, whenever any gameplay mod is applied.
        private static int ApplyFramework(IList<ModInfo> mods, List<AiSeat> seats, Dictionary<string, NativeArray<byte>> cache, List<ModInfo> failed)
        {
            var count = 0;
            try
            {
                PutFile(cache, EventsPath.Replace('/', '\\'), EventsLua());
                count++;
                if (UsesUi(mods))
                {
                    PutFile(cache, UiPath.Replace('/', '\\'), Resource("modapi.ui.lua"));
                    count++;
                }
                if (AppendTo(cache, @"host\hostMain.lua", MainAppend("host", mods.Select(m => m.Manifest.HostScript).Where(s => s.Length > 0)), "Mod API host hooks")) count++;
                if (AppendTo(cache, @"client\clientMain.lua", MainAppend("client", mods.Select(m => m.Manifest.ClientScript).Where(s => s.Length > 0)), "Mod API client hooks")) count++;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError($"Mod API match events couldn't be put in place: {e.Message}");
            }
            try { count += ApplyUnits(mods, cache); }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Mod API unit support couldn't be put in place: {e.Message}"); }
            try { count += ApplyFactions(mods, cache); }
            catch (Exception e)
            {
                // Without it their factions don't exist in the match: leave
                // those mods out rather than start one that can't spawn them.
                ModApiPlugin.Log.LogError($"Mod API faction support couldn't be put in place, so the mods adding factions are left out: {e.Message}");
                foreach (var m in mods.Where(m => m.Manifest.Factions.Count > 0 && !failed.Contains(m))) failed.Add(m);
            }
            // After the factions, which point every AI's faction tables
            // (mods' AIs' included) at the full list.
            try { count += ApplyAis(mods, seats, cache); }
            catch (Exception e)
            {
                // Seats would silently play the game's AI on one machine and
                // the mod's elsewhere: leave the AI mods out instead.
                ModApiPlugin.Log.LogError($"Mod API AI support couldn't be put in place, so the mods adding AIs are left out: {e.Message}");
                foreach (var m in mods.Where(m => m.Manifest.Ais.Count > 0 && !failed.Contains(m))) failed.Add(m);
            }
            return count;
        }

        /// Sets one cache entry (targetRel is relative to LJ\lua), keeping
        /// what's needed to put the original back.
        private static void PutFile(Dictionary<string, NativeArray<byte>> cache, string targetRel, byte[] bytes)
        {
            var target = Path.GetFullPath(Path.Combine(LuaRoot, targetRel));
            var exists = cache.TryGetValue(target, out var existing);
            var arr = new NativeArray<byte>(bytes, Allocator.Persistent);
            Allocated.Add(arr);

            if (exists)
            {
                // Stash only the true original: a key another mod already
                // touched has its pristine copy (or none, if added) stashed.
                if (!Pristine.ContainsKey(target) && !Added.Contains(target))
                    Pristine[target] = existing;
            }
            else
            {
                Added.Add(target);
                RegisterFolders(targetRel);
            }
            cache[target] = arr;
        }

        /// The game file with a mod's code added in the same chunk, so the
        /// added code sees the file's locals and can change what it defines.
        /// Many game files end in a top-level `return { ... }`, after which Lua
        /// allows nothing, so the code goes in just before that return,
        /// where changes to the file's functions still reach the returned
        /// table. Each addition gets its own do-block, so its locals can't
        /// clash with the file's or another mod's.
        internal static byte[] AppendLua(byte[] original, byte[] addition, bool beforeReturn = true)
        {
            var text = Encoding.UTF8.GetString(original);
            var add = Encoding.UTF8.GetString(addition);
            if (add.Length > 0 && add[0] == '﻿') add = add.Substring(1);
            var block = "\n-- [SanctuaryMods append]\ndo\n" + add + "\nend\n";
            var at = beforeReturn ? FinalReturn(text) : -1;
            var combined = at < 0 ? text + block : text.Substring(0, at) + block + text.Substring(at);
            return Encoding.UTF8.GetBytes(combined);
        }

        /// Where a file's closing top-level `return` starts, or -1: a line
        /// starting with `return`, followed by nothing but its own indented
        /// or closing lines, blank lines and comments.
        internal static int FinalReturn(string text)
        {
            var lines = text.Split('\n');
            var offset = text.Length;
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                offset -= line.Length + (i < lines.Length - 1 ? 1 : 0);
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("return", StringComparison.Ordinal) &&
                    (trimmed.Length == 6 || !char.IsLetterOrDigit(trimmed[6]) && trimmed[6] != '_'))
                    return offset;
                var t = trimmed.TrimStart();
                // "return" alone on its line, then "{", fields and "}" below it,
                // is how a lot of the game's files end.
                var partOfTail = t.Length == 0 || t.StartsWith("--") || t.StartsWith("{") || t.StartsWith("}") ||
                                 t.StartsWith(")") || char.IsWhiteSpace(trimmed[0]);
                if (!partOfTail) return -1;
            }
            return -1;
        }

        private static void RestoreAll()
        {
            if (_appliedToDict != null && ReferenceEquals(_appliedToDict, EM.Lua.FilesCache.pathToFileContents))
            {
                foreach (var kv in Pristine) _appliedToDict[kv.Key] = kv.Value;
                foreach (var key in Added) _appliedToDict.Remove(key);
                // Drop our folder registrations by rebuilding the index from disk.
                if (Added.Count > 0)
                {
                    try { RebuildDirIndexMi?.Invoke(null, null); }
                    catch (Exception e) { ModApiPlugin.Log.LogWarning($"Directory index rebuild failed: {e.Message}"); }
                }
            }
            ForgetDict();
            _applied = new List<ModInfo>();
            _appliedOptions = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }

        private static void ForgetDict()
        {
            // If the game reassigned the cache since we applied, our entries
            // went with the old dictionary — nothing references these arrays.
            foreach (var arr in Allocated)
            {
                try { if (arr.IsCreated) arr.Dispose(); } catch { }
            }
            Pristine.Clear();
            Added.Clear();
            Allocated.Clear();
            _appliedToDict = null;
        }

        private static void RegisterFolders(string rel)
        {
            if (!(DirIndexField?.GetValue(null) is Dictionary<string, HashSet<string>> dirIndex)) return;
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = LuaRoot;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!dirIndex.TryGetValue(parent, out var set))
                    dirIndex[parent] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(parts[i]);
                parent = Path.Combine(parent, parts[i]);
            }
        }

        private static string SafeHash()
        {
            try { return EM.Lua.FilesCache.ComputeLuaHashString(); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Lua hash compute failed: {e.Message}");
                return "?";
            }
        }

        public static string Short(string hash) =>
            string.IsNullOrEmpty(hash) ? "…" : (hash.Length > 12 ? hash.Substring(0, 12) : hash);
    }
}
