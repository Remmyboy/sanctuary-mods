// GameRef luarun <lua51.dll> <script.lua> [args...]
//
// Runs a Lua script with the game's own LuaJIT and the standard libraries (io, os, ffi,
// ...), outside the game: no Engine, no Import. The script sees its arguments in the
// global `arg` (1-based, like the stand-alone interpreter) and its own path in arg[0].
// Errors print with a traceback and exit 1. Used by BalancePatch/tools/balance/dump.lua.
using System.Runtime.InteropServices;
using System.Text;

static class LuaRun
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NewState();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void OpenLibs(IntPtr L);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadBuffer(IntPtr L, byte[] buff, UIntPtr size, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PCall(IntPtr L, int nargs, int nresults, int errfunc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ToLString(IntPtr L, int index, IntPtr length);

    static string LuaQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            bool plain = b >= 32 && b < 127 && b != (byte)'"' && b != (byte)'\\';
            sb.Append(plain ? ((char)b).ToString() : "\\" + b.ToString("000"));
        }
        return sb.Append('"').ToString();
    }

    public static int Run(string dll, string script, IEnumerable<string> args)
    {
        var lib = NativeLibrary.Load(dll);
        T Fn<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        var L = Fn<NewState>("luaL_newstate")();
        Fn<OpenLibs>("luaL_openlibs")(L);

        var full = Path.GetFullPath(script).Replace('\\', '/');
        var argList = string.Join(", ", args.Select(LuaQuote));
        var boot = $"arg = {{ [0] = {LuaQuote(full)}, {argList} }}\n" +
                   $"local f, err = loadfile({LuaQuote(full)})\n" +
                   "if not f then error(err, 0) end\n" +
                   "local ok, e = xpcall(function() return f(unpack(arg)) end, debug.traceback)\n" +
                   "io.stdout:flush()\n" +
                   "if not ok then error(e, 0) end\n";
        var bytes = Encoding.UTF8.GetBytes(boot);
        if (Fn<LoadBuffer>("luaL_loadbuffer")(L, bytes, (UIntPtr)bytes.Length, "=luarun") != 0
            || Fn<PCall>("lua_pcall")(L, 0, 0, 0) != 0)
        {
            Console.Out.Flush();
            Console.Error.WriteLine(Marshal.PtrToStringUTF8(Fn<ToLString>("lua_tolstring")(L, -1, IntPtr.Zero)));
            return 1;
        }
        return 0;
    }
}
