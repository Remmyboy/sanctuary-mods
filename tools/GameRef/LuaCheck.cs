// GameRef lua <lua51.dll> <file-or-dir>...
//
// Compiles Lua with the game's own LuaJIT (the same check ModApi's
// LuaSyntax runs in game) without running it: every .lua file, and every
// chunk of Lua held in a C# string - a verbatim @"..." literal, or a run of
// "..." + "..." literals - with interpolations and non-literal operands
// replaced by a placeholder name. Reports LuaJIT's message with the C# line.
//
// Then lints what compiled, from its bytecode (analyze.lua, via jit.util),
// printing `path:line: warning: ...`; warnings fail the run only with --strict:
//  - global assignments (a missing `local`). Allowed: names starting __ (our
//    cross-chunk globals), names listed in a `lua-check: globals A, B`
//    comment anywhere in the file (Lua or C#), and in game-file appends
//    (lua/append/**, modapi/hooks/**) any global function definition -
//    redefining the game's functions is what appends do; plain variables
//    are still flagged there. A .lua file's main-chunk globals are module
//    exports (Import gives each file its own environment) when any Lua
//    scanned reads the name as X.Name / X:Name; only unread ones are flagged.
//  - pcall(...) as a statement: the error is dropped unseen.
//  - in C#: a global set to literal true/false that the same file reads
//    back with GetLuaGlobal / ModLua.GetGlobal, which can't see booleans.
// `lua-check: ok` on the line (in the Lua, or the C# line holding it)
// silences any warning there.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static class LuaCheck
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NewState();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadBuffer(IntPtr L, byte[] buff, UIntPtr size, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ToLString(IntPtr L, int index, IntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetTop(IntPtr L, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void OpenLibs(IntPtr L);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PushLString(IntPtr L, byte[] s, UIntPtr len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GetField(IntPtr L, int index, string k);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PCall(IntPtr L, int nargs, int nresults, int errfunc);

    const int LuaGlobalsIndex = -10002;

    static LoadBuffer _load;
    static ToLString _tostring;
    static SetTop _settop;
    static PushLString _pushl;
    static GetField _getfield;
    static PCall _pcall;
    static IntPtr _state;
    static IntPtr _lint;   // a second state with the libraries open, for analyze.lua

    static readonly Regex AppendPath = new(@"[\\/]lua[\\/]append[\\/]|[\\/]modapi[\\/]hooks[\\/]", RegexOptions.IgnoreCase);
    static readonly Regex AllowGlobals = new(@"lua-check:\s*globals\s+([\w, \t]+)");
    static readonly Regex MemberRead = new(@"[.:]\s*([A-Za-z_]\w*)");
    static readonly Regex GlobalRead =new(@"\b(?:GetLuaGlobal|GetGlobal)\(\s*(?:""(\w+)""|(\w+))\s*\)");

    // Looks like Lua rather than prose (setting descriptions say "then" and
    // "end" too), a path or a UI string.
    static readonly Regex LuaMarkers = new(@"\blocal\s+[\w, ]+=|\bfunction\s*[\w.:]*\s*\(|\bImport\s*\(|\bpcall\s*\(|\b_G\.\w|\bif\b[^\n]*\bthen\b|^\s*\{\s*\w+\s*=");

    public static int Run(string dll, IEnumerable<string> args)
    {
        var strict = args.Any(a => a is "--strict" or "-strict");
        var targets = args.Where(a => a is not ("--strict" or "-strict")).ToList();
        var lib = NativeLibrary.Load(dll);
        T Fn<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        _state = Fn<NewState>("luaL_newstate")();
        _load = Fn<LoadBuffer>("luaL_loadbuffer");
        _tostring = Fn<ToLString>("lua_tolstring");
        _settop = Fn<SetTop>("lua_settop");
        var lintErr = StartLint(Fn<NewState>("luaL_newstate"), Fn<OpenLibs>("luaL_openlibs"),
            Fn<PushLString>("lua_pushlstring"), Fn<GetField>("lua_getfield"), Fn<PCall>("lua_pcall"));
        if (lintErr != null) Console.WriteLine($"lua-check: warnings unavailable ({lintErr}); syntax only.");

        var files = targets.SelectMany(t => Directory.Exists(t)
                ? Directory.EnumerateFiles(t, "*.*", SearchOption.AllDirectories)
                    .Where(f => !Regex.IsMatch("/" + Path.GetRelativePath(t, f), @"[\\/](bin|obj|\.git|\.claude|release)[\\/]"))
                : new[] { t })
            .Where(f => f.EndsWith(".lua") || f.EndsWith(".cs"))
            .ToList();

        int chunks = 0, errors = 0;
        var warnings = new List<Finding>();
        var memberReads = new HashSet<string>();   // every .Name / :Name in any Lua seen
        void Members(string lua) { foreach (Match m in MemberRead.Matches(lua)) memberReads.Add(m.Groups[1].Value); }
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".lua"))
            {
                chunks++;
                Members(text);
                var err = Compile(text, Path.GetFileName(file));
                if (err != null) { errors++; Console.WriteLine($"{file}: {err}"); }
                else if (_lint != IntPtr.Zero) Lint(file, text, null, new Variant(text), null, warnings);
                continue;
            }
            HashSet<string> csReads = null;
            foreach (var (code, alt, index, variants) in CsChunks(text))
            {
                if (Ignored(text, index)) continue;
                chunks++;
                Members(code);
                var err = Compile(code, "chunk");
                if (err == null || (alt != null && Compile(alt, "chunk") == null))
                {
                    if (_lint == IntPtr.Zero) continue;
                    // The first form that compiles as statements; the lined
                    // ones put each C# literal on its own Lua line, so a
                    // finding maps to the right C# line.
                    var v = variants.FirstOrDefault(v => Load(v.Code, "chunk") == null);
                    if (v == null) continue;   // an expression piece
                    csReads ??= ReadBack(text);
                    Lint(file, text, LineStarts(text), v, csReads, warnings);
                    continue;
                }
                errors++;
                var line = text.Take(index).Count(c => c == '\n') + 1;
                // LuaJIT says [string "chunk"]:N: - N is a line inside the chunk.
                Console.WriteLine($"{file}:{line}: {err}");
                var m = Regex.Match(err, @"\]:(\d+):");
                if (m.Success)
                {
                    var lines = code.Split('\n');
                    var n = int.Parse(m.Groups[1].Value);
                    if (n >= 1 && n <= lines.Length) Console.WriteLine($"    chunk line {n}: {lines[n - 1].Trim()}");
                }
            }
        }
        // A .lua file's main-chunk globals are its module's exports when some
        // Lua reads them as Import(...).Name or alias.Name: the game's Import
        // gives each file its own environment, so they never reach _G.
        warnings = warnings.Where(w => (w.Export == null || !memberReads.Contains(w.Export))
                && (w.BoolName == null || !_setOther.Contains((w.File, w.BoolName))))
            .OrderBy(w => w.File, StringComparer.Ordinal).ThenBy(w => w.Line).ToList();
        foreach (var w in warnings) Console.WriteLine($"{w.File}:{w.Line}: warning: {w.Msg}");
        Console.WriteLine($"{chunks} chunk(s)/file(s) compiled, {errors} error(s).");
        if (warnings.Count > 0)
            Console.WriteLine($"{warnings.Count} warning(s){(strict ? "; failing (--strict)" : " (they fail the run only with --strict)")}.");
        return errors == 0 && !(strict && warnings.Count > 0) ? 0 : 1;
    }

    // ------------------------------------------------------------- lint

    // Lua text to analyse, and where each part of it came from in the file:
    // a piece starting at chunk offset At came from source index Src; a
    // verbatim piece keeps the source's line breaks, a "..." piece is one line.
    record Piece(int At, int Src, bool Verbatim);

    class Variant
    {
        public readonly StringBuilder Sb = new();
        public readonly List<Piece> Map = new();
        string _code;
        public Variant() { }
        public Variant(string code) { _code = code; }
        public string Code => _code ??= Sb.ToString();
    }

    static string StartLint(NewState newState, OpenLibs openLibs, PushLString pushl, GetField getfield, PCall pcall)
    {
        _pushl = pushl; _getfield = getfield; _pcall = pcall;
        var path = Path.Combine(AppContext.BaseDirectory, "analyze.lua");
        if (!File.Exists(path)) return "analyze.lua missing beside GameRef.dll";
        var L = newState();
        openLibs(L);
        var bytes = File.ReadAllBytes(path);
        if (_load(L, bytes, (UIntPtr)bytes.Length, "analyze.lua") != 0 || _pcall(L, 0, 0, 0) != 0)
        {
            var msg = Marshal.PtrToStringUTF8(_tostring(L, -1, IntPtr.Zero));
            _settop(L, 0);
            return msg;
        }
        _lint = L;
        return null;
    }

    static string Analyze(string code)
    {
        _getfield(_lint, LuaGlobalsIndex, "__lc_analyze");
        var bytes = Encoding.UTF8.GetBytes(code);
        _pushl(_lint, bytes, (UIntPtr)bytes.Length);
        _pushl(_lint, new[] { (byte)'c' }, (UIntPtr)1);
        var r = _pcall(_lint, 2, 1, 0);
        var s = Marshal.PtrToStringUTF8(_tostring(_lint, -1, IntPtr.Zero)) ?? "";
        _settop(_lint, 0);
        return r == 0 ? s : "E\t" + s;
    }

    // Globals the C# reads back: GetLuaGlobal("x"), ModLua.GetGlobal("x"), or
    // GetLuaGlobal(Name) with Name = "x" declared in the same file.
    static HashSet<string> ReadBack(string text)
    {
        var set = new HashSet<string>();
        foreach (Match m in GlobalRead.Matches(text))
        {
            if (m.Groups[1].Success) { set.Add(m.Groups[1].Value); continue; }
            var decl = Regex.Match(text, @"\b" + Regex.Escape(m.Groups[2].Value) + @"\s*=\s*""(\w+)""");
            if (decl.Success) set.Add(decl.Groups[1].Value);
        }
        return set;
    }

    // Export: set on a .lua file's global that its main chunk assigns; it is
    // dropped at the end if any Lua reads that name as a member.
    // BoolName: a true/false read back from C#; dropped at the end if the
    // same file also stores something else in it (converted before the read).
    record Finding(string File, int Line, string Msg, string Export, string BoolName);

    static readonly HashSet<(string File, string Name)> _setOther = new();

    // csReads == null: a .lua file, chunk lines are file lines.
    static void Lint(string file, string text, int[] lineStarts, Variant v, HashSet<string> csReads, List<Finding> warnings)
    {
        var result = Analyze(v.Code);
        if (result.StartsWith("E\t")) { warnings.Add(new Finding(file, 1, $"lint failed: {result[2..]}", null, null)); return; }
        var allowed = new HashSet<string>(AllowGlobals.Matches(text).SelectMany(m => m.Groups[1].Value.Split(',', ' ', '\t'))
            .Where(s => s.Length > 0));
        var append = AppendPath.IsMatch(file);
        var codeLines = v.Code.Split('\n');
        var rows = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('\t')).ToList();
        var topLevel = csReads != null ? new HashSet<string>()
            : rows.Where(f => f[0] == "G" && f[4] == "1").Select(f => f[2]).ToHashSet();
        foreach (var f in rows)
        {
            var chunkLine = int.Parse(f[1]);
            int line;
            string sourceLine;
            if (csReads == null) { line = chunkLine; sourceLine = Get(codeLines, chunkLine); }
            else
            {
                line = SourceLine(v, chunkLine, lineStarts);
                var s = lineStarts[line - 1];
                var e = text.IndexOf('\n', s);
                sourceLine = text.Substring(s, (e < 0 ? text.Length : e) - s);
            }
            if (sourceLine.Contains("lua-check: ok") || Get(codeLines, chunkLine).Contains("lua-check: ok")) continue;

            string msg = null, export = null, boolName = null;
            if (f[0] == "P")
                msg = "pcall result discarded: a failure vanishes unseen (keep ok, err, or mark the line -- lua-check: ok)";
            else if (f[0] == "G")
            {
                var name = f[2];
                var kind = f[3];
                if (csReads != null && kind != "bool") _setOther.Add((file, name));
                if (csReads != null && kind == "bool" && csReads.Contains(name))
                {
                    msg = $"global '{name}' set to true/false but read back with GetLuaGlobal, which can't see booleans (always null): use 1 or a string";
                    boolName = name;
                }
                else if (name.StartsWith("__") || allowed.Contains(name) || (append && kind == "func"))
                    continue;
                else
                {
                    if (topLevel.Contains(name)) export = name;
                    msg = (kind == "func" ? $"global function '{name}'" : $"global '{name}' assigned")
                        + (export != null ? " and no Lua reads it as a module export" : "")
                        + " (missing local? else list it in a `lua-check: globals` comment)";
                }
            }
            if (msg == null) continue;
            var w = new Finding(file, line, msg, export, boolName);
            if (!warnings.Contains(w)) warnings.Add(w);
        }
    }

    static string Get(string[] lines, int n) => n >= 1 && n <= lines.Length ? lines[n - 1] : "";

    static int[] LineStarts(string text)
    {
        var list = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') list.Add(i + 1);
        return list.ToArray();
    }

    static int LineOf(int[] starts, int index)
    {
        var i = Array.BinarySearch(starts, index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    // The C# line holding chunk line n: where that line's first code sits.
    static int SourceLine(Variant v, int n, int[] starts)
    {
        var code = v.Code;
        var off = 0;
        for (var k = 1; k < n; k++) { var nl = code.IndexOf('\n', off); if (nl < 0) break; off = nl + 1; }
        var p = off;
        while (p < code.Length && code[p] != '\n' && char.IsWhiteSpace(code[p])) p++;
        if (p < code.Length && code[p] != '\n') off = p;
        var piece = v.Map.LastOrDefault(x => x.At <= off) ?? v.Map[0];
        var line = LineOf(starts, piece.Src);
        if (piece.Verbatim) line += code.Substring(piece.At, Math.Max(0, off - piece.At)).Count(c => c == '\n');
        return line;
    }

    // A chunk, else an expression: pieces like "{key=" + k + ",mode='s'}"
    // are table constructors assembled into a bigger chunk later.
    static string Compile(string code, string name)
    {
        var err = Load(code, name);
        return err == null || Load("return " + code, name) == null ? null : err;
    }

    static string Load(string code, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        var r = _load(_state, bytes, (UIntPtr)bytes.Length, name);
        var msg = r == 0 ? null : Marshal.PtrToStringAnsi(_tostring(_state, -1, IntPtr.Zero)) ?? "syntax error";
        _settop(_state, 0);
        return msg;
    }

    // `// lua-check: ignore` on the line above a literal skips it (for chunks
    // whose interpolations make placeholder-substitution meaningless).
    static bool Ignored(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
        var prevStart = lineStart <= 0 ? 0 : text.LastIndexOf('\n', lineStart - 1) + 1;
        return text.Substring(prevStart, Math.Max(0, index - prevStart)).Contains("lua-check: ignore");
    }

    // Lua chunks in C# source, with the index of where each starts.
    // Operands between + are tried two ways: as a name (a value spliced into
    // an expression) and as nothing (an optional statement, cond ? "x = 1 " : "").
    // Variants are the forms to lint, best first (see Run).
    static IEnumerable<(string Code, string Alt, int Index, Variant[] Variants)> CsChunks(string text)
    {
        var tokens = Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == 'v')
            {
                if (t.Value.Length > 40 && LuaMarkers.IsMatch(t.Value))
                {
                    var v = new Variant();
                    v.Map.Add(new Piece(0, t.Index, true));
                    v.Sb.Append(t.Value);
                    yield return (t.Value, null, t.Index, new[] { v });
                }
                continue;
            }
            if (t.Kind != 's') continue;
            // A run of literals joined by +, with other operands between them.
            // lined/lalt: the same with each literal on a new Lua line.
            var sb = new Variant(); var alt = new Variant(); var lined = new Variant(); var lalt = new Variant();
            var all = new[] { lined, lalt, sb, alt };
            void Literal(Token tok)
            {
                foreach (var v in all)
                {
                    if ((v == lined || v == lalt) && v.Sb.Length > 0) v.Sb.Append('\n');
                    v.Map.Add(new Piece(v.Sb.Length, tok.Index, false));
                    v.Sb.Append(tok.Value);
                }
            }
            Literal(t);
            var j = i + 1;
            while (j + 1 < tokens.Count && tokens[j].Kind == '+')
            {
                var next = tokens[j + 1];
                if (next.Kind == 's') Literal(next);
                else if (next.Kind == 'x') { sb.Sb.Append(" __x "); lined.Sb.Append(" __x "); alt.Sb.Append(' '); lalt.Sb.Append(' '); }
                else break;
                j += 2;
            }
            if (j > i + 1)
            {
                var code = sb.Code;
                if (code.Length > 60 && LuaMarkers.IsMatch(code)) yield return (code, alt.Code, t.Index, all);
                i = j - 1;
            }
        }
    }

    // Just enough C#: string literals ('s' regular, 'v' verbatim, both with
    // interpolation holes replaced), '+' and 'x' for any other operand
    // between pluses, and ';' / ',' / ')' as run breakers.
    record Token(char Kind, string Value, int Index);

    static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2); i = e < 0 ? s.Length : e + 2; continue; }
            if (c == '\'' ) { i++; while (i < s.Length && s[i] != '\'') { if (s[i] == '\\') i++; i++; } i++; continue; }

            var interp = false; var verbatim = false; var start = i;
            var k = i;
            while (k < s.Length && (s[k] == '$' || s[k] == '@')) { if (s[k] == '$') interp = true; else verbatim = true; k++; }
            if (k < s.Length && s[k] == '"' && (k - i) <= 2 && !(k + 2 < s.Length && s[k + 1] == '"' && s[k + 2] == '"'))
            {
                i = k + 1;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    var ch = s[i];
                    if (verbatim && ch == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; } i++; break; }
                    if (!verbatim && ch == '"') { i++; break; }
                    if (!verbatim && ch == '\\' && i + 1 < s.Length)
                    {
                        var n = s[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => n });
                        i += 2; continue;
                    }
                    if (interp && ch == '{')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                        var depth = 1; i++;
                        while (i < s.Length && depth > 0) { if (s[i] == '{') depth++; else if (s[i] == '}') depth--; i++; }
                        sb.Append(" __x "); continue;
                    }
                    if (interp && ch == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                    sb.Append(ch); i++;
                }
                list.Add(new Token(verbatim ? 'v' : 's', sb.ToString(), start));
                continue;
            }
            if (c == '+') { list.Add(new Token('+', "+", i)); i++; continue; }
            if (c == ';' || c == ',' || c == ')' || c == '{' || c == '}' || c == '(' && list.Count > 0 && list[^1].Kind != '+')
            {
                list.Add(new Token(';', c.ToString(), i)); i++; continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '(' || c == '[')
            {
                // An operand: identifiers, member access, calls, indexers and
                // parenthesised expressions, strings inside them included.
                var st = i; var depth = 0;
                while (i < s.Length)
                {
                    var ch = s[i];
                    if (ch == '(' || ch == '[') depth++;
                    else if (ch == ')' || ch == ']') { if (depth == 0) break; depth--; }
                    else if (depth == 0 && !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')) break;
                    else if (depth > 0 && ch == '"')
                    {
                        var verb = i > 0 && s[i - 1] == '@';
                        i++;
                        while (i < s.Length && s[i] != '"') { if (!verb && s[i] == '\\') i++; i++; }
                    }
                    i++;
                }
                if (i == st) i++;
                i = Math.Min(i, s.Length);
                list.Add(new Token('x', s.Substring(st, i - st), st));
                continue;
            }
            i++;
        }
        return list;
    }
}
