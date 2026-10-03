using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace Sanctuary.ModApi
{
    /// One unit a player can build, as its template describes it.
    public sealed class UnitEntry
    {
        /// The template id ("uel1001"): what a units option stores.
        public string Id { get; internal set; }
        /// The unit's own name ("Puma"); empty for some (engineers).
        public string Name { get; internal set; }
        /// What it is, the same across factions ("Tier 1: Tank").
        public string Role { get; internal set; }
        /// The faction tag it carries ("EDA"), which decides who builds it.
        public string FactionTag { get; internal set; }
        /// The faction as the lobby names it ("EDA", "Chosen", "Dycom").
        public string FactionName { get; internal set; }
        /// 1 to 4, from its TECHn tag; 0 without one.
        public int Tech { get; internal set; }
        /// "land", "air", "naval" or "structure".
        public string Domain { get; internal set; }
        public bool Mobile { get; internal set; }
        public IReadOnlyCollection<string> Tags { get; internal set; } = Array.Empty<string>();
        /// The mod whose template this is; null for the game's own.
        public ModInfo Mod { get; internal set; }

        /// "EDA Puma", or "EDA" for a unit without a name.
        public string Label => Name.Length > 0 ? FactionName + " " + Name : FactionName;

        public override string ToString() => $"{Id} {Label} ({Role})";
    }

    /// The units players can build: the game's templates under
    /// LJ\lua\common\units\unitsTemplates, with gameplay mods' templates of
    /// the same layout on top. Read from the files, so it works in the lobby,
    /// before any match Lua exists. Units no builder can make (commanders,
    /// dev and test units, templates without a faction) are left out.
    public static class UnitCatalog
    {
        private static readonly Regex TemplatePath = new Regex(
            @"^common[\\/]units[\\/]unitsTemplates[\\/]([^\\/]+)[\\/]([^\\/]+)\.santp$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> StockNames = new Dictionary<string, string>
        {
            ["EDA"] = "EDA", ["CHOSEN"] = "Chosen", ["GUARD"] = "Guard",
        };

        private static Dictionary<string, Parsed> _game;
        private static string _cacheKey;
        private static IReadOnlyList<UnitEntry> _cache;

        /// The buildable units of a match with these gameplay mods, by
        /// domain (land, air, naval, structures), then tier, role and
        /// faction. Cached until the mods change.
        public static IReadOnlyList<UnitEntry> Buildable(IEnumerable<ModInfo> mods)
        {
            var list = (mods ?? Enumerable.Empty<ModInfo>()).Where(m => m != null).ToList();
            var key = string.Join("|", list.Select(m => m.Id + "@" + m.ContentHash));
            if (_cache != null && key == _cacheKey) return _cache;

            _game = _game ?? ReadGame();
            var templates = new Dictionary<string, (Parsed tp, ModInfo mod)>(StringComparer.Ordinal);
            foreach (var kv in _game) templates[kv.Key] = (kv.Value, null);
            foreach (var m in list)
                foreach (var rel in m.OverlayFiles)
                {
                    var match = TemplatePath.Match(rel);
                    if (!match.Success || !string.Equals(match.Groups[1].Value, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) continue;
                    var tp = Read(m.SourcePath(rel), match.Groups[1].Value.ToLowerInvariant());
                    if (tp != null) templates[tp.Id] = (tp, m); // later picks win, as in the overlay
                }

            var factions = new Dictionary<string, string>(StockNames, StringComparer.Ordinal);
            var order = new List<string>(StockOrder);
            foreach (var m in list)
                foreach (var f in m.Manifest.Factions)
                    if (!factions.ContainsKey(f.Tag)) { factions[f.Tag] = f.Name; order.Add(f.Tag); }

            // Upgrade targets are built by upgrading, not from a build list.
            var upgradeTargets = new HashSet<string>(templates.Values.Select(t => t.tp.UpgradesTo).Where(u => u.Length > 0), StringComparer.Ordinal);

            var units = new List<UnitEntry>();
            foreach (var (tp, mod) in templates.Values)
            {
                var faction = tp.Tags.FirstOrDefault(factions.ContainsKey);
                if (faction == null) continue;
                if (!tp.Tags.Any(t => t.StartsWith("BUILDABLE_BY_", StringComparison.Ordinal)) && !upgradeTargets.Contains(tp.Id)) continue;
                if (tp.Role.StartsWith("DEV", StringComparison.OrdinalIgnoreCase)) continue;
                var tags = new HashSet<string>(tp.Tags, StringComparer.Ordinal);
                var tech = Enumerable.Range(1, 4).FirstOrDefault(n => tags.Contains("TECH" + n));
                units.Add(new UnitEntry
                {
                    Id = tp.Id,
                    Name = tp.Name,
                    Role = tp.Role.Length > 0 ? tp.Role : tp.Id,
                    FactionTag = faction,
                    FactionName = factions[faction],
                    Tech = tech,
                    Domain = tags.Contains("STRUCTURE") ? "structure" : tags.Contains("AIR") ? "air" : tags.Contains("NAVAL") ? "naval" : "land",
                    Mobile = tags.Contains("MOBILE"),
                    Tags = tags,
                    Mod = mod,
                });
            }

            string[] domains = { "land", "air", "naval", "structure" };
            _cache = units
                .OrderBy(u => Array.IndexOf(domains, u.Domain))
                .ThenBy(u => u.Tech)
                .ThenBy(u => u.Role, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => order.IndexOf(u.FactionTag))
                .ThenBy(u => u.Id, StringComparer.Ordinal)
                .ToList();
            _cacheKey = key;
            return _cache;
        }

        private static readonly string[] StockOrder = { "EDA", "CHOSEN", "GUARD" };

        private sealed class Parsed
        {
            internal string Id;
            internal string Name = "";
            internal string Role = "";
            internal string UpgradesTo = "";
            internal List<string> Tags = new List<string>();
        }

        private static Dictionary<string, Parsed> ReadGame()
        {
            var result = new Dictionary<string, Parsed>(StringComparer.Ordinal);
            var root = Path.Combine(Paths.GameRootPath, "LJ", "lua", "common", "units", "unitsTemplates");
            if (!Directory.Exists(root))
            {
                ModApiPlugin.Log.LogWarning($"Unit catalog: no unit templates at {root}.");
                return result;
            }
            foreach (var dir in Directory.GetDirectories(root))
            {
                var id = Path.GetFileName(dir).ToLowerInvariant();
                var tp = Read(Path.Combine(dir, id + ".santp"), id);
                if (tp != null) result[id] = tp;
            }
            return result;
        }

        private static Parsed Read(string path, string id)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = LuaTable.ParseAssignment(File.ReadAllText(path, Encoding.UTF8), "UnitTemplate");
                if (root == null) return null;
                var tp = new Parsed { Id = id };
                if (root.TryGetValue("general", out var g) && g is Dictionary<object, object> general)
                {
                    tp.Name = Str(general, "name");
                    tp.Role = Str(general, "displayName");
                }
                if (root.TryGetValue("construction", out var c) && c is Dictionary<object, object> construction)
                    tp.UpgradesTo = Str(construction, "upgradesTo").ToLowerInvariant();
                if (root.TryGetValue("tags", out var t) && t is Dictionary<object, object> tags)
                    tp.Tags = tags.Values.OfType<string>().ToList();
                return tp;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Unit catalog: couldn't read {path}: {e.Message}");
                return null;
            }
        }

        private static string Str(Dictionary<object, object> t, string key) =>
            t.TryGetValue(key, out var v) && v is string s ? s : "";
    }

    /// Reads the Lua table literals templates are written in: tables,
    /// strings, numbers, booleans, nil and comments. Positional entries get
    /// keys 1, 2, 3 (as doubles). Anything else (calls, operators) throws.
    internal sealed class LuaTable
    {
        private readonly string _s;
        private int _i;

        private LuaTable(string s) { _s = s; }

        /// The table assigned to <paramref name="name"/> at the top of a file
        /// ("UnitTemplate = { ... }"), or null when there isn't one.
        internal static Dictionary<object, object> ParseAssignment(string text, string name)
        {
            var m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(name) + @"\s*=\s*\{");
            if (!m.Success) return null;
            var p = new LuaTable(text) { _i = m.Index + m.Length - 1 };
            return p.Value() as Dictionary<object, object>;
        }

        private object Value()
        {
            Skip();
            if (_i >= _s.Length) throw Fail("unexpected end");
            var c = _s[_i];
            if (c == '{') return Table();
            if (c == '"' || c == '\'') return String();
            if (c == '-' || c == '.' || char.IsDigit(c)) return Number();
            var word = Word();
            switch (word)
            {
                case "true": return true;
                case "false": return false;
                case "nil": return null;
                default: throw Fail($"unexpected '{word}'");
            }
        }

        private Dictionary<object, object> Table()
        {
            _i++; // {
            var t = new Dictionary<object, object>();
            var n = 0;
            while (true)
            {
                Skip();
                if (_i >= _s.Length) throw Fail("unclosed table");
                if (_s[_i] == '}') { _i++; return t; }
                object key = null;
                if (_s[_i] == '[')
                {
                    _i++;
                    key = Value();
                    Skip();
                    Expect(']');
                    Skip();
                    Expect('=');
                }
                else if (char.IsLetter(_s[_i]) || _s[_i] == '_')
                {
                    var start = _i;
                    var word = Word();
                    Skip();
                    if (_i < _s.Length && _s[_i] == '=' && (_i + 1 >= _s.Length || _s[_i + 1] != '='))
                    {
                        _i++;
                        key = word;
                    }
                    else _i = start; // a value such as true or false
                }
                var v = Value();
                if (key == null) key = (double)++n;
                if (v != null) t[key] = v;
                Skip();
                if (_i < _s.Length && (_s[_i] == ',' || _s[_i] == ';')) _i++;
            }
        }

        private string String()
        {
            var q = _s[_i++];
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Fail("unclosed string");
                var c = _s[_i++];
                if (c == q) return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= _s.Length) throw Fail("unclosed string");
                var e = _s[_i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    default: sb.Append(e); break; // \\ \" \' and anything else, as written
                }
            }
        }

        private object Number()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == '-' || _s[_i] == '+'))
            {
                // A '-' or '+' only after an exponent's 'e', or at the start.
                if ((_s[_i] == '-' || _s[_i] == '+') && _i > start && char.ToLowerInvariant(_s[_i - 1]) != 'e') break;
                _i++;
            }
            var text = _s.Substring(start, _i - start);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return (double)h;
            throw Fail($"bad number '{text}'");
        }

        private string Word()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            if (_i == start) throw Fail($"unexpected '{_s[_i]}'");
            return _s.Substring(start, _i - start);
        }

        /// Whitespace and comments: "-- to the end of the line" and "--[[ ]]".
        private void Skip()
        {
            while (_i < _s.Length)
            {
                if (char.IsWhiteSpace(_s[_i])) { _i++; continue; }
                if (_s[_i] == '-' && _i + 1 < _s.Length && _s[_i + 1] == '-')
                {
                    _i += 2;
                    var open = Regex.Match(_s.Substring(_i, Math.Min(64, _s.Length - _i)), @"^\[(=*)\[");
                    if (open.Success)
                    {
                        var close = "]" + open.Groups[1].Value + "]";
                        var end = _s.IndexOf(close, _i + open.Length, StringComparison.Ordinal);
                        _i = end < 0 ? _s.Length : end + close.Length;
                    }
                    else
                    {
                        var end = _s.IndexOf('\n', _i);
                        _i = end < 0 ? _s.Length : end + 1;
                    }
                    continue;
                }
                break;
            }
        }

        private void Expect(char c)
        {
            if (_i >= _s.Length || _s[_i] != c) throw Fail($"expected '{c}'");
            _i++;
        }

        private Exception Fail(string what)
        {
            var line = 1;
            for (var k = 0; k < Math.Min(_i, _s.Length); k++) if (_s[k] == '\n') line++;
            return new FormatException($"{what} at line {line}");
        }
    }
}
