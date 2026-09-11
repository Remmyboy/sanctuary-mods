using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using SanctuaryUI;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Toasts under the economy strip for the things a player must not miss
    // while looking elsewhere: the commander taking damage, the commander
    // getting low, a structure finishing, and a player dropping out of the
    // match. Each comes with a short generated tone (no audio assets to ship)
    // and the commander ones jump the camera to it on click, the same way the
    // widget does.
    internal static class Alerts
    {
        internal enum Kind { CommanderAttacked, CommanderCritical, BuildComplete, PlayerDisconnected }

        private sealed class Toast
        {
            public Kind Kind;
            public string Text;
            public Color Colour;
            public float Shown;
            public float Expires;
            public bool JumpToCommander;
            /// A white card with dark type instead of the strip's dark panel.
            public bool Light;
        }

        private static readonly List<Toast> _toasts = new List<Toast>();

        // ---- config, bound by the plugin ----
        internal static bool AttackedEnabled = true;
        internal static bool CriticalEnabled = true;
        internal static bool BuildCompleteEnabled = true;
        /// Which completions get a toast, keyed "<role>.new" / "<role>.upgrade"
        /// for the roles WorldOverlays assigns (factory, extractor, energy,
        /// intel, defence, tech, strategic, other). Bound by the plugin from
        /// one config switch each, so the Mod Manager shows them as rows.
        internal static readonly Dictionary<string, bool> CompleteRules = new Dictionary<string, bool>();
        /// A tier-4 structure finishing is announced whatever its role.
        internal static bool CompleteAnyTier4 = true;
        internal static bool SoundEnabled = true;

        private static bool WantsCompletion(WorldOverlays.Build b)
        {
            if (CompleteAnyTier4 && b.Tier >= 4) return true;
            var key = (b.Role ?? "other") + (b.IsUpgrade ? ".upgrade" : ".new");
            return CompleteRules.TryGetValue(key, out var on) && on;
        }
        internal static float Volume = 0.5f;
        /// Commander health fraction below which the critical alert fires.
        internal static float CriticalFraction = 0.35f;

        // ---- commander state ----
        private static float _healthAccum;
        private static float _lastHealth = -1f;
        private static int _lastCommander = -1;
        private static float _lastAttackToast = -999f;
        private static bool _criticalArmed = true;

        private const float ToastSeconds = 5f;
        /// Damage keeps the attack toast alive but only re-sounds this often.
        private const float AttackRepeat = 8f;

        private static bool _hooked;

        internal static void Tick()
        {
            if (!_hooked)
            {
                _hooked = true;
                WorldOverlays.OnBuildFinished += b =>
                {
                    if (!BuildCompleteEnabled || !WantsCompletion(b)) return;
                    var what = (b.Name ?? "STRUCTURE").ToUpperInvariant();
                    Push(Kind.BuildComplete, what + (b.IsUpgrade ? " UPGRADED" : " COMPLETE"),
                        new Color(0.42f, 0.85f, 0.5f, 0.95f), false);
                    // An upgrade gets its own line when one is shipped, else
                    // the completion line (and, failing that, the tone).
                    var sound = b.IsUpgrade && Resolve("structure-upgraded") != null
                        ? "structure-upgraded" : "structure-complete";
                    PlayTone(sound, 660f, 0.08f, 880f, 0.10f);
                };
            }

            var now = Time.realtimeSinceStartup;
            if (!InMatch)
            {
                if (_toasts.Count > 0) _toasts.Clear();
                _lastHealth = -1f;
                _lastCommander = -1;
                _criticalArmed = true;
                return;
            }

            _toasts.RemoveAll(t => now >= t.Expires);

            if (_commanderLocalIndex < 0) return;
            if (_commanderLocalIndex != _lastCommander)
            {
                _lastCommander = _commanderLocalIndex;
                _lastHealth = -1f;
                _criticalArmed = true;
            }

            // Four reads a second: the ECS poll only refreshes the health
            // once a second, which would make "under attack" a second late.
            _healthAccum += Time.unscaledDeltaTime;
            if (_healthAccum < 0.25f) return;
            _healthAccum = 0f;
            if (!RefreshCommanderHealth()) return;

            var health = _commanderHealth;
            var max = _commanderMaxHealth;
            if (_lastHealth >= 0f && health < _lastHealth - 0.5f && AttackedEnabled)
            {
                var existing = _toasts.Find(t => t.Kind == Kind.CommanderAttacked);
                if (existing != null)
                {
                    existing.Expires = now + ToastSeconds;
                }
                else
                {
                    Push(Kind.CommanderAttacked, "COMMANDER UNDER ATTACK", DangerColour, true);
                }
                if (now - _lastAttackToast > AttackRepeat)
                {
                    _lastAttackToast = now;
                    PlayTone("commander-under-attack", 520f, 0.12f, 390f, 0.16f);
                }
            }
            _lastHealth = health;

            if (max > 0f && CriticalEnabled)
            {
                var frac = health / max;
                if (_criticalArmed && frac < CriticalFraction && frac > 0f)
                {
                    _criticalArmed = false;
                    Push(Kind.CommanderCritical, "COMMANDER CRITICAL  " + Mathf.RoundToInt(frac * 100f) + "%", DangerColour, true);
                    PlayTone("commander-critical", 330f, 0.18f, 330f, 0.18f, 3);
                }
                // Re-arm once repaired well clear of the line, so a commander
                // hovering around it doesn't alert every few seconds.
                else if (!_criticalArmed && frac > CriticalFraction + 0.15f)
                {
                    _criticalArmed = true;
                }
            }
        }

        private static void Push(Kind kind, string text, Color colour, bool jump,
            bool replace = true, bool light = false, float seconds = ToastSeconds)
        {
            var now = Time.realtimeSinceStartup;
            if (replace) _toasts.RemoveAll(t => t.Kind == kind);
            _toasts.Insert(0, new Toast
            {
                Kind = kind, Text = text, Colour = colour, Shown = now, Expires = now + seconds,
                JumpToCommander = jump, Light = light,
            });
            while (_toasts.Count > 4) _toasts.RemoveAt(_toasts.Count - 1);
        }

        // ---- players dropping out ------------------------------------------

        internal static bool DisconnectEnabled = true;

        // The game reports these in its log panel, top right in small type:
        // "Player <nickname> disconnected!" (host/commands.lua, broadcast to
        // every client; a player quitting reads the same, the game doesn't
        // tell the two apart) and "Connection to the host lost!"
        // (LobbyManager, when this client loses the host). Both go through
        // LogPanelUI.AddLogText, so a postfix there turns them into a toast.
        // The game's own line stays.
        private static readonly Regex DisconnectLine = new Regex(@"^Player (.+) disconnected!$", RegexOptions.CultureInvariant);
        private static readonly Color DisconnectInk = new Color(0.08f, 0.1f, 0.13f, 1f);

        internal static void ApplyLogPatch(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(LogPanelUI), nameof(LogPanelUI.AddLogText), new[] { typeof(string), typeof(float) });
            if (method == null) throw new MissingMethodException("LogPanelUI.AddLogText(string, float) not found");
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Alerts), nameof(LogTextPostfix)));
            _log?.LogInfo("Disconnect toasts: log panel hooked.");
        }

        private static void LogTextPostfix(string message)
        {
            if (!DisconnectEnabled || string.IsNullOrEmpty(message)) return;
            try
            {
                var line = message.Trim();
                string text;
                var match = DisconnectLine.Match(line);
                if (match.Success) text = Clip(match.Groups[1].Value) + " DISCONNECTED";
                else if (line == "Connection to the host lost!") text = "CONNECTION TO THE HOST LOST";
                else return;
                // White, unlike the rest, so it reads as news about the match
                // rather than a warning about your own base; and one player
                // dropping doesn't push another's notice off.
                Push(Kind.PlayerDisconnected, text, DisconnectInk, false, replace: false, light: true, seconds: 8f);
                PlayTone("player-disconnected", 587f, 0.10f, 440f, 0.16f);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Disconnect toast failed: {e.Message}");
            }
        }

        /// A nickname as the toast shows it: on one line, and short enough
        /// not to run off the screen.
        private static string Clip(string name)
        {
            var s = (name ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > 24 ? s.Substring(0, 23) + "…" : s;
        }

        // ---- sound ---------------------------------------------------------

        // The game runs its audio through Wwise, and a Unity AudioSource in
        // this build plays nothing (Unity's own mixer is switched off), so
        // the alerts bypass the engine: each sound is written once, at the
        // configured volume, as a 16-bit WAV in BepInEx's cache folder and
        // handed to the Windows waveform API (winmm PlaySound), which plays
        // it through the system mixer whatever the game is doing. Windows
        // only, which is where the game runs.
        [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(string sound, IntPtr module, uint flags);

        private const uint SndAsync = 0x0001;
        private const uint SndNoDefault = 0x0002;
        private const uint SndFilename = 0x00020000;

        private sealed class Pcm
        {
            public float[] Data;
            public int Channels;
            public int Rate;
        }

        /// Path of the cached file per alert, keyed on the volume it was
        /// rendered at (and the source file's timestamp, so a replaced WAV
        /// is picked up on the next play without a restart).
        private static readonly Dictionary<string, string> _rendered = new Dictionary<string, string>();
        private static bool _audioFailed;

        private static string CacheDir =>
            System.IO.Path.Combine(BepInEx.Paths.CachePath, "SanctuaryHud");

        /// Where shipped sounds live: `SanctuaryMods\SanctuaryHud\sounds\`,
        /// next to the DLL (the loader byte-loads assemblies, so
        /// Assembly.Location is empty and the path is built instead). A
        /// file named after the alert overrides the synthesised tone.
        private static string SoundsDir =>
            System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "SanctuaryMods", "SanctuaryHud", "sounds");

        /// Voice pack: a subfolder of the sounds folder. Set from config; an
        /// empty name, or one with no folder, falls through to files placed
        /// directly in the sounds folder, and then to the tones.
        internal static string VoicePack = "machine";

        /// The file to play for an alert, or null when there is none and the
        /// synthesised tone applies. The pack's folder wins over a loose file.
        private static string Resolve(string name)
        {
            var pack = (VoicePack ?? "").Trim();
            // "tones" is the explicit choice of the built-in sounds: no pack,
            // and no loose file either.
            if (string.Equals(pack, "tones", StringComparison.OrdinalIgnoreCase)) return null;
            if (pack.Length > 0 && pack.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
            {
                var packed = System.IO.Path.Combine(SoundsDir, pack, name + ".wav");
                if (System.IO.File.Exists(packed)) return packed;
            }
            var loose = System.IO.Path.Combine(SoundsDir, name + ".wav");
            return System.IO.File.Exists(loose) ? loose : null;
        }

        /// Names of the packs on disk, for the log and the settings hint.
        internal static string[] AvailablePacks()
        {
            try
            {
                if (!System.IO.Directory.Exists(SoundsDir)) return new string[0];
                var dirs = System.IO.Directory.GetDirectories(SoundsDir);
                var names = new string[dirs.Length];
                for (var i = 0; i < dirs.Length; i++) names[i] = System.IO.Path.GetFileName(dirs[i]);
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return names;
            }
            catch { return new string[0]; }
        }

        /// Plays the alert's shipped WAV if one exists, else two sine notes
        /// back to back `repeats` times. Either way at the config volume.
        private static void PlayTone(string name, float hz1, float s1, float hz2, float s2, int repeats = 1)
        {
            if (!SoundEnabled || Volume <= 0f || _audioFailed) return;
            try
            {
                var volume = Mathf.Clamp01(Volume);
                var source = Resolve(name);
                var stamp = source != null ? System.IO.File.GetLastWriteTimeUtc(source).Ticks : 0L;
                var key = $"{source ?? name}|{volume:0.00}|{stamp}";
                if (!_rendered.TryGetValue(key, out var path) || !System.IO.File.Exists(path))
                {
                    var pcm = (source != null ? LoadWav(source) : null) ?? MakeTone(hz1, s1, hz2, s2, repeats);
                    System.IO.Directory.CreateDirectory(CacheDir);
                    var pack = source != null ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(source)) : "tone";
                    path = System.IO.Path.Combine(CacheDir, $"{pack}-{name}-{Mathf.RoundToInt(volume * 100f)}.wav");
                    WriteWav(path, pcm, volume);
                    _rendered[key] = path;
                }
                if (!PlaySound(path, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault))
                {
                    _log?.LogWarning($"Alert sound: PlaySound refused {path} (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                }
            }
            catch (Exception e)
            {
                _audioFailed = true;
                _log?.LogWarning($"Alert sound unavailable ({e.Message}); alerts stay silent.");
            }
        }

        private static Pcm MakeTone(float hz1, float s1, float hz2, float s2, int repeats)
        {
            const int rate = 44100;
            const float gap = 0.06f;
            var n1 = (int)(s1 * rate);
            var n2 = (int)(s2 * rate);
            var ng = (int)(gap * rate);
            var per = n1 + n2 + ng;
            var data = new float[per * repeats];
            for (var r = 0; r < repeats; r++)
            {
                var o = r * per;
                Note(data, o, n1, hz1, rate);
                Note(data, o + n1, n2, hz2, rate);
            }
            return new Pcm { Data = data, Channels = 1, Rate = rate };
        }

        /// 16-bit PCM WAV of the samples scaled by `gain`.
        private static void WriteWav(string path, Pcm pcm, float gain)
        {
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                var bytes = pcm.Data.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)pcm.Channels);
                w.Write(pcm.Rate); w.Write(pcm.Rate * pcm.Channels * 2); w.Write((short)(pcm.Channels * 2)); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
                foreach (var s in pcm.Data) w.Write((short)(Mathf.Clamp(s * gain, -1f, 1f) * 32767f));
            }
        }

        /// A plain RIFF WAV (8/16/24/32-bit PCM or 32-bit float, any channel
        /// count, any rate) read into samples. Null when there is no such
        /// file; a warning and null when there is one that can't be read,
        /// so a bad export falls back to the tone rather than to silence.
        private static Pcm LoadWav(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            try
            {
                var b = System.IO.File.ReadAllBytes(path);
                if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                    throw new FormatException("not a RIFF WAVE file");
                int format = 0, channels = 0, rate = 0, bits = 0, dataStart = -1, dataLen = 0;
                for (var p = 12; p + 8 <= b.Length;)
                {
                    var id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                    var len = BitConverter.ToInt32(b, p + 4);
                    var body = p + 8;
                    var remaining = b.Length - body;
                    // A chunk has to fit in what's left of the file. A
                    // negative length would walk p backwards (-8 lands it
                    // back where it started, and the loop never ends on the
                    // main thread); an oversized one would read past the end.
                    // Only "data" gets leeway: streaming encoders leave its
                    // length unset, and the rest of the file is the audio.
                    if (id == "data" && (len < 0 || len > remaining)) len = remaining;
                    if (len < 0 || len > remaining) throw new FormatException($"'{id}' chunk length {len} doesn't fit the file");
                    if (id == "fmt ")
                    {
                        if (len < 16) throw new FormatException("fmt chunk too short");
                        format = BitConverter.ToUInt16(b, body);
                        channels = BitConverter.ToUInt16(b, body + 2);
                        rate = BitConverter.ToInt32(b, body + 4);
                        bits = BitConverter.ToUInt16(b, body + 14);
                        // WAVE_FORMAT_EXTENSIBLE carries the real format in its sub-format GUID.
                        if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(b, body + 24);
                    }
                    else if (id == "data")
                    {
                        dataStart = body;
                        dataLen = len;
                        break;
                    }
                    // len is within the file, so this always moves forward
                    // and can't overflow.
                    p = body + len + (len & 1);
                }
                if (dataStart < 0 || channels <= 0 || rate <= 0) throw new FormatException("no fmt/data chunk");

                var bytesPer = bits / 8;
                if (bytesPer <= 0) throw new FormatException($"unsupported WAV sample size of {bits} bits");
                var frames = dataLen / bytesPer;
                var data = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    var o = dataStart + i * bytesPer;
                    switch (format)
                    {
                        case 1 when bits == 8: data[i] = (b[o] - 128) / 128f; break;
                        case 1 when bits == 16: data[i] = BitConverter.ToInt16(b, o) / 32768f; break;
                        case 1 when bits == 24: data[i] = ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f; break;
                        case 1 when bits == 32: data[i] = BitConverter.ToInt32(b, o) / 2147483648f; break;
                        case 3 when bits == 32: data[i] = BitConverter.ToSingle(b, o); break;
                        default: throw new FormatException($"unsupported WAV format {format} at {bits} bits");
                    }
                }
                _log?.LogInfo($"Alert sound: {System.IO.Path.GetFileName(path)} ({rate} Hz, {channels} ch, {bits}-bit, {frames / channels / (float)rate:0.00}s).");
                return new Pcm { Data = data, Channels = channels, Rate = rate };
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Alert sound {path} could not be read ({e.Message}); using the built-in tone.");
                return null;
            }
        }

        /// A sine with a short fade at each end so it doesn't click.
        private static void Note(float[] data, int offset, int count, float hz, int rate)
        {
            var fade = Mathf.Min(count / 4, rate / 100);
            for (var i = 0; i < count; i++)
            {
                var env = 1f;
                if (i < fade) env = i / (float)fade;
                else if (i > count - fade) env = (count - i) / (float)fade;
                data[offset + i] = Mathf.Sin(2f * Mathf.PI * hz * i / rate) * env * 0.6f;
            }
        }

        internal static void Shutdown()
        {
            _toasts.Clear();
            _rendered.Clear();
        }

        // ---- drawing -------------------------------------------------------

        private static GUIStyle _stToast;
        private static bool _stylesReady;

        private static void EnsureToastStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;
            EnsureStyles();
            _stToast = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        }

        internal static void ApplyFont(Font font)
        {
            EnsureToastStyles();
            if (font == null) return;
            _stToast.font = font;
            _stToast.fontSize = 16;
        }

        /// Stacked top-centre just under the strip; newest on top. Fades in
        /// fast and out over the last second. Clicking a commander toast is
        /// the same as clicking the widget.
        internal static void Draw(float logicalWidth, float top, Texture2D panelTexture)
        {
            if (_toasts.Count == 0) return;
            EnsureToastStyles();
            var now = Time.realtimeSinceStartup;
            var y = top;
            foreach (var t in _toasts)
            {
                var age = now - t.Shown;
                var left = t.Expires - now;
                var alpha = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01(left / 1f);
                var width = _stToast.CalcSize(new GUIContent(t.Text)).x + 36f;
                var rect = new Rect(logicalWidth / 2f - width / 2f, y, width, 28f);

                var previous = GUI.color;
                if (t.Light)
                {
                    GUI.color = new Color(0.95f, 0.96f, 0.97f, 0.95f * alpha);
                    GUI.DrawTexture(rect, _texWhite);
                }
                else
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(rect, panelTexture);
                }
                var bar = t.Colour;
                bar.a *= alpha;
                GUI.color = bar;
                GUI.DrawTexture(new Rect(rect.x, rect.y, 4f, rect.height), _texWhite);
                GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), _texWhite);
                GUI.color = previous;

                // Dark type on a light card; on the dark panel, the alert's
                // colour lifted towards white so it reads.
                var textColour = t.Light ? t.Colour : Color.Lerp(t.Colour, Color.white, 0.55f);
                textColour.a = alpha;
                _stToast.normal.textColor = textColour;
                GUI.Label(rect, t.Text, _stToast);

                if (t.JumpToCommander && Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    rect.Contains(Event.current.mousePosition))
                {
                    _pendingCommander = true;
                    _applyOnFrame = -1;
                    Event.current.Use();
                }
                y += rect.height + 6f;
            }
        }
    }
}
