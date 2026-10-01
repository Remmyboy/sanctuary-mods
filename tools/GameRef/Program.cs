// Game API snapshots and the post-patch reference check. Run through
// tools/game-ref.ps1, which owns the snapshot folders:
//
//   GameRef api <ManagedDir> <outDir> [<BepInExCoreDir>]
//       api.txt         every type/member of the game's own assemblies, one
//                       self-describing line each, sorted (diffs cleanly)
//       api-engine.txt  the same for Unity/third-party assemblies (lookups only)
//
//   GameRef check <repoRoot> <snapshotDir> [<previousSnapshotDir>]
//       Scans the mods' C# and Lua (including Lua inside C# strings) for
//       names the game must provide - Engine.X functions, Import()ed files and
//       their members, reflection by string, enum values baked into built
//       DLLs - and reports the ones the snapshot lacks, marking what the
//       previous snapshot still had (a rename or removal in the patch).
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

static class GameRef
{
    // The game's own code; everything else in Managed goes to api-engine.txt.
    static readonly string[] GameAssemblies = { "Trebuchet", "Michsky.UI.Beam", "TweenLibrary", "Crosstales", "Tayx.Graphy", "ALINE" };

    static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "api": Api(args[1], args[2], args.Length > 3 ? args[3] : null); return 0;
                case "check": return Check(args[1], args[2], args.Length > 3 ? args[3] : null);
                case "lua": return LuaCheck.Run(args[1], args.Skip(2));
                default:
                    Console.Error.WriteLine("usage: GameRef api <ManagedDir> <outDir> | check <repoRoot> <snapshot> [<previous>]");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    // ------------------------------------------------------------------ api

    static void Api(string managed, string outDir, string extraDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var game = new List<string>();
        var engine = new List<string>();
        // extraDir (BepInEx\core) adds BepInEx/Harmony types for the check's lookups.
        var dlls = Directory.GetFiles(managed, "*.dll").Concat(extraDir != null ? Directory.GetFiles(extraDir, "*.dll") : Array.Empty<string>());
        foreach (var file in dlls.OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (Regex.IsMatch(name, @"^(System|mscorlib|netstandard|Mono\.|Microsoft\.)")) continue;
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver }); }
            catch { continue; }   // native or unreadable
            var target = GameAssemblies.Contains(name) ? game : engine;
            foreach (var t in asm.MainModule.GetTypes()) Describe(name, t, target);
        }
        game.Sort(StringComparer.Ordinal);
        engine.Sort(StringComparer.Ordinal);
        File.WriteAllLines(Path.Combine(outDir, "api.txt"), game);
        File.WriteAllLines(Path.Combine(outDir, "api-engine.txt"), engine);
        Console.WriteLine($"api.txt {game.Count} lines, api-engine.txt {engine.Count} lines");
    }

    static string TypeName(TypeReference t) => t.FullName.Replace('/', '.');

    static void Describe(string asm, TypeDefinition t, List<string> o)
    {
        if (t.Name.Contains('<')) return;   // compiler-generated
        var tn = TypeName(t);
        var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
        o.Add($"T {tn} {kind}{(t.BaseType != null && !t.IsEnum && !t.IsValueType ? " : " + TypeName(t.BaseType) : "")} [{asm}]");
        if (t.IsEnum)
        {
            foreach (var f in t.Fields.Where(f => f.IsStatic && f.HasConstant))
                o.Add($"E {tn}.{f.Name} = {f.Constant}");
            return;
        }
        foreach (var f in t.Fields.Where(f => !f.Name.Contains('<')))
            o.Add($"F {tn}.{f.Name} : {TypeName(f.FieldType)} {Vis(f.IsPublic, f.IsPrivate, f.IsFamily)}{(f.IsStatic ? " static" : "")}{(f.HasConstant ? " = " + f.Constant : "")}");
        foreach (var p in t.Properties)
        {
            var m = p.GetMethod ?? p.SetMethod;
            o.Add($"P {tn}.{p.Name} : {TypeName(p.PropertyType)} {Vis(m.IsPublic, m.IsPrivate, m.IsFamily)}{(m.IsStatic ? " static" : "")}{(p.GetMethod != null ? " get" : "")}{(p.SetMethod != null ? " set" : "")}");
        }
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.Name.Contains('<')))
            o.Add($"M {tn}.{m.Name}({string.Join(", ", m.Parameters.Select(p => TypeName(p.ParameterType)))}) : {TypeName(m.ReturnType)} {Vis(m.IsPublic, m.IsPrivate, m.IsFamily)}{(m.IsStatic ? " static" : "")}");
        foreach (var e in t.Events)
            o.Add($"V {tn}.{e.Name} : {TypeName(e.EventType)}");
    }

    static string Vis(bool pub, bool priv, bool fam) => pub ? "public" : priv ? "private" : fam ? "protected" : "internal";

    // ---------------------------------------------------------------- check

    sealed class Snapshot
    {
        public readonly HashSet<string> Types = new();              // full and simple names
        public readonly Dictionary<string, HashSet<string>> Members = new();  // simple and full type name -> members
        public readonly HashSet<string> AllMembers = new();
        public readonly Dictionary<string, string> Enums = new();   // Type.Member (full and simple type) -> value
        public readonly HashSet<string> EngineFunctions = new();       // client VM
        public readonly HashSet<string> HostEngineFunctions = new();
        public readonly string Lua;
        public readonly string Name;
        public readonly HashSet<string> TopDirs = new(StringComparer.OrdinalIgnoreCase);

        public Snapshot(string dir)
        {
            Name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            Lua = Path.Combine(dir, "lua");
            foreach (var file in new[] { "api.txt", "api-engine.txt" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path)) Add(line);
            }
            if (Directory.Exists(Lua))
                foreach (var d in Directory.GetDirectories(Lua)) TopDirs.Add(Path.GetFileName(d));
            foreach (var (side, set) in new[] { ("client", EngineFunctions), ("host", HostEngineFunctions) })
            {
                var ef = Path.Combine(Lua, side, "generated", "doc", "engineFunctions.lua");
                if (File.Exists(ef))
                    foreach (Match m in Regex.Matches(File.ReadAllText(ef), @"^Engine\.(\w+)\s*=", RegexOptions.Multiline))
                        set.Add(m.Groups[1].Value);
            }
        }

        void Add(string line)
        {
            if (line.Length < 3) return;
            if (line[0] == 'T')
            {
                var full = line.Substring(2, line.IndexOf(' ', 2) - 2);
                Types.Add(full);
                Types.Add(Simple(full));
                return;
            }
            var rest = line.Substring(2);
            var end = rest.IndexOfAny(new[] { '(', ' ' });
            var qualified = end < 0 ? rest : rest.Substring(0, end);
            var dot = qualified.LastIndexOf('.');
            if (dot < 0) return;
            var type = qualified.Substring(0, dot);
            var member = qualified.Substring(dot + 1);
            AllMembers.Add(member);
            foreach (var key in new[] { type, Simple(type) })
            {
                if (!Members.TryGetValue(key, out var set)) Members[key] = set = new HashSet<string>();
                set.Add(member);
            }
            if (line[0] == 'E')
            {
                var value = line.Substring(line.LastIndexOf('=') + 1).Trim();
                Enums[type + "." + member] = value;
                Enums[Simple(type) + "." + member] = value;
            }
        }

        public bool HasLuaFile(string rel) => File.Exists(Path.Combine(Lua, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    static string Simple(string full) { var i = full.LastIndexOf('.'); return i < 0 ? full : full.Substring(i + 1); }

    sealed record Finding(string Kind, string Name, string Where, string Note);

    static int Check(string repo, string snapDir, string prevDir)
    {
        var snap = new Snapshot(snapDir);
        var prev = prevDir != null ? new Snapshot(prevDir) : null;
        if (snap.Types.Count == 0) throw new Exception("snapshot has no api.txt: " + snapDir);

        var files = Directory.EnumerateFiles(repo, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".lua"))
                        && !Regex.IsMatch("/" + Path.GetRelativePath(repo, f), @"[\\/](bin|obj|\.claude|\.git|release|GameRef|archive)[\\/]"))
            .ToList();
        var texts = files.ToDictionary(f => f, File.ReadAllText);
        var allSource = string.Join("\n", texts.Values);
        // Lua files shipped by the mods themselves: ModApi/lua/modapi/x.lua serves Import('modapi/x.lua').
        var modLua = files.Where(f => f.EndsWith(".lua")).Select(f => f.Replace('\\', '/')).ToList();
        bool ModProvides(string rel) => modLua.Any(f => f.EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase));

        var findings = new List<Finding>();
        var seen = new HashSet<string>();
        void Report(string kind, string name, string file, int index, string note)
        {
            var line = texts[file].Take(index).Count(c => c == '\n') + 1;
            var where = Path.GetRelativePath(repo, file).Replace('\\', '/') + ":" + line;
            if (seen.Add(kind + name + where)) findings.Add(new Finding(kind, name, where, note));
        }
        string Was(Func<Snapshot, bool> had) => prev == null ? "" : had(prev) ? $"  << was in {prev.Name}: renamed or removed by the patch" : "";

        foreach (var (file, text) in texts)
        {
            // Engine.X, in Lua files and in Lua held in C# strings. C# code
            // like EM.DOTS.Engine.Loader is a namespace/type, not a function.
            foreach (Match m in Regex.Matches(text, @"(?<![\w.])Engine\.([A-Z]\w*)"))
            {
                var fn = m.Groups[1].Value;
                var cs = file.EndsWith(".cs");
                if (cs && !InString(text, m.Index)) continue;   // C# code, not Lua
                if (snap.EngineFunctions.Contains(fn)) continue;
                if (snap.HostEngineFunctions.Contains(fn))
                {
                    // Lua in C# strings runs in the client VM (ModLua); a mod's .lua files may be host-side.
                    if (cs) Report("lua-engine", "Engine." + fn, file, m.Index, "host-only: the client VM has no such function" + Was(p => p.EngineFunctions.Contains(fn)));
                    continue;
                }
                Report("lua-engine", "Engine." + fn, file, m.Index, "in neither client nor host engineFunctions.lua" +
                       Was(p => p.EngineFunctions.Contains(fn) || p.HostEngineFunctions.Contains(fn)));
            }

            // Import('path.lua'), plus members read straight off it or off a local alias.
            var aliases = new List<(string Alias, string Rel, int Start)>();
            foreach (Match m in Regex.Matches(text, @"(?:local\s+(\w+)\s*=\s*)?Import\(\s*['""]([^'""]+\.lua)['""]\s*\)(?:\s*[.:]\s*(\w+))?"))
            {
                var rel = m.Groups[2].Value;
                if (rel.Contains('<') || rel.Contains("&lt;")) continue;   // a template in a doc comment
                // Only paths into the game's own tree are checked: modapi/, modoptions/
                // and each mod's own folder are served by the Mod API at runtime.
                if (!snap.TopDirs.Contains(rel.Split('/')[0])) continue;
                var inGame = snap.HasLuaFile(rel);
                if (!inGame && !ModProvides(rel))
                {
                    Report("lua-import", rel, file, m.Index, "no such game Lua file" + Was(p => p.HasLuaFile(rel)));
                    continue;
                }
                if (!inGame) continue;
                if (m.Groups[1].Success && !m.Groups[3].Success) aliases.Add((m.Groups[1].Value, rel, m.Index + m.Length));
                if (m.Groups[3].Success) CheckLuaMember(rel, m.Groups[3].Value, m.Index);
            }
            // An alias holds until it is redefined; in C# only inside string
            // literals and near its definition, since `m.Index` is C# too.
            foreach (var (alias, rel, start) in aliases)
            {
                var next = aliases.Where(x => x.Alias == alias && x.Start > start).Select(x => x.Start).DefaultIfEmpty(text.Length).Min();
                var end = file.EndsWith(".cs") ? Math.Min(next, start + 4000) : next;
                foreach (Match m in Regex.Matches(text.Substring(0, end), @"(?<![\w./])" + Regex.Escape(alias) + @"\s*[.:]\s*(\w+)"))
                    if (m.Index >= start && (file.EndsWith(".lua") || InString(text, m.Index)))
                        CheckLuaMember(rel, m.Groups[1].Value, m.Index);
            }

            void CheckLuaMember(string rel, string member, int index)
            {
                var src = File.ReadAllText(Path.Combine(snap.Lua, rel));
                if (Regex.IsMatch(src, @"(?<![\w])" + Regex.Escape(member) + @"(?!\w)")) return;
                Report("lua-member", rel + " ." + member, file, index, "name not found in that file" +
                       Was(p => p.HasLuaFile(rel) && File.ReadAllText(Path.Combine(p.Lua, rel)).Contains(member)));
            }

            if (!file.EndsWith(".cs")) continue;

            // Reflection by string against a known type: AccessTools.X(typeof(T), "name"),
            // FieldRefAccess<T, U>("name"), [HarmonyPatch(typeof(T), "name")].
            foreach (Match m in Regex.Matches(text,
                @"(?:AccessTools\.\w+|HarmonyPatch)\s*(?:<\s*([\w.]+)[^>]*>)?\s*\(\s*(?:typeof\(([\w.]+)\)\s*,\s*)?""(\w+)""(?:\s*,\s*""(\w+)"")?"))
            {
                var type = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Success ? m.Groups[1].Value : null;
                var name = m.Groups[3].Value;
                if (m.Value.StartsWith("AccessTools.TypeByName")) { CheckType(name, m.Index); continue; }
                CheckMember(type, name, m.Index);
            }
            // Receiver unknown: .GetField("x"), .GetMethod("x"), .GetProperty("x").
            foreach (Match m in Regex.Matches(text, @"\.Get(?:Field|Method|Property|Event|NestedType)\(\s*""(\w+)"""))
                CheckMember(null, m.Groups[1].Value, m.Index);
            // Type.GetType("Full.Name, Assembly") and asm.GetType("Full.Name").
            foreach (Match m in Regex.Matches(text, @"\bGetType\(\s*""([\w.+]+)"))
                CheckType(m.Groups[1].Value, m.Index);

            void CheckMember(string type, string name, int index)
            {
                if (type != null && snap.Members.TryGetValue(Simple(type), out var members))
                {
                    if (members.Contains(name)) return;
                    Report("reflection", Simple(type) + "." + name, file, index, "no such member on the game type" +
                           Was(p => p.Members.TryGetValue(Simple(type), out var pm) && pm.Contains(name)));
                    return;
                }
                if (snap.AllMembers.Contains(name)) return;
                // The mods reflect on their own types too (stale hot-reload copies).
                if (Regex.IsMatch(allSource, @"(?<![""\w])" + Regex.Escape(name) + @"(?![""\w])")) return;
                Report("reflection", (type != null ? Simple(type) + "." : "") + name, file, index,
                       "no member of that name in the game or the mods" + Was(p => p.AllMembers.Contains(name)));
            }

            void CheckType(string name, int index)
            {
                if (snap.Types.Contains(name) || snap.Types.Contains(Simple(name.Replace('+', '.')))) return;
                if (Regex.IsMatch(name, @"^(System|UnityEngine|Unity|Mono|BepInEx|HarmonyLib)\b")) return;
                if (Regex.IsMatch(allSource, @"\b(class|struct|enum|interface)\s+" + Regex.Escape(Simple(name)) + @"\b")) return;
                Report("reflection-type", name, file, index, "type not in the game" + Was(p => p.Types.Contains(name) || p.Types.Contains(Simple(name))));
            }
        }

        // Enum members whose value changed: every DLL built against the old
        // value still sends it (the 0.0.1.20 UIPanelType shift).
        if (prev != null)
        {
            foreach (var (key, oldValue) in prev.Enums.Where(kv => kv.Key.Count(c => c == '.') == 1))
            {
                var now = snap.Enums.TryGetValue(key, out var v) ? v : null;
                if (now == oldValue) continue;
                foreach (var (file, text) in texts.Where(kv => kv.Key.EndsWith(".cs")))
                    foreach (Match m in Regex.Matches(text, @"(?<![\w])" + Regex.Escape(key) + @"(?!\w)"))
                        Report("enum-value", key, file, m.Index,
                               now == null ? $"removed (was {oldValue})" : $"{oldValue} -> {now}: released DLLs carry the old value; rebuild and re-release");
            }
        }

        Console.WriteLine($"Checked {files.Count} source files against {snap.Name}" + (prev != null ? $" (previous: {prev.Name})" : "") +
                          $": {snap.EngineFunctions.Count} engine functions, {snap.Types.Count / 2} types.");
        if (findings.Count == 0) { Console.WriteLine("No missing references."); return 0; }
        foreach (var g in findings.OrderByDescending(f => f.Note.Contains("<<")).ThenBy(f => f.Kind).GroupBy(f => f.Kind + " " + f.Name))
        {
            var f = g.First();
            Console.WriteLine($"{f.Kind,-16} {f.Name}  {f.Note}");
            foreach (var x in g.Take(6)) Console.WriteLine($"{"",-16}   {x.Where}");
            if (g.Count() > 6) Console.WriteLine($"{"",-16}   … {g.Count() - 6} more");
        }
        Console.WriteLine($"\n{findings.Select(f => f.Kind + f.Name).Distinct().Count()} name(s) to look at. Lines marked << existed in the previous build.");
        return 1;
    }

    // Crude but enough: is the index inside a "..." or @"..." literal on its line?
    static bool InString(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var quotes = text.Substring(start, index - start).Count(c => c == '"');
        if (quotes % 2 == 1) return true;
        // Inside a multi-line verbatim string: an odd number of lone quotes since the last @".
        var at = text.LastIndexOf("@\"", index, StringComparison.Ordinal);
        if (at < 0) return false;
        var between = text.Substring(at + 2, index - at - 2).Replace("\"\"", "");
        return !between.Contains('"');
    }
}
