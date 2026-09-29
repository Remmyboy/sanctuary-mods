using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Sanctuary.ModApi
{
    /// One commander a faction can start with: its own entry in the lobby's
    /// faction dropdown, and its own faction number in the match.
    public sealed class CommanderDef
    {
        public string Name { get; internal set; }
        /// The starting unit's template id.
        public string Unit { get; internal set; }
    }

    /// The AI a faction's AI armies play: a folder laid out like the stock
    /// AI/mods/AI-Sanctuary-Rush, usually a copy of it in the mod's lua\.
    public sealed class FactionAi
    {
        /// Under LJ\lua, forward slashes: "AI/mods/AI-Dycom".
        public string Folder { get; internal set; }
        public string Name => Folder.Substring(Folder.LastIndexOf('/') + 1);
        public bool Land { get; internal set; } = true;
        public bool Air { get; internal set; } = true;
        public bool Naval { get; internal set; }
    }

    /// One entry of mod.json's "factions": a faction players pick in the
    /// lobby's faction dropdown while the host has the mod picked.
    ///
    /// <code>
    /// "factions": [{
    ///   "key": "dycom", "name": "Dycom", "tag": "DYCOM", "unitPrefix": "ud",
    ///   "icon": "icons/dycom.png", "looksLike": "CHOSEN",
    ///   "commanders": [ { "name": "Spider", "unit": "udl0000" }, { "name": "Mech", "unit": "udl0001" } ],
    ///   "ai": { "folder": "AI/mods/AI-Dycom", "air": false, "naval": false }
    /// }]
    /// </code>
    public sealed class FactionDef
    {
        internal const int MaxFactions = 8;
        internal const int MaxCommanders = 8;

        /// The mod's own name for it: letters, digits, '_' and '-'.
        public string Key { get; internal set; }
        /// Shown in the lobby, and FactionsData's name.
        public string Name { get; internal set; }
        /// The faction tag every unit of the faction carries (Tags.DYCOM).
        public string Tag { get; internal set; }
        /// The two letters its unit ids start with ("ud").
        public string UnitPrefix { get; internal set; }
        /// A PNG in the mod folder, shown beside the name in the lobby.
        /// Empty for none.
        public string Icon { get; internal set; } = "";
        /// "EDA", "CHOSEN" or "GUARD": whose shields, build beams, factory
        /// platforms and adjacency effects the faction's units borrow. Empty
        /// for the game's default (EDA's, with a log line per unit).
        public string LooksLike { get; internal set; } = "";
        /// At least one.
        public IReadOnlyList<CommanderDef> Commanders { get; internal set; } = Array.Empty<CommanderDef>();
        /// Null: AI armies of the faction play the stock AI.
        public FactionAi Ai { get; internal set; }

        private static readonly Regex KeyPattern = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);
        private static readonly Regex TagPattern = new Regex("^[A-Z][A-Z0-9_]{0,31}$", RegexOptions.Compiled);
        private static readonly Regex PrefixPattern = new Regex("^[a-z]{2}$", RegexOptions.Compiled);
        private static readonly Regex UnitPattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
        internal static readonly string[] StockTags = { "EDA", "CHOSEN", "GUARD" };
        private static readonly string[] StockPrefixes = { "ue", "uc", "ug" };

        /// What goes into the mod's content hash: everything the match's Lua
        /// is made from (the icon is only the lobby's).
        internal string Signature() =>
            string.Join("|", Key, Name, Tag, UnitPrefix, LooksLike,
                string.Join(",", Commanders.Select(c => c.Name + "=" + c.Unit)),
                Ai == null ? "-" : Ai.Folder + ":" + Ai.Land + Ai.Air + Ai.Naval);

        internal static IReadOnlyList<FactionDef> ParseAll(JToken token, List<string> problems)
        {
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<FactionDef>();
            if (!(token is JArray arr))
            {
                problems.Add("mod.json: factions must be a list");
                return Array.Empty<FactionDef>();
            }
            var result = new List<FactionDef>();
            foreach (var t in arr)
            {
                if (result.Count == MaxFactions)
                {
                    problems.Add($"mod.json: more than {MaxFactions} factions; the rest are ignored");
                    break;
                }
                if (!(t is JObject o))
                {
                    problems.Add("mod.json: each faction must be an object");
                    continue;
                }
                var f = Parse(o, problems);
                if (f == null) continue;
                if (result.Any(x => x.Key == f.Key || x.Tag == f.Tag))
                {
                    problems.Add($"mod.json: faction '{f.Key}' repeats another's key or tag; ignored");
                    continue;
                }
                result.Add(f);
            }
            return result;
        }

        private static FactionDef Parse(JObject o, List<string> problems)
        {
            var tag = (Str(o, "tag") ?? "").Trim();
            var who = $"faction '{Str(o, "name") ?? tag}'";
            if (!TagPattern.IsMatch(tag))
            {
                problems.Add($"mod.json: {who} needs a tag of capital letters, digits and _ (like \"DYCOM\"); faction ignored");
                return null;
            }
            if (StockTags.Contains(tag))
            {
                problems.Add($"mod.json: {who} uses the game's own tag {tag}; faction ignored");
                return null;
            }
            var prefix = (Str(o, "unitPrefix") ?? "").Trim();
            if (!PrefixPattern.IsMatch(prefix) || StockPrefixes.Contains(prefix))
            {
                problems.Add($"mod.json: {who} needs a unitPrefix of two small letters that isn't ue, uc or ug (like \"ud\"); faction ignored");
                return null;
            }
            var f = new FactionDef
            {
                Tag = tag,
                UnitPrefix = prefix,
                Name = Clip((Str(o, "name") ?? "").Trim(), 32),
                Key = (Str(o, "key") ?? tag.ToLowerInvariant()).Trim(),
            };
            if (f.Name.Length == 0) f.Name = tag.Substring(0, 1) + tag.Substring(1).ToLowerInvariant();
            if (!KeyPattern.IsMatch(f.Key))
            {
                problems.Add($"mod.json: {who}'s key '{f.Key}' must be 1-32 of a-z 0-9 _ -; using '{tag.ToLowerInvariant()}'");
                f.Key = tag.ToLowerInvariant();
            }

            var icon = (Str(o, "icon") ?? "").Trim().Replace('\\', '/').TrimStart('/');
            if (icon.Length > 0)
            {
                if (icon.Contains("..") || Path.IsPathRooted(icon) || icon.Contains(":") ||
                    !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"mod.json: {who}'s icon '{icon}' must be a .png inside the mod folder; no icon");
                else f.Icon = icon;
            }

            var look = (Str(o, "looksLike") ?? "").Trim().ToUpperInvariant();
            if (look.Length > 0)
            {
                if (StockTags.Contains(look)) f.LooksLike = look;
                else problems.Add($"mod.json: {who}'s looksLike '{look}' must be EDA, CHOSEN or GUARD; using the game's default");
            }

            var commanders = new List<CommanderDef>();
            if (o["commanders"] is JArray list)
            {
                foreach (var c in list)
                {
                    if (commanders.Count == MaxCommanders)
                    {
                        problems.Add($"mod.json: {who} has more than {MaxCommanders} commanders; the rest are ignored");
                        break;
                    }
                    var unit = (c.Type == JTokenType.String ? (string)c : Str(c as JObject, "unit") ?? "").Trim();
                    var name = c is JObject co ? Clip((Str(co, "name") ?? "").Trim(), 24) : "";
                    if (!UnitPattern.IsMatch(unit))
                    {
                        problems.Add($"mod.json: {who} has a commander without a unit id; skipped");
                        continue;
                    }
                    if (commanders.Any(x => x.Unit == unit)) continue;
                    commanders.Add(new CommanderDef { Unit = unit, Name = name.Length > 0 ? name : unit });
                }
            }
            else
            {
                var unit = (Str(o, "commander") ?? "").Trim();
                if (UnitPattern.IsMatch(unit)) commanders.Add(new CommanderDef { Unit = unit, Name = unit });
            }
            if (commanders.Count == 0)
            {
                problems.Add($"mod.json: {who} needs a starting unit: \"commander\": \"<unit id>\", or a \"commanders\" list; faction ignored");
                return null;
            }
            f.Commanders = commanders;

            var ai = o["ai"];
            if (ai != null && ai.Type != JTokenType.Null && !(ai.Type == JTokenType.Boolean && !(bool)ai))
            {
                var folder = (ai.Type == JTokenType.String ? (string)ai : Str(ai as JObject, "folder") ?? "").Trim()
                    .Replace('\\', '/').Trim('/');
                if (folder.StartsWith("lua/", StringComparison.OrdinalIgnoreCase)) folder = folder.Substring(4);
                if (folder.Length == 0 || folder.Contains("..") || folder.Contains(":") || folder.IndexOfAny(new[] { '"', '\n', '\r' }) >= 0)
                {
                    problems.Add($"mod.json: {who}'s ai needs a folder under the mod's lua folder (like \"AI/mods/AI-Dycom\"); its AI armies play the stock AI");
                }
                else
                {
                    var a = new FactionAi { Folder = folder };
                    if (ai is JObject aio)
                    {
                        a.Land = Bool(aio, "land") ?? true;
                        a.Air = Bool(aio, "air") ?? true;
                        a.Naval = Bool(aio, "naval") ?? Bool(aio, "water") ?? false;
                    }
                    f.Ai = a;
                }
            }
            return f;
        }

        private static string Str(JObject o, string key)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            var s = t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 300 ? s.Substring(0, 300) : s;
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

    /// One entry of the lobby's faction dropdown past the game's three.
    public sealed class LobbyFaction
    {
        /// The lobby's faction value (the dropdown index): 3 and up.
        public int Value { get; internal set; }
        /// The faction number the match's Lua sees (FactionsData's index).
        public int Index => Value + 1;
        public ModInfo Mod { get; internal set; }
        public FactionDef Faction { get; internal set; }
        public CommanderDef Commander { get; internal set; }
        /// The faction's first commander, which is the faction's own entry;
        /// other commanders are variants of it.
        public bool IsFirstCommander { get; internal set; }
        /// The value of the faction's first commander.
        public int BaseValue { get; internal set; }
        /// What the dropdown shows: "Dycom", or "Dycom (Mech)" for a further
        /// commander.
        public string Label => IsFirstCommander || Faction.Commanders.Count < 2
            ? Faction.Name
            : Faction.Name + " (" + Commander.Name + ")";
        internal string Identity => Mod.Id + "|" + Faction.Key + "|" + Commander.Unit;
    }

    /// The factions gameplay mods add, as the lobby and the match number
    /// them: after the game's three, in the host's pick order, each faction's
    /// commanders in the order its mod.json lists them. Every player works
    /// this out from the same pick, so it's the same everywhere.
    public static class Factions
    {
        private static readonly string[] StockNames = { "EDA", "Chosen", "Guard" };
        /// The lobby sends a faction as one byte.
        private const int MaxValue = 255;

        private static List<LobbyFaction> _current = new List<LobbyFaction>();
        private static int _version;

        /// The modded factions in the lobby's dropdown now, by value. Empty
        /// when no picked mod adds one.
        public static IReadOnlyList<LobbyFaction> Current => _current;

        /// Bumped whenever <see cref="Current"/> changes.
        public static int Version => _version;

        /// The lobby's name for a faction value: the game's three, a modded
        /// faction's label, or "Faction n" for one this machine doesn't know.
        public static string NameOf(int value)
        {
            if (value >= 0 && value < StockNames.Length) return StockNames[value];
            return _current.FirstOrDefault(f => f.Value == value)?.Label ?? "Faction " + (value + 1);
        }

        /// The value of a mod's faction (its first commander), or -1 when
        /// that mod isn't picked or has no such faction.
        public static int ValueOf(string modId, string key) =>
            _current.FirstOrDefault(f => f.Mod.Id == modId && f.Faction.Key == key && f.IsFirstCommander)?.Value ?? -1;

        internal static List<LobbyFaction> Layout(IEnumerable<ModInfo> mods)
        {
            var result = new List<LobbyFaction>();
            var value = StockNames.Length;
            var tags = new HashSet<string>(FactionDef.StockTags, StringComparer.Ordinal);
            foreach (var mod in mods)
            {
                foreach (var f in mod.Manifest.Factions)
                {
                    // Two picked mods with one tag would be one faction to
                    // the game's Lua: the later one is left out.
                    if (!tags.Add(f.Tag))
                    {
                        ModApiPlugin.Log.LogWarning($"Faction {f.Name} of '{mod.Name}' uses the tag {f.Tag}, which another picked mod's faction has; left out.");
                        continue;
                    }
                    if (value + f.Commanders.Count - 1 > MaxValue) break;
                    var baseValue = value;
                    for (var i = 0; i < f.Commanders.Count; i++)
                    {
                        result.Add(new LobbyFaction
                        {
                            Value = value++, Mod = mod, Faction = f, Commander = f.Commanders[i],
                            IsFirstCommander = i == 0, BaseValue = baseValue,
                        });
                    }
                }
            }
            return result;
        }

        /// The overlay now holds these mods. Called on every machine, the
        /// host's included, whenever the applied set changed.
        internal static void OnApplied(IList<ModInfo> mods)
        {
            var next = Layout(mods);
            if (next.Select(f => f.Identity + "@" + f.Value).SequenceEqual(_current.Select(f => f.Identity + "@" + f.Value)) &&
                next.Select(f => f.Label).SequenceEqual(_current.Select(f => f.Label)))
                return;
            var previous = _current;
            _current = next;
            _version++;
            try { FactionLobby.OnFactionsChanged(previous, next); }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Lobby factions: {e.Message}"); }
        }

        // ---- the match's Lua ---------------------------------------------------

        internal const string LuaPath = "modapi/factions.lua";
        internal const string UnitsLuaPath = "modapi/units.lua";

        /// modapi/factions.lua: the factions as data, then the helpers the
        /// framework's hooks and mods' own Lua use.
        internal static byte[] Lua(IList<LobbyFaction> layout)
        {
            var sb = new StringBuilder();
            sb.Append("-- Sanctuary Mod API: the factions the picked gameplay mods add, numbered the way\n");
            sb.Append("-- the lobby numbers them. Written by the Mod API; the same on every player's machine.\n");
            sb.Append("List = {\n");
            foreach (var f in layout)
            {
                sb.Append("    { index = ").Append(f.Index).Append(", base = ").Append(f.BaseValue + 1);
                sb.Append(", mod = ").Append(OptionValues.LuaString(f.Mod.Id));
                sb.Append(", key = ").Append(OptionValues.LuaString(f.Faction.Key));
                sb.Append(", name = ").Append(OptionValues.LuaString(f.Faction.Name));
                sb.Append(", tag = ").Append(OptionValues.LuaString(f.Faction.Tag));
                sb.Append(", tpLetter = ").Append(OptionValues.LuaString(f.Faction.UnitPrefix));
                sb.Append(", initialUnit = ").Append(OptionValues.LuaString(f.Commander.Unit));
                sb.Append(", commander = ").Append(OptionValues.LuaString(f.Commander.Name));
                if (f.Faction.LooksLike.Length > 0) sb.Append(", looksLike = ").Append(OptionValues.LuaString(f.Faction.LooksLike));
                var ai = f.Faction.Ai;
                if (ai != null)
                {
                    sb.Append(", ai = { directory = ").Append(OptionValues.LuaString(ai.Folder));
                    sb.Append(", name = ").Append(OptionValues.LuaString(ai.Name));
                    sb.Append(", land = ").Append(ai.Land ? "true" : "false");
                    sb.Append(", air = ").Append(ai.Air ? "true" : "false");
                    sb.Append(", water = ").Append(ai.Naval ? "true" : "false").Append(" }");
                }
                sb.Append(" },\n");
            }
            sb.Append("}\n\n");
            sb.Append(Encoding.UTF8.GetString(Overlay.Resource("modapi.factions_lib.lua")));
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// modapi/units.lua: which unit portraits the picked mods' art packs
        /// bring, so a unit borrowing another's model keeps its own.
        internal static byte[] UnitsLua(IEnumerable<ModInfo> mods)
        {
            var ids = mods.SelectMany(m => m.PortraitIds).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("-- Sanctuary Mod API: unit portraits the picked gameplay mods' art packs bring.\n");
            sb.Append("OwnPortraits = {\n");
            foreach (var id in ids) sb.Append("    [").Append(OptionValues.LuaString(id)).Append("] = true,\n");
            sb.Append("}\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // The stock AI knows the three factions by a table written out in
        // several functions (so an append can't reach it). Every copy under
        // AI\ is pointed at the full list instead.
        private static readonly Regex AiFactionTable = new Regex(
            "\\{\\s*\\[1\\]\\s*=\\s*\"EDA\"\\s*,\\s*\\[2\\]\\s*=\\s*\"CHOSEN\"\\s*,\\s*\\[3\\]\\s*=\\s*\"GUARD\"\\s*,?\\s*\\}",
            RegexOptions.Compiled);

        internal const string AiFactionTableReplacement = "Import(\"modapi/factions.lua\").TagsByIndex()";

        /// The file with the AI's faction tables replaced, or null when it
        /// has none.
        internal static string PatchAiTables(string text)
        {
            if (text.IndexOf("\"GUARD\"", StringComparison.Ordinal) < 0) return null;
            var patched = AiFactionTable.Replace(text, AiFactionTableReplacement);
            return ReferenceEquals(patched, text) || patched == text ? null : patched;
        }
    }
}
