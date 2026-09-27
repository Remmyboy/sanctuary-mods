using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EM.Network;
using EM.Network.Replay;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Sanctuary.ModApi
{
    // Replays of modded matches. A .sanreplay's header records the game's Lua
    // hash at the moment it was recorded, and the replay list refuses any
    // replay whose hash isn't the current one — which, in the menu, is always
    // vanilla. So every replay of a modded match records its mods in a small
    // sidecar beside it (<replay>.mods.json), and playing one applies them
    // again first. The sidecar follows the replay when it is saved or deleted.
    internal static class Replays
    {
        private const string SidecarSuffix = ".mods.json";

        private sealed class Sidecar
        {
            public string vanillaHash;
            public string luaHash;
            public List<WireMod> mods = new List<WireMod>();
        }

        private static readonly AccessTools.FieldRef<string> RecordingPath =
            AccessTools.StaticFieldRefAccess<string>(AccessTools.Field(typeof(ReplayFile), "filePath"));

        private static bool _listing;

        internal static void Apply(Harmony h)
        {
            Patch(h, typeof(ReplayFile), nameof(ReplayFile.StartRecording), postfix: nameof(RecordingPostfix));
            Patch(h, typeof(ReplayFile), nameof(ReplayFile.DeleteOldestRecordings), postfix: nameof(PruneSidecars));
            Patch(h, typeof(ReplayFile), nameof(ReplayFile.TryReadHeader), postfix: nameof(HeaderPostfix));
            Patch(h, typeof(EM.UI.ReplayListInterface), "Refresh", prefix: nameof(ListingPrefix), finalizer: nameof(ListingFinalizer));
            Patch(h, typeof(EM.UI.ReplayListInterface), "SaveReplay", prefix: nameof(SavePrefix));
            Patch(h, typeof(EM.UI.ReplayListInterface), "DeleteReplay", prefix: nameof(DeletePrefix));
            Patch(h, typeof(NetworkManager), nameof(NetworkManager.StartReplayPlayback), prefix: nameof(PlaybackPrefix));
        }

        private static void Patch(Harmony h, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    ModApiPlugin.Log.LogWarning($"{type.Name}.{method} not found in this game version; modded replays lose that hook.");
                    return;
                }
                h.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Replays), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Replays), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Replays), finalizer));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Replay hook on {type.Name}.{method} failed: {e.Message}");
            }
        }

        private static string SidecarOf(string replayPath) => replayPath + SidecarSuffix;

        private static Sidecar Read(string replayPath)
        {
            var path = SidecarOf(replayPath);
            if (!File.Exists(path)) return null;
            try
            {
                var text = File.ReadAllText(path);
                if (text.Length > 64 * 1024) return null;
                var s = JsonConvert.DeserializeObject<Sidecar>(text);
                if (s?.mods == null) return null;
                return s;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"{Path.GetFileName(path)} unreadable: {e.Message}");
                return null;
            }
        }

        /// The installed, identical copies of the replay's mods in order, or
        /// null (with what's missing) when any isn't here.
        private static List<ModInfo> Resolve(Sidecar s, out string missing)
        {
            var found = new List<ModInfo>();
            var lacking = new List<string>();
            foreach (var w in s.mods)
            {
                var m = ModManifest.IsValidId(w.id) ? ModCatalog.Find(w.id) : null;
                if (m != null && m.ContentHash == w.hash) found.Add(m);
                else lacking.Add($"{w.name} {w.version}".Trim());
            }
            missing = string.Join(", ", lacking);
            return lacking.Count == 0 ? found : null;
        }

        // ---- recording ------------------------------------------------------

        private static void RecordingPostfix()
        {
            try
            {
                var path = RecordingPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || Overlay.IsVanilla) return;
                var s = new Sidecar
                {
                    vanillaHash = Overlay.VanillaHash,
                    luaHash = Overlay.CurrentHash,
                    mods = Overlay.Applied.Select(m => new WireMod
                    {
                        id = m.Id, name = m.Name, version = m.Version, hash = m.ContentHash, url = m.Manifest.Url,
                    }).ToList(),
                };
                File.WriteAllText(SidecarOf(path), JsonConvert.SerializeObject(s, Formatting.Indented));
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Couldn't record this replay's mods: {e.Message}"); }
        }

        // The game keeps the newest 15 recordings and deletes the rest; their
        // sidecars go with them.
        private static void PruneSidecars()
        {
            try
            {
                foreach (var dir in new[] { ReplayFile.ReplayDirectory, ReplayFile.SavedReplayDirectory })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var side in Directory.GetFiles(dir, "*" + ReplayFile.Extension + SidecarSuffix))
                    {
                        var replay = side.Substring(0, side.Length - SidecarSuffix.Length);
                        if (!File.Exists(replay)) File.Delete(side);
                    }
                }
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Replay sidecar cleanup: {e.Message}"); }
        }

        // ---- the replay list ------------------------------------------------

        private static void ListingPrefix() => _listing = true;

        private static Exception ListingFinalizer(Exception __exception)
        {
            _listing = false;
            return __exception;
        }

        // While the list is being filled: a modded replay whose mods are all
        // here reads as the current version, so it can be picked; one whose
        // mods are missing says which, in place of the version the game would
        // show.
        private static void HeaderPostfix(Stream stream, ref ReplayFile.Header header, bool __result)
        {
            if (!_listing || !__result || !(stream is FileStream fs)) return;
            try
            {
                var s = Read(fs.Name);
                if (s == null) return;
                var mods = Resolve(s, out var missing);
                if (mods == null)
                {
                    header.gameVersion = "needs " + missing;
                    return;
                }
                var recorded = Application.version + "#" + s.luaHash;
                if (header.gameVersion == recorded && s.vanillaHash == Overlay.VanillaHash)
                    header.gameVersion = Application.version + "#" + Overlay.CurrentHash;
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Replay list: {e.Message}"); }
        }

        private static void SavePrefix(string path)
        {
            try
            {
                var side = SidecarOf(path);
                if (!File.Exists(side)) return;
                var to = Path.Combine(ReplayFile.SavedReplayDirectory, Path.GetFileName(side));
                if (File.Exists(to)) File.Delete(to);
                File.Move(side, to);
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Couldn't move a replay's mod list: {e.Message}"); }
        }

        private static void DeletePrefix(string path)
        {
            try
            {
                var side = SidecarOf(path);
                if (File.Exists(side)) File.Delete(side);
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Couldn't delete a replay's mod list: {e.Message}"); }
        }

        // ---- playback ---------------------------------------------------------

        // Before the replay's client world (and its Lua VM) is created: the
        // recorded mods go on, or playback is refused rather than desyncing.
        // A replay without a sidecar is a vanilla one.
        private static bool PlaybackPrefix(string filePath, ref string error, ref bool __result)
        {
            try
            {
                var s = Read(filePath);
                if (s == null || s.mods.Count == 0)
                {
                    Overlay.Clear();
                    LoaderBridge.SetActiveGameplayFolders(Array.Empty<string>());
                    return true;
                }
                var mods = Resolve(s, out var missing);
                if (mods == null)
                {
                    error = "This replay needs gameplay mods that aren't installed: " + missing + ".";
                    __result = false;
                    return false;
                }
                Overlay.Apply(mods);
                if (!string.IsNullOrEmpty(s.luaHash) && Overlay.CurrentHash != s.luaHash)
                {
                    Overlay.Clear();
                    error = "This replay's gameplay mods don't produce the recorded game files here.";
                    __result = false;
                    return false;
                }
                LoaderBridge.SetActiveGameplayFolders(mods.Where(m => m.DllsAreGameplay).Select(m => m.Folder).ToArray());
                ModApiPlugin.Log.LogInfo($"Replay plays with gameplay mods: {string.Join(", ", mods)}.");
            }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Replay mods: {e}"); }
            return true;
        }
    }
}
