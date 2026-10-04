using System;
using SanctuaryHud;
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
        private static LuaBridge.Calls _lua;
        private static float _nextTry;
        private static string _lastError;

        /// True while the client VM exists (in a match or replay, after
        /// loading). Everything else here is a no-op until it is.
        public static bool Ready
        {
            get
            {
                Resolve();
                try { return _lua != null && _lua.Ready(); }
                catch { return false; }
            }
        }

        /// Runs a chunk in the client VM. False when there is no VM yet or
        /// the chunk raised an error (logged), so callers can simply retry.
        public static bool Run(string chunk)
        {
            if (!Ready) return false;
            try
            {
                var err = _lua.Run(chunk);
                if (err == null) return true;
                if (err != _lastError)
                {
                    _lastError = err;
                    ModApiPlugin.Log.LogWarning($"Lua chunk failed: {err} (in: {LuaBridge.Trim(chunk)})");
                }
                return false;
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
            if (!Ready) return null;
            try { return _lua.GetGlobal(name); }
            catch { return null; }
        }

        // The emitted calls are shared with the HUD mods (shared/LuaBridge.cs).
        private static void Resolve()
        {
            if (_lua != null) return;
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 5f;
            try
            {
                _lua = LuaBridge.Emit(typeof(ModLua), out var missing);
                if (_lua == null) ModApiPlugin.Log.LogWarning($"ModLua unavailable: the game's Lua interface has changed shape (missing {missing}).");
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"ModLua resolve failed: {e.Message}");
            }
        }
    }
}
