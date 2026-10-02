using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LogKeeper
{
    // Keeps the logs of the last few game sessions in BepInEx\LogArchive, so
    // what happened in a match can still be read after the game is closed or
    // relaunched (tools\gamelog.ps1 -Sessions). The game keeps only its last
    // two Player logs, and BepInEx empties LogOutput.log at every launch.
    //
    //   <session>_bepinex.log  this session's BepInEx log: what LogOutput.log
    //                          held when this loaded, then every line after it
    //                          as it is logged (so it survives a crash)
    //   <session>_player.log   the game's own log (Player.log) of that session,
    //                          saved at the next launch from Player-prev.log
    //
    // <session> is the game's start time, so a hot reload carries on with the
    // same file. Old sessions go first when there are more than Sessions of
    // them or the folder passes MaxSizeMB; one file stops at a quarter of
    // MaxSizeMB, so a session flooding the log can't push every other one out.
    //
    // A personal dev tool for the repo owner's own game, like tools\Probe:
    // never packed or released.
    [BepInPlugin("com.sanctuarydb.logkeeper", "Log Keeper", "1.0.0")]
    public class LogKeeperPlugin : BaseUnityPlugin
    {
        private LogArchive _archive;

        private void Awake()
        {
            try { _archive = new LogArchive(Config, Logger); }
            catch (Exception e) { Logger.LogWarning($"Log archive unavailable: {e.Message}"); }
        }

        // A reload's new copy carries on with the same session file.
        private void OnDestroy() => _archive?.Dispose();
    }

    internal sealed class LogArchive : IDisposable
    {
        private const string Stamp = "yyyy-MM-dd_HH-mm-ss";
        private static readonly Regex SessionFile = new Regex(@"^(\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d)_", RegexOptions.Compiled);

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<int> _cfgSessions;
        private readonly ConfigEntry<int> _cfgMaxSizeMB;
        private readonly string _folder = Path.Combine(Paths.BepInExRootPath, "LogArchive");
        private readonly string _session;
        private Listener _listener;

        internal LogArchive(ConfigFile config, ManualLogSource log)
        {
            _log = log;
            _cfgSessions = config.Bind("Logs", "Sessions", 10,
                new ConfigDescription("How many game sessions' logs to keep.", new AcceptableValueRange<int>(1, 50)));
            _cfgMaxSizeMB = config.Bind("Logs", "MaxSizeMB", 100,
                new ConfigDescription("The most the kept logs may take up, in MB. A single log stops at a quarter of this.",
                    new AcceptableValueRange<int>(10, 1000)));
            _session = SessionStamp();

            _cfgSessions.SettingChanged += (_, __) => Prune();
            _cfgMaxSizeMB.SettingChanged += (_, __) => Prune();
            Apply();
        }

        private long FileCap => Math.Max(1L, _cfgMaxSizeMB.Value) * 1024L * 1024L / 4L;

        private static string SessionStamp()
        {
            try { return Process.GetCurrentProcess().StartTime.ToString(Stamp, CultureInfo.InvariantCulture); }
            catch { return DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture); }
        }

        private void Apply()
        {
            try
            {
                Start();
            }
            catch (Exception e)
            {
                _log.LogWarning($"Log archive: {e.Message}");
            }
        }

        private void Start()
        {
            if (_listener != null) return;
            Directory.CreateDirectory(_folder);
            SavePreviousPlayerLog();

            var path = Path.Combine(_folder, _session + "_bepinex.log");
            var fresh = !File.Exists(path);
            _listener = new Listener(path, FileCap);
            // Listening first, so nothing logged while the file is copied is
            // missed (a line or two may then appear twice).
            BepInEx.Logging.Logger.Listeners.Add(_listener);
            if (fresh)
            {
                // BepInEx buffers LogOutput.log and writes it out now and then:
                // without a flush the file can still be empty at this point.
                foreach (var disk in BepInEx.Logging.Logger.Listeners.OfType<DiskLogListener>().ToList())
                {
                    try { disk.LogWriter?.Flush(); } catch { }
                }
                _listener.Prepend(ReadShared(Path.Combine(Paths.BepInExRootPath, "LogOutput.log")));
            }
            else _listener.Note("Log Keeper reloaded: lines logged while it was reloading are missing here");
            Prune();
            _log.LogInfo($"Log archive: keeping the last {_cfgSessions.Value} session(s), up to {_cfgMaxSizeMB.Value} MB, in {_folder}.");
        }

        private void Stop()
        {
            if (_listener == null) return;
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
            _listener.Dispose();
            _listener = null;
        }

        /// The game moved the last session's Player.log to Player-prev.log at
        /// this launch; it belongs to the session recorded in last-session.txt.
        private void SavePreviousPlayerLog()
        {
            var marker = Path.Combine(_folder, "last-session.txt");
            string previous = null;
            try { if (File.Exists(marker)) previous = File.ReadAllText(marker).Trim(); } catch { }

            if (previous != _session)
            {
                var prevLog = Path.Combine(Path.GetDirectoryName(Application.consoleLogPath) ?? "", "Player-prev.log");
                if (File.Exists(prevLog))
                {
                    var written = File.GetLastWriteTime(prevLog);
                    // Without a record of the last session (the archive was
                    // just switched on), name it by when it was last written.
                    var name = previous != null && SessionFile.IsMatch(previous + "_") ? previous
                        : written.ToString(Stamp, CultureInfo.InvariantCulture);
                    var target = Path.Combine(_folder, name + "_player.log");
                    if (!File.Exists(target)) CopyCapped(prevLog, target);
                }
            }
            File.WriteAllText(marker, _session);
        }

        /// Copies a log, keeping only its end when it is over the cap: the
        /// end is where a match finished, or crashed.
        private void CopyCapped(string from, string to)
        {
            var cap = FileCap;
            using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write))
            {
                if (src.Length > cap)
                {
                    var note = Encoding.UTF8.GetBytes($"[Log archive: the first {src.Length - cap:N0} bytes of this log were left out to keep it under the size limit]{Environment.NewLine}");
                    dst.Write(note, 0, note.Length);
                    src.Seek(-cap, SeekOrigin.End);
                }
                src.CopyTo(dst);
            }
        }

        private static string ReadShared(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                    return reader.ReadToEnd();
            }
            catch { return ""; }
        }

        /// Oldest sessions first, never this one: down to Sessions sessions,
        /// then under MaxSizeMB.
        private void Prune()
        {
            try
            {
                if (!Directory.Exists(_folder)) return;
                var sessions = new DirectoryInfo(_folder).GetFiles()
                    .Select(f => (file: f, match: SessionFile.Match(f.Name)))
                    .Where(x => x.match.Success)
                    .GroupBy(x => x.match.Groups[1].Value, x => x.file)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                var others = sessions.Where(g => g.Key != _session).ToList();
                var keep = Math.Max(1, _cfgSessions.Value);
                var removed = 0;
                while (others.Count > 0 && others.Count + 1 > keep)
                {
                    removed += Delete(others[0]);
                    others.RemoveAt(0);
                }
                var limit = (long)_cfgMaxSizeMB.Value * 1024L * 1024L;
                long Total() => new DirectoryInfo(_folder).GetFiles().Sum(f => f.Length);
                while (others.Count > 0 && Total() > limit)
                {
                    removed += Delete(others[0]);
                    others.RemoveAt(0);
                }
                if (removed > 0) _log.LogInfo($"Log archive: removed {removed} file(s) of older sessions.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Log archive: tidying up failed: {e.Message}");
            }
        }

        private static int Delete(IEnumerable<FileInfo> files)
        {
            var n = 0;
            foreach (var f in files)
            {
                try { f.Delete(); n++; } catch { }
            }
            return n;
        }

        public void Dispose() => Stop();

        /// Writes every BepInEx log line (Debug excluded, as in LogOutput.log)
        /// to the session's file, flushed as it goes, up to the cap.
        private sealed class Listener : ILogListener
        {
            private readonly object _lock = new object();
            private readonly long _cap;
            private StreamWriter _writer;
            private bool _full;

            internal Listener(string path, long cap)
            {
                _cap = cap;
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            }

            internal void Prepend(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                Write(text.EndsWith("\n") ? text.TrimEnd('\r', '\n') : text);
            }

            internal void Note(string text) => Write($"[Log archive: {text}]");

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (eventArgs.Level == LogLevel.Debug) return;
                Write(eventArgs.ToString());
            }

            private void Write(string line)
            {
                lock (_lock)
                {
                    if (_writer == null || _full) return;
                    try
                    {
                        if (_writer.BaseStream.Length >= _cap)
                        {
                            _full = true;
                            _writer.WriteLine("[Log archive: this log reached its size limit; later lines are only in LogOutput.log]");
                            return;
                        }
                        _writer.WriteLine(line);
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }
    }
}
