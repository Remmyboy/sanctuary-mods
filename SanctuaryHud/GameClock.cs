using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The match clock and sim speed, under the menu buttons in the middle of
    // the economy strip. The game shows neither: no timer anywhere, and a speed
    // change only as a passing "Speed changed to 2" line in its log.
    //
    // Time is the client's own Engine.GetSimulationTime. Speed is what the
    // host last announced in that log line (session.lua sends it to everyone
    // when a player changes it), since a client's Engine.GetSimulationSpeed is
    // its own engine's and need not follow the host; before any announcement
    // this match, the engine's figure stands in.
    internal static class GameClock
    {
        internal static ConfigEntry<bool> Enabled;

        private static readonly Regex SpeedLine = new Regex(@"^Speed changed to ([0-9.]+)", RegexOptions.CultureInvariant);

        private static float _poll;
        private static double _simTime = -1;
        private static float _engineSpeed = 1f;
        private static float _announcedSpeed = -1f;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "ShowClock", false,
                "Show the match clock and sim speed in the middle of the economy strip, under the menu buttons " +
                "(where the game's version line is). The game has no clock of its own.");
        }

        /// The caption to show, or null when the clock is off or unknown.
        internal static string Text
        {
            get
            {
                if (Enabled == null || !Enabled.Value || !InMatch || _simTime < 0) return null;
                var t = (int)Math.Floor(_simTime);
                var clock = t >= 3600
                    ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}"
                    : $"{t / 60}:{t % 60:00}";
                if (Paused) return clock + "   PAUSED";
                var speed = _announcedSpeed > 0f ? _announcedSpeed : _engineSpeed;
                return clock + "   " + speed.ToString("0.#", CultureInfo.InvariantCulture) + "×";
            }
        }

        /// From Update: reads the sim clock a few times a second.
        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value) return;
            if (!InMatch)
            {
                // A new match starts at normal speed until told otherwise.
                _simTime = -1;
                _announcedSpeed = -1f;
                return;
            }
            _poll += Time.unscaledDeltaTime;
            if (_poll < 0.25f) return;
            _poll = 0f;
            if (!LuaReady) return;
            if (!RunLua("local ok, t = pcall(Engine.GetSimulationTime) local ok2, s = pcall(Engine.GetSimulationSpeed) " +
                        "__SdbClock = string.format('%.2f|%.3f', ok and t or (_G.Time or 0), ok2 and s or 1)")) return;
            var raw = GetLuaGlobal("__SdbClock");
            if (raw == null) return;
            var parts = raw.Split('|');
            if (parts.Length != 2) return;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                // Time going backwards is a new match (or a replay seek back):
                // the last one's announced speed no longer applies.
                if (t + 1.0 < _simTime) _announcedSpeed = -1f;
                _simTime = t;
            }
            if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0f)
                _engineSpeed = s;
        }

        /// From the log panel hook: every line the game adds to its log.
        internal static void OnLogLine(string line)
        {
            var match = SpeedLine.Match(line);
            if (match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0f)
                _announcedSpeed = speed;
        }
    }
}
