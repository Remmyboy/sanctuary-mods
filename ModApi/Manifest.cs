using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sanctuary.ModApi
{
    /// What a mod is for.
    public enum ModKind
    {
        /// Changes only your own screen: a DLL, started and stopped by you
        /// from the Mods page at any time, never part of a lobby's check.
        Ui,

        /// Changes the match itself (Lua, .santp, maybe a DLL): picked by the
        /// lobby host, and every player must hold an identical copy.
        Gameplay,
    }

    /// A mod folder's mod.json. Every field but <see cref="Id"/> is optional.
    ///
    /// <code>
    /// {
    ///   "id": "alice.fastertanks",
    ///   "name": "Faster Tanks",
    ///   "version": "1.0.0",
    ///   "author": "Alice",
    ///   "description": "Tanks move 20% faster.",
    ///   "kind": "gameplay",
    ///   "luaRoot": "lua",
    ///   "url": "https://example.com/fastertanks",
    ///   "requires": ["bob.tankcore"],
    ///   "apiVersion": 1,
    ///   "options": [
    ///     { "key": "speedBonus", "label": "Speed bonus (%)", "type": "number", "default": 20, "min": 0, "max": 50, "step": 5 }
    ///   ]
    /// }
    /// </code>
    public sealed class ModManifest
    {
        public const string FileName = "mod.json";

        /// Lower-case letters, digits, '.', '_' and '-': how the lobby names
        /// the mod to other players. Prefix it with your own name so it
        /// never collides with someone else's.
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        public string Author { get; internal set; }
        public string Description { get; internal set; }
        public ModKind Kind { get; internal set; }
        /// The folder, relative to the mod's own, that mirrors LJ\lua. Only
        /// .lua and .santp files under it are overlaid.
        public string LuaRoot { get; internal set; }
        /// Where to get the mod. Shown to players who are missing it; never
        /// opened or fetched by the game.
        public string Url { get; internal set; }
        /// Ids of gameplay mods this one needs picked alongside it.
        public IReadOnlyList<string> Requires { get; internal set; } = Array.Empty<string>();
        /// The ModApi major version the mod was written for.
        public int ApiVersion { get; internal set; } = 1;
        /// Settings the lobby host picks for a gameplay mod. See
        /// <see cref="ModOption"/>.
        public IReadOnlyList<ModOption> Options { get; internal set; } = Array.Empty<ModOption>();
        /// The game version the mod was made and tested for ("0.0.1.20"), or
        /// a prefix of it ("0.0.1" for any 0.0.1.x). Empty when not stated.
        /// Only informs: a mod for another version still loads.
        public string GameVersion { get; internal set; } = "";
        /// A Lua file of the mod's, as a path under LJ\lua ("alice/norush/host.lua"),
        /// that the framework imports in the host's simulation when the mod
        /// is picked. Empty for none.
        public string HostScript { get; internal set; } = "";
        /// The same, imported in every player's client (and in replays).
        public string ClientScript { get; internal set; } = "";

        /// True when <see cref="GameVersion"/> is stated and the running
        /// game isn't that version.
        public bool IsForOtherGameVersion(string running) =>
            GameVersion.Length > 0 && running != null &&
            !(running == GameVersion || running.StartsWith(GameVersion + ".", StringComparison.Ordinal));
        /// True when the folder has no mod.json and these values were made up
        /// from its contents, the way mods were laid out before manifests.
        public bool Synthesised { get; internal set; }

        private static readonly Regex IdPattern = new Regex("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);

        public static bool IsValidId(string id) => id != null && IdPattern.IsMatch(id);

        /// Parses mod.json text. Problems that don't stop the mod loading
        /// (an unknown kind, a bad requires entry) land in problems.
        internal static ModManifest Parse(string json, string folderName, List<string> problems)
        {
            var o = JObject.Parse(json);
            var m = new ModManifest
            {
                Id = Str(o, "id"),
                Name = Str(o, "name"),
                Version = Str(o, "version") ?? "0.0.0",
                Author = Str(o, "author") ?? "",
                Description = Str(o, "description") ?? "",
                LuaRoot = Str(o, "luaRoot") ?? "lua",
                Url = Str(o, "url") ?? "",
            };
            m.ApiVersion = ApiMajor(o["apiVersion"], problems);

            if (!IsValidId(m.Id))
            {
                problems.Add($"mod.json: id '{m.Id}' must be 1-64 of a-z 0-9 . _ - ; using the folder name");
                var fromFolder = folderName.ToLowerInvariant();
                m.Id = IsValidId(fromFolder) ? fromFolder : Regex.Replace(fromFolder, "[^a-z0-9._-]", "_");
                if (m.Id.Length > 64) m.Id = m.Id.Substring(0, 64);
            }
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = folderName;

            var kind = (Str(o, "kind") ?? "").Trim().ToLowerInvariant();
            if (kind == "gameplay") m.Kind = ModKind.Gameplay;
            else if (kind == "ui") m.Kind = ModKind.Ui;
            else
            {
                if (kind.Length > 0) problems.Add($"mod.json: kind '{kind}' is neither \"ui\" nor \"gameplay\"");
                m.Kind = ModKind.Ui; // corrected from the folder's contents by the catalog
                m.KindUnset = true;
            }

            // luaRoot stays inside the mod folder.
            var root = m.LuaRoot.Replace('\\', '/').Trim('/');
            if (root.Contains("..") || Path.IsPathRooted(root))
            {
                problems.Add($"mod.json: luaRoot '{m.LuaRoot}' must be a folder inside the mod; using \"lua\"");
                root = "lua";
            }
            m.LuaRoot = root.Length == 0 ? "." : root;

            var requires = new List<string>();
            if (o["requires"] is JArray arr)
            {
                foreach (var t in arr)
                {
                    var id = t.Type == JTokenType.String ? (string)t : null;
                    if (IsValidId(id)) requires.Add(id);
                    else problems.Add($"mod.json: requires entry '{t}' is not a mod id");
                }
            }
            m.Requires = requires;
            m.Options = ModOption.ParseAll(o["options"], problems);
            m.GameVersion = (Str(o, "gameVersion") ?? "").Trim();
            if (m.GameVersion.Length > 40) m.GameVersion = m.GameVersion.Substring(0, 40);
            m.HostScript = ScriptPath(Str(o, "hostScript"), "hostScript", problems);
            m.ClientScript = ScriptPath(Str(o, "clientScript"), "clientScript", problems);
            return m;
        }

        /// A script path as Import takes it: forward slashes, relative,
        /// inside LJ\lua, ending in .lua.
        private static string ScriptPath(string raw, string field, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var p = raw.Trim().Replace('\\', '/').TrimStart('/');
            if (p.StartsWith("lua/", StringComparison.OrdinalIgnoreCase) && p.Length > 4)
            {
                // "lua/alice/x.lua" is the path in the mod folder; Import
                // wants the path under the mod's luaRoot.
                problems.Add($"mod.json: {field} '{raw}' starts with the lua folder; paths are relative to it, so using '{p.Substring(4)}'");
                p = p.Substring(4);
            }
            if (p.Contains("..") || Path.IsPathRooted(p) || p.Contains(":") || !p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                p.IndexOfAny(new[] { '"', '\n', '\r' }) >= 0)
            {
                problems.Add($"mod.json: {field} '{raw}' must be a .lua file of the mod's, relative to its lua folder; ignored");
                return "";
            }
            return p;
        }

        /// The API major version from "apiVersion": 1, 1.2, "1" or "1.2.0"
        /// all mean 1. Anything else is noted and taken as 1, rather than
        /// throwing and losing the whole mod.json over one field.
        private static int ApiMajor(JToken t, List<string> problems)
        {
            if (t == null || t.Type == JTokenType.Null) return 1;
            var text = (t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None)).Trim().TrimStart('v', 'V');
            var major = text.Split('.')[0];
            if (int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 1)
                return v;
            problems.Add($"mod.json: apiVersion '{t}' isn't a version number; taken as 1");
            return 1;
        }

        /// kind was missing or unknown: the catalog decides from the files.
        internal bool KindUnset;

        private static string Str(JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            var s = t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
            // Everything here ends up on screen or on the wire.
            return s.Length > 2000 ? s.Substring(0, 2000) : s;
        }
    }
}
