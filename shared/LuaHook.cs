using System;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // A piece of Lua a mod keeps installed in the client VM: a wrapper on a
    // game function, or a table of functions its polls call. The same loop
    // every hook needs, in one place:
    //
    //  - every match brings a fresh VM, so it goes back in when the VM does;
    //  - a VM swapped between two checks (a match that starts and ends
    //    without LuaReady reading false) is caught by a marker global, not
    //    trusted to a flag;
    //  - the marker is a hash of the chunk, so a hot reload with edited Lua
    //    (or a chunk built from settings that changed) replaces the old one;
    //  - a chunk that errors is retried on a back-off, not every check;
    //  - Remove undoes it, for switching off and for OnDestroy.
    //
    // The install chunk must define the global named by `table`. The default
    // remove calls `table.Remove()` when there is one and clears `table`.
    // Install chunks run once per VM, so a poll that calls a function
    // defined there (`hook.Call("__SdbX.Poll()")`) compiles a few bytes, not
    // the whole poll, each time.
    internal sealed class LuaHook
    {
        private readonly string _table;
        private readonly string _what;
        private readonly Func<string> _install;
        private readonly string _remove;
        private readonly string _markerGlobal;

        /// Seconds between checks that it is still in place.
        internal float CheckEvery = 1f;
        /// Seconds before a chunk that raised an error is tried again.
        internal float RetryAfter = 30f;
        /// Log the install at Info (off for hooks that go in every match quietly).
        internal bool LogInstalls = true;

        /// Runs after each install: reset anything pushed into the old VM.
        internal Action Installed;

        private float _nextCheck;
        private float _retryAt;
        private string _hash;
        private string _hashedChunk;

        internal bool Live { get; private set; }

        /// <param name="table">The Lua global the chunk defines, e.g. "__SdbCmdrGuard".</param>
        /// <param name="what">What it is, for the log ("commander delete guard").</param>
        /// <param name="install">The install chunk. Asked for once per check, so
        /// a chunk built from settings should be cached by the caller.</param>
        /// <param name="remove">Undoes it; defaults to calling table.Remove().</param>
        internal LuaHook(string table, string what, Func<string> install, string remove = null)
        {
            _table = table;
            _what = what;
            _install = install;
            _markerGlobal = table + "Hash";
            _remove = remove ?? $"if {table} and {table}.Remove then {table}.Remove() end {table} = nil";
        }

        internal LuaHook(string table, string what, string install, string remove = null)
            : this(table, what, () => install, remove)
        {
        }

        /// Call every frame (or from any periodic tick); it checks on its own
        /// interval. `wanted` false takes it out.
        internal void Tick(bool wanted = true)
        {
            var now = Time.unscaledTime;
            if (now < _nextCheck) return;
            _nextCheck = now + CheckEvery;

            EnsureLuaBridge();
            if (!LuaReady)
            {
                Live = false;
                return;
            }
            if (!wanted)
            {
                if (Live) Remove();
                return;
            }

            var chunk = _install();
            if (!ReferenceEquals(chunk, _hashedChunk))
            {
                _hashedChunk = chunk;
                _hash = Hash(chunk);
            }
            var marker = GetLuaGlobal(_markerGlobal);
            if (marker == _hash)
            {
                if (!Live)
                {
                    // Already there: put in by an earlier copy of this mod
                    // (a hot reload) or by the last check.
                    Live = true;
                    Installed?.Invoke();
                }
                return;
            }

            // Gone (a new VM) or a different version of it.
            Live = false;
            if (now < _retryAt) return;
            if (marker != null) RunLua(_remove + " " + _markerGlobal + " = nil");
            if (RunLua(chunk + "\n" + _markerGlobal + " = '" + _hash + "'"))
            {
                Live = true;
                _retryAt = 0f;
                if (LogInstalls) _log?.LogInfo($"Lua: {_what} installed.");
                Installed?.Invoke();
            }
            else
            {
                _retryAt = now + RetryAfter;
            }
        }

        /// Takes it out of the VM, if there is one. Safe to call any time.
        internal void Remove()
        {
            Live = false;
            _nextCheck = 0f;
            try
            {
                if (LuaReady && GetLuaGlobal(_markerGlobal) != null)
                    RunLua(_remove + " " + _markerGlobal + " = nil");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Lua: {_what} could not be removed: {e.Message}");
            }
        }

        /// Runs a call into the installed table; false while it is not in.
        /// Guarded in Lua too, for a VM swapped since the last check.
        internal bool Call(string chunk) => Live && RunLua("if " + _table + " then " + chunk + " end");

        /// Checks again on the next Tick instead of waiting out the interval.
        internal void CheckNow()
        {
            _nextCheck = 0f;
            _retryAt = 0f;
        }

        /// The marker: FNV-1a of the chunk, the same in every process.
        private static string Hash(string s)
        {
            var h = 2166136261u;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
