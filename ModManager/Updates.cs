using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx;
using Newtonsoft.Json.Linq;
using Sanctuary.ModApi;
using UnityEngine;
using UnityEngine.Networking;

namespace SanctuaryHud
{
    // Updates for the mods released from this repo: one look at its GitHub
    // releases tells each mod folder its newest version, and an Update button
    // on the Mods page fetches that release's ModManager zip and writes its
    // SanctuaryMods\<Mod>\ files over the installed ones. The loader notices
    // the new DLL and hot-reloads it, so a UI mod updates in place; a Lua
    // gameplay mod is simply new files for the next lobby.
    //
    // Only first-party mods: a folder is matched to the releases tagged
    // "<folder name>-<version>", which is how every release here is tagged
    // and how every zip lays its folder out.
    //
    // Files are written beside their target and swapped in, never written in
    // place, so the loader never reads half a DLL. Files the new release no
    // longer has are removed only when they are code (.dll, .lua, .santp):
    // anything else in the folder may be the player's (SanctuaryHud's own
    // voice packs live in its sounds\ folder).
    //
    // The Mod Manager's own release also carries the loader and the mod API
    // (BepInEx\plugins), which BepInEx loaded at start-up and which never
    // reload. When those change, the old files are renamed aside (Windows
    // allows renaming a loaded DLL, not overwriting it), the new ones put in
    // their place for the next launch, and the manager's own DLL waits in
    // SanctuaryMods\.updates until then: a new manager against the old API
    // could call methods it doesn't have yet. Awake applies what waits.
    internal sealed class Updates
    {
        private const string Repo = "Remmyboy/sanctuary-mods";
        private const string ReleasesUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";
        /// A check at most this often when the page opens; the button forces one.
        private const float RecheckAfter = 30f * 60f;
        /// Files a release's absence removes from the folder; see above.
        private static readonly string[] CodeExtensions = { ".dll", ".lua", ".santp" };

        internal sealed class Release
        {
            public string Tag;
            public Version Version;
            public string Display; // the version as tagged
            public string ZipUrl;
        }

        private readonly MonoBehaviour _host;
        private readonly BepInEx.Logging.ManualLogSource _log;

        // Newest release per mod folder name, from the last good check.
        private readonly Dictionary<string, Release> _latest = new Dictionary<string, Release>(StringComparer.OrdinalIgnoreCase);
        // What this session installed: the folder's DLL may not have
        // hot-reloaded yet, so its reported version still reads the old one.
        private readonly Dictionary<string, Version> _installed = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        // Per folder: the last failure, said on its button until it is tried again.
        private readonly Dictionary<string, string> _failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _restartPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float _checkedAt = -1f;

        public Updates(MonoBehaviour host, BepInEx.Logging.ManualLogSource log)
        {
            _host = host;
            _log = log;
        }

        /// Bumped whenever anything the page shows from here changes.
        public int Changes { get; private set; }
        public bool Checking { get; private set; }
        /// The folder being downloaded and installed, or null.
        public string Installing { get; private set; }
        /// The last check's outcome, for the check button: null before one.
        public string CheckResult { get; private set; }

        public string FailureOf(string folder) => folder != null && _failed.TryGetValue(folder, out var f) ? f : null;
        public bool RestartPending(string folder) => folder != null && _restartPending.Contains(folder);

        /// The release being installed into the folder, or null.
        public Release InstallingRelease(string folder) =>
            folder != null && string.Equals(Installing, folder, StringComparison.OrdinalIgnoreCase) && _latest.TryGetValue(folder, out var r) ? r : null;

        // Installed since the page last opened: the page says so under the
        // mod until it is next opened, so an update never passes unnoticed.
        private readonly Dictionary<string, Release> _justInstalled = new Dictionary<string, Release>(StringComparer.OrdinalIgnoreCase);
        public Release JustInstalled(string folder) => folder != null && _justInstalled.TryGetValue(folder, out var r) ? r : null;
        public void ForgetJustInstalled() => _justInstalled.Clear();

        /// A newer release than the installed version, or null.
        public Release Available(string folder, string installedVersion)
        {
            if (string.IsNullOrEmpty(folder) || !_latest.TryGetValue(folder, out var r)) return null;
            if (_restartPending.Contains(folder)) return null;
            var have = ParseVersion(installedVersion);
            if (_installed.TryGetValue(folder, out var ours) && (have == null || ours > have)) have = ours;
            return have == null || r.Version > have ? r : null;
        }

        /// The folders with a newer release than what is installed.
        public List<string> AvailableFolders(Func<string, string> installedVersionOf) =>
            _latest.Keys.Where(f => { var v = installedVersionOf(f); return v != null && Available(f, v) != null; })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        public void CheckIfStale()
        {
            if (_checkedAt < 0f || Time.unscaledTime - _checkedAt > RecheckAfter) Check();
        }

        public void Check()
        {
            if (Checking) return;
            _host.StartCoroutine(CheckRoutine());
        }

        private IEnumerator CheckRoutine()
        {
            Checking = true;
            Changes++;
            var started = Time.unscaledTime;
            try
            {
                using (var req = UnityWebRequest.Get(ReleasesUrl))
                {
                    req.SetRequestHeader("Accept", "application/vnd.github+json");
                    req.SetRequestHeader("User-Agent", "SanctuaryModManager");
                    req.timeout = 20;
                    yield return req.SendWebRequest();
                    // A click that comes straight back looks like nothing
                    // happened: "Checking…" stays up for a moment at least.
                    var shown = Time.unscaledTime - started;
                    if (shown < 0.8f) yield return new WaitForSecondsRealtime(0.8f - shown);
                    _checkedAt = Time.unscaledTime;
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        CheckResult = "Check Failed";
                        _log.LogWarning($"Updates: could not read {Repo}'s releases: {req.responseCode} {req.error}");
                        yield break;
                    }
                    try
                    {
                        Read(JArray.Parse(req.downloadHandler.text));
                        CheckResult = "Checked";
                        _log.LogInfo($"Updates: {_latest.Count} mod(s) have releases in {Repo}.");
                    }
                    catch (Exception e)
                    {
                        CheckResult = "Check Failed";
                        _log.LogWarning($"Updates: {Repo}'s release list was unreadable: {e.Message}");
                    }
                }
            }
            finally
            {
                Checking = false;
                Changes++;
            }
        }

        /// Newest first, as GitHub lists them; the first full release seen
        /// per mod with its ModManager zip is that mod's newest.
        private void Read(JArray releases)
        {
            _latest.Clear();
            foreach (var r in releases.OfType<JObject>())
            {
                if ((bool?)r["draft"] == true || (bool?)r["prerelease"] == true) continue;
                var tag = (string)r["tag_name"];
                var dash = tag?.LastIndexOf('-') ?? -1;
                if (dash <= 0) continue;
                var folder = tag.Substring(0, dash);
                var version = ParseVersion(tag.Substring(dash + 1));
                if (version == null || _latest.TryGetValue(folder, out var seen) && seen.Version >= version) continue;
                var zip = (r["assets"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(a => string.Equals((string)a["name"], tag + "-ModManager.zip", StringComparison.OrdinalIgnoreCase));
                var url = (string)zip?["browser_download_url"];
                if (string.IsNullOrEmpty(url)) continue;
                _latest[folder] = new Release { Tag = tag, Version = version, Display = tag.Substring(dash + 1), ZipUrl = url };
            }
        }

        /// "0.9.1" as a version with every part present, so 0.9 == 0.9.0.
        /// Null for anything that isn't one.
        internal static Version ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            if (s.IndexOf('.') < 0) s += ".0";
            if (!Version.TryParse(s, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        // ---- install ---------------------------------------------------------

        /// Downloads the folder's newest release and installs it into
        /// `targetDir` (the mod's folder as it is on disk).
        public void Install(string folder, string targetDir)
        {
            if (Installing != null || !_latest.TryGetValue(folder, out var release)) return;
            _host.StartCoroutine(InstallRoutine(folder, targetDir, release));
        }

        /// Installs each of these folders' newest release, one after another.
        /// The Mod Manager's own goes last: it may hand the rest of its
        /// update to the next launch.
        public void InstallAll(IEnumerable<(string folder, string targetDir)> mods)
        {
            if (Installing != null) return;
            var queue = mods.Where(m => _latest.ContainsKey(m.folder))
                .OrderBy(m => string.Equals(m.folder, "ModManager", StringComparison.OrdinalIgnoreCase)).ToList();
            if (queue.Count > 0) _host.StartCoroutine(InstallAllRoutine(queue));
        }

        private IEnumerator InstallAllRoutine(List<(string folder, string targetDir)> queue)
        {
            foreach (var (folder, targetDir) in queue)
                yield return _host.StartCoroutine(InstallRoutine(folder, targetDir, _latest[folder]));
        }

        private IEnumerator InstallRoutine(string folder, string targetDir, Release release)
        {
            Installing = folder;
            _failed.Remove(folder);
            Changes++;
            try
            {
                byte[] zip;
                using (var req = UnityWebRequest.Get(release.ZipUrl))
                {
                    req.SetRequestHeader("User-Agent", "SanctuaryModManager");
                    req.timeout = 120;
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        _failed[folder] = "download failed";
                        _log.LogWarning($"Updates: downloading {release.Tag} failed: {req.responseCode} {req.error}");
                        yield break;
                    }
                    zip = req.downloadHandler.data;
                }
                try
                {
                    var restart = Apply(folder, targetDir, zip);
                    if (restart) _restartPending.Add(folder);
                    else _installed[folder] = release.Version;
                    if (!restart) _justInstalled[folder] = release;
                    _log.LogInfo($"Updates: installed {release.Tag} into {targetDir}" +
                                 (restart ? "; the loader and mod API changed, so it finishes when the game restarts." : "."));
                    ModCatalog.Rescan();
                }
                catch (Exception e)
                {
                    _failed[folder] = e.Message;
                    _log.LogError($"Updates: installing {release.Tag} failed: {e}");
                }
            }
            finally
            {
                Installing = null;
                Changes++;
            }
        }

        /// Writes the zip's SanctuaryMods\<folder>\ files into targetDir, and
        /// its BepInEx\plugins files beside BepInEx's. True when those
        /// plugins changed, so the rest waits for a restart.
        private bool Apply(string folder, string targetDir, byte[] zipBytes)
        {
            var modPrefix = "SanctuaryMods/" + folder + "/";
            const string pluginPrefix = "BepInEx/plugins/";
            var modFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var pluginFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/")) continue; // a folder
                    Dictionary<string, byte[]> into;
                    string rel;
                    if (name.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase)) { into = modFiles; rel = name.Substring(modPrefix.Length); }
                    else if (name.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase)) { into = pluginFiles; rel = name.Substring(pluginPrefix.Length); }
                    else continue; // README.txt
                    if (!SafeRelative(rel)) throw new InvalidDataException($"the zip holds an unsafe path, {name}");
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        into[rel.Replace('/', Path.DirectorySeparatorChar)] = ms.ToArray();
                    }
                }
            }
            if (modFiles.Count == 0) throw new InvalidDataException($"the zip has no {modPrefix} folder");

            var restart = SwapPlugins(pluginFiles);
            if (restart)
            {
                // Waits for the next launch; see Awake's ApplyPending.
                var staged = Path.Combine(PendingRoot, folder);
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                WriteFiles(staged, modFiles, removeStale: false);
            }
            else WriteFiles(targetDir, modFiles, removeStale: true);
            return restart;
        }

        private static bool SafeRelative(string rel) =>
            rel.Length > 0 && !rel.Split('/').Any(p => p == ".." || p == ".") && !rel.Contains(":") && !Path.IsPathRooted(rel);

        /// SanctuaryMods\.updates: a dot folder, which neither the loader nor
        /// the catalog looks into.
        private static string PendingRoot => Path.Combine(ModCatalog.ModsRoot, ".updates");

        /// The plugins that differ from BepInEx's copies: each old file is
        /// renamed to .old and the new one written in its place. All or
        /// nothing: a failure puts back what was moved. True when any changed.
        private bool SwapPlugins(Dictionary<string, byte[]> files)
        {
            var changed = files.Where(f =>
            {
                var path = Path.Combine(Paths.PluginPath, f.Key);
                return !File.Exists(path) || !SameBytes(File.ReadAllBytes(path), f.Value);
            }).ToList();
            if (changed.Count == 0) return false;

            var done = new List<(string path, bool hadOld)>();
            try
            {
                foreach (var f in changed)
                {
                    var path = Path.Combine(Paths.PluginPath, f.Key);
                    var old = path + ".old";
                    var hadOld = File.Exists(path);
                    if (hadOld)
                    {
                        if (File.Exists(old)) File.Delete(old); // an earlier update's, no longer loaded
                        File.Move(path, old);
                    }
                    done.Add((path, hadOld));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, f.Value);
                }
            }
            catch (Exception e)
            {
                foreach (var (path, hadOld) in done)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        if (hadOld) File.Move(path + ".old", path);
                    }
                    catch (Exception undo) { _log.LogError($"Updates: could not put back {path}: {undo.Message}"); }
                }
                throw new IOException($"could not replace {string.Join(", ", changed.Select(c => c.Key))} in BepInEx\\plugins " +
                                      $"while the game runs ({e.Message}); close the game and extract the release zip by hand", e);
            }
            return true;
        }

        /// Each file written beside its target and swapped in, so a reader
        /// (the loader polling DLLs) sees the old file or the new, never half
        /// of one. Unchanged files are left alone, so their DLLs don't reload.
        private static void WriteFiles(string dir, Dictionary<string, byte[]> files, bool removeStale)
        {
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                var path = Path.Combine(dir, f.Key);
                if (File.Exists(path) && SameBytes(File.ReadAllBytes(path), f.Value)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".updating"; // not *.dll: the loader ignores it
                File.WriteAllBytes(tmp, f.Value);
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch (Exception)
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else File.Move(tmp, path);
            }
            if (!removeStale) return;
            var keep = new HashSet<string>(files.Keys.Select(k => Path.GetFullPath(Path.Combine(dir, k))), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!CodeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (keep.Contains(Path.GetFullPath(path))) continue;
                // Dot folders are nobody's code (the loader skips them).
                var rel = path.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith("."))) continue;
                File.Delete(path);
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// At start-up: puts in the mod files an update left waiting for a
        /// restart (the loader then hot-reloads them), and removes the
        /// plugins it renamed aside, which nothing has loaded any more.
        internal static void ApplyPending(BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                foreach (var old in Directory.GetFiles(Paths.PluginPath, "*.dll.old"))
                {
                    try { File.Delete(old); }
                    catch (Exception e) { log.LogWarning($"Updates: could not remove {old}: {e.Message}"); }
                }
                if (!Directory.Exists(PendingRoot)) return;
                foreach (var staged in Directory.GetDirectories(PendingRoot))
                {
                    var target = Path.Combine(ModCatalog.ModsRoot, Path.GetFileName(staged));
                    var files = Directory.GetFiles(staged, "*", SearchOption.AllDirectories)
                        .ToDictionary(p => p.Substring(staged.Length).TrimStart(Path.DirectorySeparatorChar), File.ReadAllBytes);
                    WriteFiles(target, files, removeStale: true);
                    Directory.Delete(staged, true);
                    log.LogInfo($"Updates: finished the update of {Path.GetFileName(staged)} waiting since the last session.");
                }
                Directory.Delete(PendingRoot, true);
            }
            catch (Exception e)
            {
                log.LogError($"Updates: finishing a waiting update failed: {e}");
            }
        }
    }
}
