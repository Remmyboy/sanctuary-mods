using System;
using System.Runtime.InteropServices;

namespace Sanctuary.ModApi
{
    /// Compiles Lua without running it, with the game's own LuaJIT
    /// (lua51.dll, already loaded by the game), so a mod's syntax error is
    /// reported by name when the overlay goes on, instead of surfacing as a
    /// failed Import somewhere inside a match.
    internal static class LuaSyntax
    {
        [DllImport("lua51", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr luaL_newstate();

        [DllImport("lua51", CallingConvention = CallingConvention.Cdecl)]
        private static extern int luaL_loadbuffer(IntPtr L, byte[] buff, UIntPtr size, string name);

        [DllImport("lua51", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lua_tolstring(IntPtr L, int index, IntPtr length);

        [DllImport("lua51", CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_settop(IntPtr L, int index);

        private static IntPtr _state;
        private static bool _unavailable;

        /// Null when the code compiles (or when it can't be checked), else
        /// LuaJIT's error message.
        internal static string Check(byte[] code, string name)
        {
            if (_unavailable) return null;
            try
            {
                if (_state == IntPtr.Zero) _state = luaL_newstate();
                if (_state == IntPtr.Zero) { _unavailable = true; return null; }
                var result = luaL_loadbuffer(_state, code, (UIntPtr)code.Length, "@" + name);
                var message = result == 0 ? null : Marshal.PtrToStringAnsi(lua_tolstring(_state, -1, IntPtr.Zero)) ?? "syntax error";
                lua_settop(_state, 0);
                return message;
            }
            catch (Exception e)
            {
                _unavailable = true;
                ModApiPlugin.Log?.LogWarning($"Lua syntax checks unavailable ({e.Message}); mods' Lua is applied unchecked.");
                return null;
            }
        }
    }
}
