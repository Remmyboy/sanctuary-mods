using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Sanctuary.ModApi
{
    public enum ModOptionType
    {
        /// On or off.
        Toggle,
        /// One of a fixed list of values.
        Choice,
        /// A number, optionally within a range and on a step.
        Number,
    }

    public sealed class ModOptionChoice
    {
        /// What the mod's code sees.
        public string Value { get; internal set; }
        /// What the lobby shows.
        public string Label { get; internal set; }
    }

    /// One entry of mod.json's "options": a setting the lobby host picks for
    /// the match, the same for every player.
    ///
    /// <code>
    /// "options": [
    ///   { "key": "minutes", "label": "No-rush time", "type": "number", "default": 20, "min": 0, "max": 60, "step": 5 },
    ///   { "key": "noAir", "label": "No air units", "type": "toggle", "default": false },
    ///   { "key": "color", "label": "Army colour", "type": "choice", "default": "pink",
    ///     "choices": [ { "value": "pink", "label": "Pink" }, "green" ] }
    /// ]
    /// </code>
    ///
    /// Values travel and are stored as canonical strings: "true"/"false" for
    /// a toggle, the choice's value, and a number written with a '.' and no
    /// exponent. Every machine turns the same input into the same string,
    /// which is what lets the lobby compare them.
    public sealed class ModOption
    {
        internal const int MaxOptions = 32;
        private const int MaxChoices = 50;

        public string Key { get; internal set; }
        public string Label { get; internal set; }
        public string Description { get; internal set; }
        public ModOptionType Type { get; internal set; }
        /// The canonical default value.
        public string Default { get; internal set; }
        /// For a choice.
        public IReadOnlyList<ModOptionChoice> Choices { get; internal set; } = Array.Empty<ModOptionChoice>();
        /// For a number; null when unbounded on that side.
        public double? Min { get; internal set; }
        public double? Max { get; internal set; }
        /// For a number: values snap to Min (or 0) plus a whole number of
        /// steps. Null for any value.
        public double? Step { get; internal set; }

        /// A number that only takes whole values.
        public bool IsWhole => Type == ModOptionType.Number && Step.HasValue && IsInt(Step.Value) && IsInt(Min ?? 0);

        private static readonly Regex KeyPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,39}$", RegexOptions.Compiled);

        private static readonly HashSet<string> LuaKeywords = new HashSet<string>
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
            "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        /// The value as this option stores it, or the default when the value
        /// isn't one it can take. Null or empty gives the default.
        public string Normalize(string raw)
        {
            if (raw == null) return Default;
            // A choice's value is taken as written first: it may have
            // spaces of its own.
            var exact = Type == ModOptionType.Choice ? Choices.FirstOrDefault(c => c.Value == raw) : null;
            if (exact != null) return exact.Value;
            raw = raw.Trim();
            switch (Type)
            {
                case ModOptionType.Toggle:
                    var b = ParseBool(raw);
                    return b.HasValue ? (b.Value ? "true" : "false") : Default;
                case ModOptionType.Choice:
                    var loose = Choices.FirstOrDefault(c => string.Equals(c.Value.Trim(), raw, StringComparison.OrdinalIgnoreCase));
                    return loose?.Value ?? Default;
                default:
                    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
                        ? FormatNumber(Snap(d))
                        : Default;
            }
        }

        /// How the lobby shows a value.
        public string Display(string value)
        {
            value = Normalize(value);
            switch (Type)
            {
                case ModOptionType.Toggle: return value == "true" ? "On" : "Off";
                case ModOptionType.Choice: return Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;
                default: return value;
            }
        }

        private double Snap(double v)
        {
            if (Min.HasValue && v < Min.Value) v = Min.Value;
            if (Max.HasValue && v > Max.Value) v = Max.Value;
            if (Step.HasValue && Step.Value > 0)
            {
                var from = Min ?? 0;
                v = from + Math.Round((v - from) / Step.Value, MidpointRounding.AwayFromZero) * Step.Value;
                // A step that doesn't divide the range can round the top
                // value past Max: take the last step inside it.
                if (Max.HasValue && v > Max.Value + 1e-9) v -= Step.Value;
                if (Min.HasValue && v < Min.Value - 1e-9) v = Min.Value;
            }
            return v;
        }

        /// A number as options store it: a '.', no exponent, no trailing zeros.
        public static string FormatNumber(double v)
        {
            // Twelve significant digits hides binary noise from stepping
            // (0.1 * 3) while keeping any number a person would type.
            var rounded = double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (rounded == 0) rounded = 0; // no "-0"
            return rounded.ToString("0.############", CultureInfo.InvariantCulture);
        }

        private static bool IsInt(double v) => Math.Abs(v - Math.Round(v)) < 1e-9;

        internal static bool? ParseBool(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": return true;
                case "false": case "0": case "off": case "no": return false;
                default: return null;
            }
        }

        /// What decides the Lua file this option writes, for the mod's
        /// content hash: two copies of a mod whose options take different
        /// values can't be told apart by their Lua files alone. Labels and
        /// descriptions stay out, so rewording them changes nothing.
        internal string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(Key).Append('|').Append(Type).Append('|');
            if (Type == ModOptionType.Choice) sb.Append(string.Join("\u001f", Choices.Select(c => c.Value)));
            if (Type == ModOptionType.Number)
                sb.Append(Min.HasValue ? FormatNumber(Min.Value) : "").Append('|')
                  .Append(Max.HasValue ? FormatNumber(Max.Value) : "").Append('|')
                  .Append(Step.HasValue ? FormatNumber(Step.Value) : "");
            return sb.ToString();
        }

        internal static List<ModOption> ParseAll(JToken token, List<string> problems)
        {
            var list = new List<ModOption>();
            if (token == null || token.Type == JTokenType.Null) return list;
            if (!(token is JArray arr))
            {
                problems.Add("mod.json: options must be a list");
                return list;
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in arr)
            {
                if (list.Count >= MaxOptions)
                {
                    problems.Add($"mod.json: only the first {MaxOptions} options are used");
                    break;
                }
                if (!(t is JObject o))
                {
                    problems.Add("mod.json: every option must be an object with a key and a type");
                    continue;
                }
                var opt = Parse(o, problems);
                if (opt == null) continue;
                if (!keys.Add(opt.Key))
                {
                    problems.Add($"mod.json: option '{opt.Key}' is listed twice; the second is ignored");
                    continue;
                }
                list.Add(opt);
            }
            return list;
        }

        private static ModOption Parse(JObject o, List<string> problems)
        {
            var key = Str(o, "key");
            if (key == null || !KeyPattern.IsMatch(key) || LuaKeywords.Contains(key))
            {
                problems.Add($"mod.json: option key '{key}' must be a Lua name (letters, digits, _; not starting with a digit; not a keyword); option ignored");
                return null;
            }
            var opt = new ModOption
            {
                Key = key,
                Label = Cap(Str(o, "label"), 80) ?? key,
                Description = Cap(Str(o, "description"), 600) ?? "",
            };

            var type = (Str(o, "type") ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "toggle": case "bool": case "boolean": opt.Type = ModOptionType.Toggle; break;
                case "choice": case "select": opt.Type = ModOptionType.Choice; break;
                case "number": case "int": case "integer": opt.Type = ModOptionType.Number; break;
                default:
                    problems.Add($"mod.json: option '{key}' has type '{type}'; use \"toggle\", \"choice\" or \"number\". Option ignored");
                    return null;
            }

            var def = o["default"];
            switch (opt.Type)
            {
                case ModOptionType.Toggle:
                    opt.Default = "false";
                    if (def != null && def.Type != JTokenType.Null)
                    {
                        var b = def.Type == JTokenType.Boolean ? (bool)def : ParseBool(def.ToString());
                        if (b.HasValue) opt.Default = b.Value ? "true" : "false";
                        else problems.Add($"mod.json: option '{key}': default '{def}' isn't true or false");
                    }
                    break;

                case ModOptionType.Choice:
                    var choices = new List<ModOptionChoice>();
                    if (o["choices"] is JArray ca)
                    {
                        foreach (var c in ca)
                        {
                            if (choices.Count >= MaxChoices) { problems.Add($"mod.json: option '{key}': only the first {MaxChoices} choices are used"); break; }
                            string value, label;
                            if (c is JObject co) { value = Str(co, "value"); label = Str(co, "label"); }
                            else if (c.Type == JTokenType.String || c.Type == JTokenType.Integer || c.Type == JTokenType.Float || c.Type == JTokenType.Boolean)
                            { value = c.Type == JTokenType.Boolean ? ((bool)c ? "true" : "false") : c.ToString(); label = null; }
                            else { value = null; label = null; }
                            value = Cap(value, 100);
                            if (string.IsNullOrEmpty(value)) { problems.Add($"mod.json: option '{key}': a choice has no value"); continue; }
                            if (choices.Any(x => x.Value == value)) { problems.Add($"mod.json: option '{key}': choice '{value}' is listed twice"); continue; }
                            choices.Add(new ModOptionChoice { Value = value, Label = Cap(label, 80) ?? value });
                        }
                    }
                    if (choices.Count == 0)
                    {
                        problems.Add($"mod.json: option '{key}' is a choice with no choices; option ignored");
                        return null;
                    }
                    opt.Choices = choices;
                    var dv = def == null || def.Type == JTokenType.Null ? null
                        : def.Type == JTokenType.Boolean ? ((bool)def ? "true" : "false") : def.ToString();
                    opt.Default = choices[0].Value;
                    if (dv != null)
                    {
                        var match = choices.FirstOrDefault(x => x.Value == dv);
                        if (match != null) opt.Default = match.Value;
                        else problems.Add($"mod.json: option '{key}': default '{dv}' isn't one of its choices; using '{choices[0].Value}'");
                    }
                    break;

                default:
                    opt.Min = Num(o, "min", key, problems);
                    opt.Max = Num(o, "max", key, problems);
                    opt.Step = Num(o, "step", key, problems);
                    if (opt.Step.HasValue && opt.Step.Value <= 0)
                    {
                        problems.Add($"mod.json: option '{key}': step must be above 0; ignored");
                        opt.Step = null;
                    }
                    if (type == "int" || type == "integer") opt.Step = opt.Step ?? 1;
                    if (opt.Min.HasValue && opt.Max.HasValue && opt.Min.Value > opt.Max.Value)
                    {
                        problems.Add($"mod.json: option '{key}': min is above max; swapped");
                        var m = opt.Min; opt.Min = opt.Max; opt.Max = m;
                    }
                    // No default given: the lowest value it can take (or 0).
                    opt.Default = "0";
                    opt.Default = opt.Normalize(opt.Min.HasValue ? FormatNumber(opt.Min.Value) : "0");
                    var dn = Num(o, "default", key, problems);
                    if (dn.HasValue)
                    {
                        var normal = opt.Normalize(FormatNumber(dn.Value));
                        if (normal != FormatNumber(dn.Value))
                            problems.Add($"mod.json: option '{key}': default {FormatNumber(dn.Value)} is outside its range or step; using {normal}");
                        opt.Default = normal;
                    }
                    break;
            }
            return opt;
        }

        private static double? Num(JObject o, string name, string key, List<string> problems)
        {
            var t = o[name];
            if (t == null || t.Type == JTokenType.Null) return null;
            if ((t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && !double.IsNaN((double)t) && !double.IsInfinity((double)t))
                return (double)t;
            if (t.Type == JTokenType.String && double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                !double.IsNaN(d) && !double.IsInfinity(d))
                return d;
            problems.Add($"mod.json: option '{key}': {name} '{t}' isn't a number; ignored");
            return null;
        }

        private static string Str(JObject o, string name)
        {
            var t = o[name];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Cap(string s, int n) => s == null ? null : s.Length > n ? s.Substring(0, n) : s;
    }

    /// The option values a gameplay mod runs with: the host's picks, or the
    /// defaults. Every getter falls back to the option's default, and to
    /// the type's zero value for a key the mod doesn't declare.
    public sealed class ModOptionValues
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public ModInfo Mod { get; }

        /// Key to canonical value, one per option the mod declares.
        public IReadOnlyDictionary<string, string> Values => _values;

        /// These are the values a live match (or replay) on this machine runs
        /// with, rather than the mod's defaults.
        public bool IsLive { get; }

        internal ModOptionValues(ModInfo mod, IReadOnlyDictionary<string, string> values, bool live)
        {
            Mod = mod;
            _values = values ?? new Dictionary<string, string>();
            IsLive = live;
        }

        public string GetString(string key) => _values.TryGetValue(key, out var v) ? v : "";

        public bool GetBool(string key) => ModOption.ParseBool(GetString(key)) ?? false;

        public double GetNumber(string key) =>
            double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        public int GetInt(string key) => (int)Math.Round(GetNumber(key));

        public override string ToString() => string.Join(", ", _values.Select(kv => kv.Key + "=" + kv.Value));
    }

    /// Option values: filling in, checking, and the Lua file they become.
    internal static class OptionValues
    {
        /// Where a mod's options file sits in LJ\lua, and so what its Lua
        /// imports: Import("modoptions/&lt;id&gt;.lua").Options.
        internal static string LuaPath(string modId) => "modoptions/" + modId + ".lua";

        /// Every option the mod declares, set to the given value when it's a
        /// valid one and to the default otherwise. Keys the mod doesn't
        /// declare are dropped.
        internal static Dictionary<string, string> Complete(ModInfo mod, IReadOnlyDictionary<string, string> given)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in mod.Manifest.Options)
            {
                string raw = null;
                given?.TryGetValue(o.Key, out raw);
                result[o.Key] = o.Normalize(raw);
            }
            return result;
        }

        internal static Dictionary<string, string> Defaults(ModInfo mod) => Complete(mod, null);

        /// A stable text form of a set of values, for "did anything change".
        internal static string Signature(IReadOnlyDictionary<string, string> values) =>
            values == null ? "" : string.Join("\u001e", values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "\u001f" + kv.Value));

        /// The Lua file: a table of the values, keys sorted, so the same
        /// values make the same bytes on every machine.
        internal static byte[] LuaFile(ModInfo mod, IReadOnlyDictionary<string, string> values)
        {
            var sb = new StringBuilder();
            sb.Append("-- Written by the Sanctuary Mod API: the options the lobby host picked for\n");
            sb.Append("-- ").Append(OneLine(mod.Name)).Append(" (").Append(mod.Id).Append("). Every player's copy is identical.\n");
            sb.Append("-- Read it with: local Options = Import(\"").Append(LuaPath(mod.Id)).Append("\").Options\n");
            sb.Append("Options = {\n");
            foreach (var o in mod.Manifest.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                values.TryGetValue(o.Key, out var v);
                v = o.Normalize(v);
                sb.Append("    ").Append(o.Key).Append(" = ");
                switch (o.Type)
                {
                    case ModOptionType.Toggle: sb.Append(v == "true" ? "true" : "false"); break;
                    case ModOptionType.Number: sb.Append(v); break;
                    default: sb.Append(LuaString(v)); break;
                }
                sb.Append(",\n");
            }
            sb.Append("}\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string OneLine(string s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ');

        internal static string LuaString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var b in Encoding.UTF8.GetBytes(s ?? ""))
            {
                if (b == '"' || b == '\\') sb.Append('\\').Append((char)b);
                else if (b < 32 || b == 127) sb.Append('\\').Append(((int)b).ToString("000", CultureInfo.InvariantCulture));
                else if (b < 128) sb.Append((char)b);
                else sb.Append('\\').Append(((int)b).ToString("000", CultureInfo.InvariantCulture));
            }
            return sb.Append('"').ToString();
        }
    }
}
