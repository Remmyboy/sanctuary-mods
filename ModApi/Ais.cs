using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Sanctuary.ModApi
{
    /// One AI a gameplay mod brings: a folder laid out like the game's
    /// AI/mods/AI-Sanctuary-Rush (AIPlatoonFunctions.lua, formers\,
    /// strategies\). The lobby host gives it to any AI seat from that seat's
    /// dropdown.
    ///
    /// <code>
    /// "ais": [
    ///   { "key": "duck", "name": "DuckAI", "folder": "AI/mods/IwillDUCKYOUup2",
    ///     "description": "Plays for map control.", "air": true, "naval": false }
    /// ]
    /// </code>
    public sealed class AiDef
    {
        internal const int MaxAis = 8;

        /// The mod's own name for it: a-z, 0-9, '_' and '-'.
        public string Key { get; internal set; }
        /// Shown in the AI seat's dropdown ("AI: DuckAI").
        public string Name { get; internal set; }
        public string Description { get; internal set; } = "";
        /// Under LJ\lua, forward slashes: "AI/mods/IwillDUCKYOUup2".
        public string Folder { get; internal set; }
        /// The game's name for the AI (its aiSettings.modName): the folder's
        /// last part, as the game's own AIs are named.
        public string ModName => Folder.Substring(Folder.LastIndexOf('/') + 1);
        public bool Land { get; internal set; } = true;
        public bool Air { get; internal set; } = true;
        public bool Naval { get; internal set; }

        /// What goes into the mod's content hash: what the match's Lua is
        /// made from (the name and description are only the lobby's).
        internal string Signature() => string.Join("|", Key, Folder, Land, Air, Naval);

        private static readonly Regex KeyPattern = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

        /// mod.json's "ais". `impliedFolder` is where an AI folder with a
        /// mod.json of its own goes (entries without a folder mean it, and
        /// no entries at all mean one AI named after the mod); null for a mod
        /// laid out like LJ\lua.
        internal static IReadOnlyList<AiDef> ParseAll(JToken token, List<string> problems, string impliedFolder, string modName)
        {
            var result = new List<AiDef>();
            if (token != null && token.Type != JTokenType.Null)
            {
                if (!(token is JArray arr)) problems.Add("mod.json: ais must be a list");
                else
                {
                    foreach (var t in arr)
                    {
                        if (result.Count == MaxAis)
                        {
                            problems.Add($"mod.json: more than {MaxAis} AIs; the rest are ignored");
                            break;
                        }
                        var a = Parse(t, problems, impliedFolder, modName);
                        if (a == null) continue;
                        if (result.Any(x => x.Key == a.Key || x.Folder.Equals(a.Folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            problems.Add($"mod.json: AI '{a.Name}' repeats another's key or folder; ignored");
                            continue;
                        }
                        result.Add(a);
                    }
                }
            }
            if (result.Count == 0 && impliedFolder != null)
            {
                // "AI-TheVirusNetwork" shows as "AI: TheVirusNetwork".
                var shown = Regex.Replace(modName, "^AI[-_ ]+(?=.)", "", RegexOptions.IgnoreCase);
                result.Add(new AiDef { Key = KeyFrom(shown), Name = Clip(shown, 40), Folder = impliedFolder });
            }
            return result;
        }

        private static AiDef Parse(JToken t, List<string> problems, string impliedFolder, string modName)
        {
            var o = t as JObject;
            if (o == null && t.Type != JTokenType.String)
            {
                problems.Add("mod.json: each AI must be an object (or a folder)");
                return null;
            }
            var rawFolder = (o == null ? (string)t : Str(o, "folder") ?? "").Trim().Replace('\\', '/').Trim('/');
            if (rawFolder.StartsWith("lua/", StringComparison.OrdinalIgnoreCase)) rawFolder = rawFolder.Substring(4);
            var name = Clip((o == null ? "" : Str(o, "name") ?? "").Trim(), 40);
            var who = $"AI '{(name.Length > 0 ? name : rawFolder)}'";
            string folder;
            if (rawFolder.Length == 0 || rawFolder == ".")
            {
                if (impliedFolder == null)
                {
                    problems.Add($"mod.json: {who} needs a folder under the mod's lua folder (like \"AI/mods/AI-Mine\"); ignored");
                    return null;
                }
                folder = impliedFolder;
            }
            else if (impliedFolder != null)
            {
                // The mod folder is the AI itself; nothing else is overlaid.
                problems.Add($"mod.json: {who}'s folder is ignored: this mod folder is an AI folder (it has AIPlatoonFunctions.lua), so the AI is the folder itself");
                folder = impliedFolder;
            }
            else if (rawFolder.Contains("..") || rawFolder.Contains(":") || rawFolder.IndexOfAny(new[] { '"', '\n', '\r' }) >= 0)
            {
                problems.Add($"mod.json: {who}'s folder '{rawFolder}' must be a folder under the mod's lua folder; ignored");
                return null;
            }
            else folder = rawFolder;

            var a = new AiDef { Folder = folder };
            a.Name = name.Length > 0 ? name : (impliedFolder != null ? Clip(modName, 40) : a.ModName);
            var key = (o == null ? null : Str(o, "key"))?.Trim();
            a.Key = key ?? KeyFrom(a.Name);
            if (!KeyPattern.IsMatch(a.Key))
            {
                if (key != null) problems.Add($"mod.json: {who}'s key '{key}' must be 1-32 of a-z 0-9 _ -; using '{KeyFrom(a.Name)}'");
                a.Key = KeyFrom(a.Name);
            }
            if (o != null)
            {
                a.Description = Clip((Str(o, "description") ?? "").Trim(), 300);
                a.Land = Bool(o, "land") ?? true;
                a.Air = Bool(o, "air") ?? true;
                a.Naval = Bool(o, "naval") ?? Bool(o, "water") ?? false;
            }
            return a;
        }

        internal static string KeyFrom(string name)
        {
            var k = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            if (k.Length > 32) k = k.Substring(0, 32);
            return k.Length == 0 ? "ai" : k;
        }

        private static string Str(JObject o, string key)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            var s = t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 400 ? s.Substring(0, 400) : s;
        }

        private static bool? Bool(JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return ModOption.ParseBool(t.ToString());
        }

        private static string Clip(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
    }

    /// An AI a mod on this machine brings, as the host's seat dropdowns
    /// offer it.
    public sealed class AvailableAi
    {
        public ModInfo Mod { get; internal set; }
        public AiDef Ai { get; internal set; }
        /// "AI: DuckAI", or with the mod's name when two AIs share a name.
        public string Label { get; internal set; }
    }

    /// AI mods: what's installed, what each AI seat plays, and the match's
    /// Lua that routes each seat to its AI.
    public static class Ais
    {
        /// A folder with this file at its root is an AI, as the game's
        /// AI\mods\AI-Sanctuary-Rush is.
        internal const string MarkerFile = "AIPlatoonFunctions.lua";

        internal static bool IsAiFolder(string dir) => File.Exists(Path.Combine(dir, MarkerFile));

        private static string GameAiMods => Path.Combine(BepInEx.Paths.GameRootPath, "LJ", "lua", "AI", "mods");

        /// Where a mod that is an AI folder goes under LJ\lua. Never onto one
        /// of the game's own AIs: a copy of one (a newer version, say) sits
        /// beside it instead of replacing it for every seat.
        internal static string OverlayFolderFor(string name)
        {
            var clean = Regex.Replace(name, "[^A-Za-z0-9 ._()-]", "_").Trim();
            if (clean.Length == 0) clean = "AI";
            if (clean.Length > 64) clean = clean.Substring(0, 64);
            var folder = "AI/mods/" + clean;
            if (Directory.Exists(Path.Combine(GameAiMods, clean))) folder += " (mod)";
            return folder;
        }

        /// The game's own AI folders by name ("AI-Sanctuary-Rush", ...).
        internal static bool IsGameAi(string name)
        {
            try { return Directory.Exists(Path.Combine(GameAiMods, name)); }
            catch { return false; }
        }

        private static IReadOnlyList<AvailableAi> _available = Array.Empty<AvailableAi>();
        private static int _availableFor = -1;

        /// Every AI the mods on this machine bring, in catalog order.
        public static IReadOnlyList<AvailableAi> Available
        {
            get
            {
                if (_availableFor == ModCatalog.Version) return _available;
                _availableFor = ModCatalog.Version;
                var list = ModCatalog.GameplayMods.SelectMany(m => m.Manifest.Ais.Select(a => new AvailableAi { Mod = m, Ai = a })).ToList();
                foreach (var x in list)
                    x.Label = "AI: " + (list.Count(y => y.Ai.Name == x.Ai.Name) > 1 ? $"{x.Ai.Name} ({x.Mod.Name})" : x.Ai.Name);
                return _available = list;
            }
        }

        public static AvailableAi Find(string modId, string key) =>
            Available.FirstOrDefault(a => a.Mod.Id == modId && a.Ai.Key == key);

        // ---- the match's Lua ---------------------------------------------------

        internal const string LuaPath = "modapi/ai.lua";

        /// The seats whose picked AI is among these applied mods, by army.
        internal static List<(AiSeat seat, AiDef ai)> Resolve(IEnumerable<AiSeat> seats, IList<ModInfo> mods)
        {
            var result = new List<(AiSeat, AiDef)>();
            if (seats == null) return result;
            foreach (var s in seats.OrderBy(s => s.army))
            {
                if (s.army <= 0 || result.Any(r => r.Item1.army == s.army)) continue;
                var ai = mods.FirstOrDefault(m => m.Id == s.mod)?.Manifest.Ais.FirstOrDefault(a => a.Key == s.key);
                if (ai == null)
                {
                    ModApiPlugin.Log.LogWarning($"AI seat {s.army}: {s.mod}/{s.key} isn't among the applied mods' AIs; it plays the game's default.");
                    continue;
                }
                result.Add((s, ai));
            }
            return result;
        }

        /// A stable string for "these seats play these AIs".
        internal static string Signature(IEnumerable<AiSeat> seats) =>
            seats == null ? "" : string.Join(";", seats.OrderBy(s => s.army).ThenBy(s => s.slot).Select(s => $"{s.slot}:{s.army}:{s.mod}/{s.key}"));

        /// modapi/ai.lua: which AI each army (by its lobby army number, the
        /// map start slot) plays.
        internal static byte[] Lua(List<(AiSeat seat, AiDef ai)> seats)
        {
            var sb = new StringBuilder();
            sb.Append("-- Sanctuary Mod API: the AI each AI seat plays, as the lobby host picked it, by the\n");
            sb.Append("-- seat's army number (its map start slot). Written by the Mod API; the same on every\n");
            sb.Append("-- player's machine. Seats not listed play the game's default AI.\n");
            sb.Append("Seats = {\n");
            foreach (var (seat, ai) in seats)
            {
                sb.Append("    [").Append(seat.army).Append("] = { directory = ").Append(OptionValues.LuaString(ai.Folder));
                sb.Append(", name = ").Append(OptionValues.LuaString(ai.ModName));
                sb.Append(", mod = ").Append(OptionValues.LuaString(seat.mod));
                sb.Append(", key = ").Append(OptionValues.LuaString(ai.Key));
                sb.Append(", land = ").Append(ai.Land ? "true" : "false");
                sb.Append(", air = ").Append(ai.Air ? "true" : "false");
                sb.Append(", water = ").Append(ai.Naval ? "true" : "false").Append(" },\n");
            }
            sb.Append("}\n\n");
            sb.Append("--- The AI picked for the army with this lobby army number, or nil for the game's default.\n");
            sb.Append("function ForArmy(armyID)\n    return Seats[armyID]\nend\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }
}
