using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SanctuaryHud
{
    // The calls into the client's Lua VM, emitted once per assembly that
    // compiles shared/ (HudCore's RunLua and ModApi's ModLua both sit on it).
    //
    // ClientLuaInterface.Data is `ref Unmanaged` over a Burst SharedStatic,
    // and reflection refuses to invoke ByRef-returning getters, so tiny
    // methods are emitted that read luaState in IL. Outside a match the state
    // is a null handle, and handing that to LuaJIT is a native access
    // violation no managed catch can stop: every caller checks Ready first.
    internal static class LuaBridge
    {
        internal sealed class Calls
        {
            /// Runs a chunk: null when it ran, else the Lua error. The stack
            /// is put back as it was either way, so neither the chunk's
            /// return values nor an error message pile up on it.
            internal Func<string, string> Run;

            /// A _G global as a string (numbers convert), or null. Booleans
            /// and tables read as null.
            internal Func<string, string> GetGlobal;

            /// True while the client VM exists.
            internal Func<bool> Ready;
        }

        /// Null, with what's missing in <paramref name="missing"/>, when the
        /// game's Lua interface has changed shape.
        internal static Calls Emit(Type owner, out string missing)
        {
            var luaJit = TypeIndex.ByName("LuaJIT");
            var cli = TypeIndex.ByFullName("EM.Lua.Client.ClientLuaInterface");
            const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
            var doString = luaJit?.GetMethod("luaL_dostring", Static);
            var getTop = luaJit?.GetMethod("lua_gettop", Static);
            var setTop = luaJit?.GetMethod("lua_settop", Static);
            var toString = luaJit?.GetMethod("lua_tostring", Static);
            var getGlobal = luaJit?.GetMethod("lua_getglobal", Static);
            var dataGetter = cli?.GetProperty("Data", Static)?.GetGetMethod();
            var unmanaged = dataGetter?.ReturnType;
            if (unmanaged != null && unmanaged.IsByRef) unmanaged = unmanaged.GetElementType();
            var stateField = unmanaged?.GetField("luaState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // lua_State is a struct over one nuint: no VM while it is 0.
            var handleField = stateField?.FieldType.GetField("Handle", BindingFlags.Public | BindingFlags.Instance);

            var gaps = new List<string>();
            if (doString == null) gaps.Add("luaL_dostring");
            if (getTop == null) gaps.Add("lua_gettop");
            if (setTop == null) gaps.Add("lua_settop");
            if (toString == null) gaps.Add("lua_tostring");
            if (getGlobal == null) gaps.Add("lua_getglobal");
            if (dataGetter == null) gaps.Add("ClientLuaInterface.Data");
            if (stateField == null) gaps.Add("luaState");
            if (handleField == null) gaps.Add("lua_State.Handle");
            missing = string.Join(", ", gaps.ToArray());
            if (gaps.Count > 0) return null;

            var calls = new Calls();

            // string Run(string chunk):
            //   var L = Data.luaState; var top = lua_gettop(L);
            //   string err = luaL_dostring(L, chunk) == 0 ? null : (lua_tostring(L, -1) ?? "error");
            //   lua_settop(L, top); return err;
            var run = new DynamicMethod(owner.Name + "_RunLua", typeof(string), new[] { typeof(string) }, owner, true);
            var il = run.GetILGenerator();
            var state = il.DeclareLocal(stateField.FieldType);
            var top = il.DeclareLocal(typeof(int));
            var err = il.DeclareLocal(typeof(string));
            var done = il.DefineLabel();
            il.Emit(OpCodes.Call, dataGetter);
            il.Emit(OpCodes.Ldfld, stateField);
            il.Emit(OpCodes.Stloc, state);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Call, getTop);
            il.Emit(OpCodes.Stloc, top);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, doString);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Call, toString);
            il.Emit(OpCodes.Dup);
            var haveMessage = il.DefineLabel();
            il.Emit(OpCodes.Brtrue, haveMessage);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, "error");
            il.MarkLabel(haveMessage);
            il.Emit(OpCodes.Stloc, err);
            il.MarkLabel(done);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Ldloc, top);
            il.Emit(OpCodes.Call, setTop);
            il.Emit(OpCodes.Ldloc, err);
            il.Emit(OpCodes.Ret);
            calls.Run = (Func<string, string>)run.CreateDelegate(typeof(Func<string, string>));

            // string GetGlobal(string name): push it, convert, pop.
            var gm = new DynamicMethod(owner.Name + "_GetLuaGlobal", typeof(string), new[] { typeof(string) }, owner, true);
            var g = gm.GetILGenerator();
            var gState = g.DeclareLocal(stateField.FieldType);
            var result = g.DeclareLocal(typeof(string));
            g.Emit(OpCodes.Call, dataGetter);
            g.Emit(OpCodes.Ldfld, stateField);
            g.Emit(OpCodes.Stloc, gState);
            g.Emit(OpCodes.Ldloc, gState);
            g.Emit(OpCodes.Ldarg_0);
            g.Emit(OpCodes.Call, getGlobal);
            g.Emit(OpCodes.Ldloc, gState);
            g.Emit(OpCodes.Ldc_I4_M1);
            g.Emit(OpCodes.Call, toString);
            g.Emit(OpCodes.Stloc, result);
            g.Emit(OpCodes.Ldloc, gState);
            g.Emit(OpCodes.Ldc_I4_S, (sbyte)-2);
            g.Emit(OpCodes.Call, setTop);
            g.Emit(OpCodes.Ldloc, result);
            g.Emit(OpCodes.Ret);
            calls.GetGlobal = (Func<string, string>)gm.CreateDelegate(typeof(Func<string, string>));

            // bool Ready(): Data.luaState.Handle != 0.
            var rm = new DynamicMethod(owner.Name + "_LuaStateReady", typeof(bool), Type.EmptyTypes, owner, true);
            var r = rm.GetILGenerator();
            r.Emit(OpCodes.Call, dataGetter);
            r.Emit(OpCodes.Ldfld, stateField);
            r.Emit(OpCodes.Ldfld, handleField);
            r.Emit(OpCodes.Ldc_I4_0);
            r.Emit(OpCodes.Conv_U);
            r.Emit(OpCodes.Cgt_Un);
            r.Emit(OpCodes.Ret);
            calls.Ready = (Func<bool>)rm.CreateDelegate(typeof(Func<bool>));

            return calls;
        }

        /// A chunk cut down for a log line.
        internal static string Trim(string chunk)
        {
            if (chunk == null) return "";
            chunk = chunk.Trim();
            return chunk.Length > 160 ? chunk.Substring(0, 160) + "…" : chunk;
        }
    }

    // Every loaded type by name, built once instead of each resolver scanning
    // every assembly again. Rebuilt when an assembly has loaded since, so a
    // miss early in startup doesn't stick.
    internal static class TypeIndex
    {
        private static Dictionary<string, Type> _byFullName;
        private static Dictionary<string, Type> _byName;
        private static int _assemblyCount;

        internal static Type ByFullName(string fullName)
        {
            Ensure();
            if (_byFullName.TryGetValue(fullName, out var t)) return t;
            return Refresh() && _byFullName.TryGetValue(fullName, out t) ? t : null;
        }

        /// The first type with this simple name (load order, as a linear scan
        /// would find it).
        internal static Type ByName(string name)
        {
            Ensure();
            if (_byName.TryGetValue(name, out var t)) return t;
            return Refresh() && _byName.TryGetValue(name, out t) ? t : null;
        }

        internal static IEnumerable<Type> All
        {
            get
            {
                Ensure();
                return _byFullName.Values;
            }
        }

        private static void Ensure()
        {
            if (_byFullName == null) Build();
        }

        private static bool Refresh()
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Length == _assemblyCount) return false;
            Build();
            return true;
        }

        private static void Build()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            _assemblyCount = assemblies.Length;
            var byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
            var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var a in assemblies)
            {
                if (a.IsDynamic) continue;
                foreach (var t in Types(a))
                {
                    if (t.FullName != null && !byFullName.ContainsKey(t.FullName)) byFullName[t.FullName] = t;
                    if (!byName.ContainsKey(t.Name)) byName[t.Name] = t;
                }
            }
            _byFullName = byFullName;
            _byName = byName;
        }

        internal static Type[] Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
            catch { return Type.EmptyTypes; }
        }
    }
}
