// GameRef lua <lua51.dll> <file-or-dir>...
//
// Compiles Lua with the game's own LuaJIT (the same check ModApi's
// LuaSyntax runs in game) without running it: every .lua file, and every
// chunk of Lua held in a C# string - a verbatim @"..." literal, or a run of
// "..." + "..." literals - with interpolations and non-literal operands
// replaced by a placeholder name. Reports LuaJIT's message with the C# line.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static class LuaCheck
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NewState();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadBuffer(IntPtr L, byte[] buff, UIntPtr size, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ToLString(IntPtr L, int index, IntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetTop(IntPtr L, int index);

    static LoadBuffer _load;
    static ToLString _tostring;
    static SetTop _settop;
    static IntPtr _state;

    // Looks like Lua rather than prose (setting descriptions say "then" and
    // "end" too), a path or a UI string.
    static readonly Regex LuaMarkers = new(@"\blocal\s+[\w, ]+=|\bfunction\s*[\w.:]*\s*\(|\bImport\s*\(|\bpcall\s*\(|\b_G\.\w|\bif\b[^\n]*\bthen\b|^\s*\{\s*\w+\s*=");

    public static int Run(string dll, IEnumerable<string> targets)
    {
        var lib = NativeLibrary.Load(dll);
        T Fn<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        _state = Fn<NewState>("luaL_newstate")();
        _load = Fn<LoadBuffer>("luaL_loadbuffer");
        _tostring = Fn<ToLString>("lua_tolstring");
        _settop = Fn<SetTop>("lua_settop");

        var files = targets.SelectMany(t => Directory.Exists(t)
                ? Directory.EnumerateFiles(t, "*.*", SearchOption.AllDirectories)
                    .Where(f => !Regex.IsMatch("/" + Path.GetRelativePath(t, f), @"[\\/](bin|obj|\.git|\.claude|release)[\\/]"))
                : new[] { t })
            .Where(f => f.EndsWith(".lua") || f.EndsWith(".cs"))
            .ToList();

        int chunks = 0, errors = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".lua"))
            {
                chunks++;
                var err = Compile(text, Path.GetFileName(file));
                if (err != null) { errors++; Console.WriteLine($"{file}: {err}"); }
                continue;
            }
            foreach (var (code, alt, index) in CsChunks(text))
            {
                if (Ignored(text, index)) continue;
                chunks++;
                var err = Compile(code, "chunk");
                if (err == null || (alt != null && Compile(alt, "chunk") == null)) continue;
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
        Console.WriteLine($"{chunks} chunk(s)/file(s) compiled, {errors} error(s).");
        return errors == 0 ? 0 : 1;
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
    static IEnumerable<(string Code, string Alt, int Index)> CsChunks(string text)
    {
        var tokens = Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == 'v')
            {
                if (t.Value.Length > 40 && LuaMarkers.IsMatch(t.Value)) yield return (t.Value, null, t.Index);
                continue;
            }
            if (t.Kind != 's') continue;
            // A run of literals joined by +, with other operands between them.
            var sb = new StringBuilder(t.Value);
            var alt = new StringBuilder(t.Value);
            var j = i + 1;
            while (j + 1 < tokens.Count && tokens[j].Kind == '+')
            {
                var next = tokens[j + 1];
                if (next.Kind == 's') { sb.Append(next.Value); alt.Append(next.Value); }
                else if (next.Kind == 'x') { sb.Append(" __x "); alt.Append(" "); }
                else break;
                j += 2;
            }
            if (j > i + 1)
            {
                var code = sb.ToString();
                if (code.Length > 60 && LuaMarkers.IsMatch(code)) yield return (code, alt.ToString(), t.Index);
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
