using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Sanctuary.ModApi
{
    /// Runs Lua in your own client's VM: presentation-side only. Nothing run
    /// here reaches the host's simulation or other players; to change the
    /// match itself, ship Lua files in a gameplay mod instead.
    ///
    /// Call from the main thread (Update, a Harmony patch on game code).
    /// Every call is safe outside a match: it returns false or null rather
    /// than touching a VM that doesn't exist.
    ///
    /// Remember that each game Lua file is its own module: its "globals" are
    /// not in _G. Reach them through the module, as in
    /// <c>Import('client/inputEventsFunctions.lua').GetHoverUnit()</c>.
    public static class ModLua
    {
        private static Func<string, int> _runChunk;
        private static Func<string, string> _getGlobal;
        private static Func<bool> _stateReady;
        private static float _nextTry;

        /// True while the client VM exists (in a match or replay, after
        /// loading). Everything else here is a no-op until it is.
        public static bool Ready
        {
            get
            {
                Resolve();
                try { return _stateReady != null && _stateReady(); }
                catch { return false; }
            }
        }

        /// Runs a chunk in the client VM. False when there is no VM yet or
        /// the chunk raised an error (logged), so callers can simply retry.
        public static bool Run(string chunk)
        {
            if (!Ready || _runChunk == null) return false;
            try
            {
                var code = _runChunk(chunk);
                if (code != 0) ModApiPlugin.Log.LogWarning($"Lua chunk failed (code {code}): {Trim(chunk)}");
                return code == 0;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Lua call failed: {e.Message}");
                return false;
            }
        }

        /// Reads a _G global from the client VM as a string (numbers are
        /// converted), or null. Booleans and tables read as null: expose a
        /// number or a string when you need to read something back.
        public static string GetGlobal(string name)
        {
            if (!Ready || _getGlobal == null) return null;
            try { return _getGlobal(name); }
            catch { return null; }
        }

        private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;

        // ClientLuaInterface.Data is `ref Unmanaged` over a Burst SharedStatic,
        // and reflection refuses to invoke ByRef-returning getters, so tiny
        // methods are emitted that read luaState in IL. Outside a match the
        // state is a null handle, and handing that to LuaJIT is a native
        // access violation no managed catch can stop — hence the readiness
        // check guards every call.
        private static void Resolve()
        {
            if (_stateReady != null) return;
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 5f;
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).SelectMany(SafeTypes).ToList();
                var luaJit = types.FirstOrDefault(t => t.Name == "LuaJIT");
                var doString = luaJit?.GetMethod("luaL_dostring", BindingFlags.Public | BindingFlags.Static);
                var cli = types.FirstOrDefault(t => t.FullName == "EM.Lua.Client.ClientLuaInterface");
                var dataGetter = cli?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                var unmanaged = dataGetter?.ReturnType;
                if (unmanaged != null && unmanaged.IsByRef) unmanaged = unmanaged.GetElementType();
                var stateField = unmanaged?.GetField("luaState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var handleField = stateField?.FieldType.GetField("Handle", BindingFlags.Public | BindingFlags.Instance);
                if (doString == null || dataGetter == null || stateField == null || handleField == null)
                {
                    ModApiPlugin.Log.LogWarning("ModLua unavailable: the game's Lua interface has changed shape.");
                    return;
                }

                var run = new DynamicMethod("ModApi_RunLua", typeof(int), new[] { typeof(string) }, typeof(ModLua), true);
                var il = run.GetILGenerator();
                il.Emit(OpCodes.Call, dataGetter);
                il.Emit(OpCodes.Ldfld, stateField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, doString);
                il.Emit(OpCodes.Ret);
                _runChunk = (Func<string, int>)run.CreateDelegate(typeof(Func<string, int>));

                var getGlobal = luaJit.GetMethod("lua_getglobal", BindingFlags.Public | BindingFlags.Static);
                var toString = luaJit.GetMethod("lua_tostring", BindingFlags.Public | BindingFlags.Static);
                var setTop = luaJit.GetMethod("lua_settop", BindingFlags.Public | BindingFlags.Static);
                if (getGlobal != null && toString != null && setTop != null)
                {
                    var gm = new DynamicMethod("ModApi_GetLuaGlobal", typeof(string), new[] { typeof(string) }, typeof(ModLua), true);
                    var g = gm.GetILGenerator();
                    var state = g.DeclareLocal(stateField.FieldType);
                    var result = g.DeclareLocal(typeof(string));
                    g.Emit(OpCodes.Call, dataGetter);
                    g.Emit(OpCodes.Ldfld, stateField);
                    g.Emit(OpCodes.Stloc, state);
                    g.Emit(OpCodes.Ldloc, state);
                    g.Emit(OpCodes.Ldarg_0);
                    g.Emit(OpCodes.Call, getGlobal);
                    g.Emit(OpCodes.Ldloc, state);
                    g.Emit(OpCodes.Ldc_I4_M1);
                    g.Emit(OpCodes.Call, toString);
                    g.Emit(OpCodes.Stloc, result);
                    g.Emit(OpCodes.Ldloc, state);
                    g.Emit(OpCodes.Ldc_I4_S, (sbyte)-2);
                    g.Emit(OpCodes.Call, setTop);
                    g.Emit(OpCodes.Ldloc, result);
                    g.Emit(OpCodes.Ret);
                    _getGlobal = (Func<string, string>)gm.CreateDelegate(typeof(Func<string, string>));
                }

                // Last, so Ready stays false unless everything above worked.
                var rm = new DynamicMethod("ModApi_LuaStateReady", typeof(bool), Type.EmptyTypes, typeof(ModLua), true);
                var r = rm.GetILGenerator();
                r.Emit(OpCodes.Call, dataGetter);
                r.Emit(OpCodes.Ldfld, stateField);
                r.Emit(OpCodes.Ldfld, handleField);
                r.Emit(OpCodes.Ldc_I4_0);
                r.Emit(OpCodes.Conv_U);
                r.Emit(OpCodes.Cgt_Un);
                r.Emit(OpCodes.Ret);
                _stateReady = (Func<bool>)rm.CreateDelegate(typeof(Func<bool>));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"ModLua resolve failed: {e.Message}");
            }
        }

        private static Type[] SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
            catch { return Type.EmptyTypes; }
        }
    }
}
