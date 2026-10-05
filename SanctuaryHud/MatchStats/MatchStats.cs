using System;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The post-game screen: every army's economy and units over the match,
    // as a table and a set of charts, opened when the game's own victory or
    // defeat panel comes up. (Once its own mod, MatchStats 0.1.0.)
    //
    // Nothing is shown while the match is being played. The host sends
    // every army's economy and every unit's lifecycle to every client (see
    // the netcode notes in the README) and the client throws away what
    // isn't its own; the Lua side here keeps it instead, in the client VM,
    // and this side only reads it out. The screen opens once the focused
    // army's result is in, which is the point where the game itself stops
    // hiding anything.
    //
    // Client-side only: the hooks are table-field wraps in the client VM
    // (no file touched, so the lobby's Lua hash is unchanged) and nothing
    // is sent anywhere. The hooks and the parser are shared source
    // (shared/Stats): LadderReporter installs the same collector for its
    // opt-in stats upload, and whichever mod goes first serves both.
    //
    // The screen has a full-screen container of its own on the HUD canvas,
    // beside the HUD's root rather than on it: the root hides with the HUD's
    // toggle key and once the match's economy stream stops, which is just
    // when this has to show.
    internal static class MatchStats
    {
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgAutoOpen;
        private static ConfigEntry<KeyCode> _cfgToggleKey;

        private static MatchData _data;
        private static bool _hooked;
        private static float _installAccum, _pullAccum, _bridgeAccum = 1f;
        private static string _lastErr;
        private static bool _resultSeen;
        private static int _endTick = -1;
        private static StatsScreen _screen;
        private static RectTransform _container;
        private static bool _dumped;

        internal static void Bind(ConfigFile config)
        {
            _cfgEnabled = config.Bind("MatchStats", "Enabled", true,
                "Keep every army's economy and units through the match, and show them as a table and charts when the game's " +
                "victory or defeat panel comes up, with a FAF-style score. Off stops keeping them from the next match on.");
            _cfgAutoOpen = config.Bind("MatchStats", "AutoOpen", true,
                "Open the stats as soon as the game's victory or defeat panel comes up. Off leaves just the MATCH STATS button under it.");
            _cfgToggleKey = config.Bind("MatchStats", "ToggleKey", KeyCode.F3,
                "Key that opens and closes the stats once the match has a result (never before).");
        }

        internal static void Shutdown() => Forget();

        /// From Update, every frame.
        internal static void Tick()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                if (_lastErr != e.Message)
                {
                    _lastErr = e.Message;
                    _log?.LogWarning($"Match stats: {e}");
                }
            }
        }

        private static void Step()
        {
            if (_cfgEnabled == null || !_cfgEnabled.Value)
            {
                // Hooks already in keep counting until the match ends (they
                // cannot be taken out cleanly); nothing is shown.
                if (_screen != null) Forget();
                return;
            }

            // The resolve walks every assembly's types, so a failed one is not
            // retried every frame.
            _bridgeAccum += Time.unscaledDeltaTime;
            if (_bridgeAccum >= 1f)
            {
                _bridgeAccum = 0f;
                EnsureLuaBridge();
            }
            if (!LuaReady)
            {
                // Back in the menu: the scene took the screen with it.
                if (_data != null) Forget();
                return;
            }

            // Hooked as early as the client VM allows, so the whole match is
            // seen; a quarter-second retry costs nothing once it has taken.
            if (!_hooked)
            {
                _installAccum += Time.unscaledDeltaTime;
                if (_installAccum < 0.25f) return;
                _installAccum = 0f;
                if (!RunLua(MatchStatsCore.InstallChunk)) return;
                _hooked = GetLuaGlobal("__SdbStatsHook") == "true";
                ReportLuaError();
                if (!_hooked) return;
                _log?.LogInfo("Match stats: hooks installed.");
            }

            var result = MatchStatsCore.ResultPanel();
            var resultShowing = result != null && result.IsVisible;

            _pullAccum += Time.unscaledDeltaTime;
            // Once the result is in, a pull is also what gives the screen
            // its last figures before it opens.
            if (_pullAccum >= 2f || (resultShowing && !_resultSeen)) Pull();

            if (resultShowing && !_resultSeen && _data != null)
            {
                _resultSeen = true;
                _endTick = _data.Tick;
                _log?.LogInfo($"Match stats: result screen up at tick {_endTick}; {_data.Players().Count} armies.");
                DumpResultPanel(result);
                if (_cfgAutoOpen.Value) Screen(result)?.Open(_data, _endTick);
            }

            if (!_resultSeen) return;
            var screen = Screen(result);
            if (screen == null) return;
            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                if (screen.IsOpen) screen.Close();
                else screen.Open(_data, _endTick);
            }
            screen.Update(_data, resultShowing ? result : null);
        }

        private static StatsScreen Screen(SanctuaryPanelUI beside)
        {
            if (_screen != null && _screen.Alive && _container != null) return _screen;
            var root = beside != null ? HudCanvas.Ensure(beside) : HudCanvas.Ensure();
            if (root == null || root.parent == null) return null;
            _screen?.Destroy();
            if (_container != null) UnityEngine.Object.Destroy(_container.gameObject);

            // Beside the HUD's root, covering the same area, so the screen's
            // layout (which measures the root) is unchanged; just above it.
            var go = new GameObject("Match stats", typeof(RectTransform));
            _container = (RectTransform)go.transform;
            _container.SetParent(root.parent, false);
            _container.SetSiblingIndex(root.GetSiblingIndex() + 1);
            _container.anchorMin = Vector2.zero;
            _container.anchorMax = Vector2.one;
            _container.offsetMin = Vector2.zero;
            _container.offsetMax = Vector2.zero;
            _container.localScale = Vector3.one;
            go.AddComponent<LayoutElement>().ignoreLayout = true;

            _screen = new StatsScreen(_container);
            return _screen;
        }

        private static void Forget()
        {
            _screen?.Destroy();
            _screen = null;
            if (_container != null) UnityEngine.Object.Destroy(_container.gameObject);
            _container = null;
            _data = null;
            _hooked = false;
            _resultSeen = false;
            _endTick = -1;
        }

        private static void Pull()
        {
            _pullAccum = 0f;
            var cursor = _data?.Cursor ?? 0;
            if (!RunLua($"__SdbStatsOut = __SdbStatsPull and __SdbStatsPull({cursor}) or ''")) return;
            var raw = GetLuaGlobal("__SdbStatsOut");
            ReportLuaError();
            var session = MatchData.SessionOf(raw);
            if (session == null) return;
            if (_data == null || _data.Session != session)
            {
                // A new VM is a new match. The cursor was for the old one's
                // lines, so read this one's from the start.
                _screen?.Destroy();
                _screen = null;
                _resultSeen = false;
                _endTick = -1;
                _data = new MatchData();
                if (cursor != 0)
                {
                    RunLua("__SdbStatsOut = __SdbStatsPull(0)");
                    raw = GetLuaGlobal("__SdbStatsOut") ?? "";
                }
            }
            _data.Apply(raw);
        }

        private static void ReportLuaError()
        {
            var err = GetLuaGlobal("__SdbStatsErr");
            if (!string.IsNullOrEmpty(err) && err != _lastErr)
            {
                _lastErr = err;
                _log?.LogWarning($"Match stats: Lua reports: {err}");
            }
        }

        // What the result panel is made of, once, for anyone laying anything
        // else out against it.
        private static void DumpResultPanel(SanctuaryPanelUI panel)
        {
            if (_dumped || panel == null) return;
            _dumped = true;
            var sb = new System.Text.StringBuilder("Match stats: result panel tree:\n");
            void Walk(Transform t, int depth)
            {
                var rt = t as RectTransform;
                sb.Append(' ', depth * 2).Append(t.name)
                  .Append(t.gameObject.activeSelf ? "" : " (inactive)")
                  .Append(rt != null ? FormattableString.Invariant($" {rt.rect.width:0}x{rt.rect.height:0}") : "");
                foreach (var c in t.GetComponents<Component>())
                    if (!(c is Transform)) sb.Append(' ').Append(c.GetType().Name);
                sb.Append('\n');
                if (depth < 6) for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
            }
            Walk(panel.transform, 0);
            _log?.LogDebug(sb.ToString());
        }
    }
}
