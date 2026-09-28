using System;
using System.Collections.Generic;

namespace Sanctuary.ModApi
{
    /// Lobby and match events for mods. Every subscription names its owner,
    /// normally your plugin (`this`): when the owner is destroyed — switched
    /// off, or replaced by a hot reload — its handlers are dropped, so an old
    /// copy of your code never runs again. Handlers run on the main thread.
    public static class ModEvents
    {
        private sealed class Sub
        {
            public UnityEngine.Object Owner;
            public Delegate Handler;
        }

        private static readonly Dictionary<string, List<Sub>> Subs = new Dictionary<string, List<Sub>>();

        /// You joined or created a lobby. The argument is true when you host.
        public static void OnLobbyEntered(UnityEngine.Object owner, Action<bool> handler) => Add("entered", owner, handler);

        /// You left the lobby, or the match it started has ended.
        public static void OnLobbyLeft(UnityEngine.Object owner, Action handler) => Add("left", owner, handler);

        /// The gameplay mods applied on this machine changed (the host picked
        /// different ones or changed their options, or a picked mod's files
        /// changed).
        public static void OnSelectionChanged(UnityEngine.Object owner, Action handler) => Add("selection", owner, handler);

        /// The host pressed Start and the match is loading.
        public static void OnMatchStarting(UnityEngine.Object owner, Action handler) => Add("starting", owner, handler);

        /// The match was cleaned up (quit, lost, won, or left).
        public static void OnMatchEnded(UnityEngine.Object owner, Action handler) => Add("ended", owner, handler);

        /// Drops every handler the owner registered.
        public static void Unsubscribe(UnityEngine.Object owner)
        {
            foreach (var list in Subs.Values) list.RemoveAll(s => ReferenceEquals(s.Owner, owner));
        }

        private static void Add(string key, UnityEngine.Object owner, Delegate handler)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner), "Pass your plugin as the owner, so a hot reload can drop the handler.");
            if (handler == null) return;
            if (!Subs.TryGetValue(key, out var list)) Subs[key] = list = new List<Sub>();
            list.Add(new Sub { Owner = owner, Handler = handler });
        }

        private static void Raise(string key, object arg = null, bool hasArg = false)
        {
            if (!Subs.TryGetValue(key, out var list)) return;
            // Unity's == null is true for a destroyed object.
            list.RemoveAll(s => s.Owner == null);
            foreach (var s in list.ToArray())
            {
                try
                {
                    if (hasArg) ((Action<bool>)s.Handler)((bool)arg);
                    else ((Action)s.Handler)();
                }
                catch (Exception e)
                {
                    ModApiPlugin.Log.LogError($"A mod's {key} handler ({s.Owner.GetType().FullName}) threw: {e}");
                }
            }
        }

        internal static void RaiseLobbyEntered(bool host) => Raise("entered", host, true);
        internal static void RaiseLobbyLeft() => Raise("left");
        internal static void RaiseSelectionChanged() => Raise("selection");
        internal static void RaiseMatchStarting() => Raise("starting");
        internal static void RaiseMatchEnded() => Raise("ended");
    }
}
