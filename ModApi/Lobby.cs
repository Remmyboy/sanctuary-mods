using System;
using System.Collections.Generic;
using System.Linq;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;

namespace Sanctuary.ModApi
{
    /// How one player stands against the host's gameplay selection.
    public enum PlayerModState
    {
        /// Has every selected mod, identical to the host's.
        Ok,
        /// Hasn't answered this revision of the selection yet.
        Pending,
        /// Answered, but is missing a mod or has a different copy.
        Problem,
        /// Never answered at all: no mod support installed. Fine while
        /// nothing is selected.
        Vanilla,
    }

    public sealed class PlayerModStatus
    {
        public ulong PlayerId { get; internal set; }
        public string Name { get; internal set; }
        public PlayerModState State { get; internal set; }
        /// Why, in a few words, when State isn't Ok.
        public string Detail { get; internal set; }
    }

    /// One entry of the host's selection, as every player sees it. Local is
    /// this machine's copy when it is identical to the host's, else null.
    public sealed class SelectedMod
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        public string ContentHash { get; internal set; }
        public string Url { get; internal set; }
        public ModInfo Local { get; internal set; }
        /// The local folder has this id but different contents.
        public ModInfo LocalDifferent { get; internal set; }
        /// The host's option values, key to canonical value. Empty for a mod
        /// without options.
        public IReadOnlyDictionary<string, string> Options { get; internal set; } = new Dictionary<string, string>();
        /// The mod's options as declared in mod.json, for showing the values.
        /// Empty when this machine doesn't have the host's copy.
        public IReadOnlyList<ModOption> OptionDefinitions => Local?.Manifest.Options ?? (IReadOnlyList<ModOption>)Array.Empty<ModOption>();
    }

    /// The lobby side of gameplay mods: the host picks, every player's
    /// client applies the same files, and Start waits until everyone holds
    /// identical copies. Nothing here changes anything for a lobby whose
    /// selection is empty, so vanilla players play as they always did.
    public static class Lobby
    {
        // ---- host state -----------------------------------------------------
        private static readonly List<string> _selection = new List<string>();
        private static int _rev;
        private static bool _locked;
        private static bool _hostSession;
        private static bool _hostSessionIsLadder;
        private static bool _pendingLadderFlag;
        private static readonly Dictionary<ulong, ReportMsg> _reports = new Dictionary<ulong, ReportMsg>();
        private static readonly HashSet<ulong> _hellos = new HashSet<ulong>();
        private static bool _statusDirty;
        private static string _rosterSig = "";
        private static List<ModInfo> _hostResolved = new List<ModInfo>();
        private static string _hostSelectionProblem;
        // The host's option values for each picked mod that has options.
        private static readonly Dictionary<string, Dictionary<string, string>> _options =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // An option changed and hasn't gone out yet: a slider being dragged
        // sends its final value, not every one on the way.
        private static bool _optionsPending;
        private static float _optionsChangedAt;
        private const float OptionsSettle = 0.4f;
        private static readonly Dictionary<ulong, string> _apiVersions = new Dictionary<ulong, string>();
        // The AI seats the host gave a mod's AI, by lobby row. Seats not here
        // play the game's default.
        private static readonly Dictionary<int, (string mod, string key, float at)> _aiPicks =
            new Dictionary<int, (string, string, float)>();
        // The AI seats' rows and army numbers when the picks last went out:
        // the match knows a seat by its army number, so moving one re-applies.
        private static string _aiSeatsSig = "";
        // A pick made on an empty row waits this long for the game to put
        // the AI in it before it's dropped.
        private const float AiAddGrace = 3f;

        // ---- client state ---------------------------------------------------
        private static bool _helloSent;
        private static ModSetMsg _received;
        private static StatusMsg _status;
        private static int _catalogSeen = -1;
        private static bool _abortPending;
        private static string _abortReason;

        private static int _changeCounter;

        /// Bumped on any change a lobby screen should redraw for: the
        /// selection, anyone's status, the catalog.
        public static int ChangeCounter => _changeCounter;

        public static bool InLobby => LobbyManager.IsInLobby;
        public static bool IsHost => LobbyManager.IsCurrentUserHost();

        /// A match is loading or running from this lobby: the selection is
        /// frozen and so is the overlay.
        public static bool MatchUnderway => LobbyManager.IsInLobby && LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby;

        /// The host may change the selection: hosting, in the lobby screen,
        /// before Start. Never in a ladder lobby: ranked matches are vanilla.
        public static bool CanChangeSelection => IsHost && _hostSession && !_hostSessionIsLadder && !_locked && !MatchUnderway;

        /// This is a ladder lobby, where gameplay mods can't be picked.
        public static bool IsLadderLobby => _hostSession ? _hostSessionIsLadder : _received?.ladder == true;

        /// The host has this API: a selection exists and can be shown. False
        /// in a lobby hosted by a vanilla player (no gameplay mods possible).
        public static bool HostHasModSupport => IsHost ? _hostSession : _received != null;

        /// The selection, as sent by the host (or held by it). Empty outside
        /// a lobby.
        // Screens read Selection and the status every frame; both are worked
        // out again only when something they come from has changed.
        private static IReadOnlyList<SelectedMod> _selectionCache;
        private static string _selectionKey;
        private static StatusMsg _statusCache;
        private static IReadOnlyList<PlayerModStatus> _playersCache;
        private static StatusMsg _playersFrom;

        public static IReadOnlyList<SelectedMod> Selection
        {
            get
            {
                if (!InLobby) return Array.Empty<SelectedMod>();
                // Everything the list is made from bumps the change counter
                // (the pick, option values, the host's message) or the
                // catalog's version.
                var key = $"{IsHost}|{_changeCounter}|{ModCatalog.Version}";
                if (_selectionCache != null && key == _selectionKey) return _selectionCache;
                _selectionKey = key;
                return _selectionCache = BuildSelection();
            }
        }

        private static IReadOnlyList<SelectedMod> BuildSelection()
        {
            if (IsHost)
            {
                return _selection.Select(id =>
                {
                    var m = ModCatalog.Find(id);
                    return new SelectedMod
                    {
                        Id = id, Name = m?.Name ?? id, Version = m?.Version ?? "", ContentHash = m?.ContentHash ?? "",
                        Url = m?.Manifest.Url ?? "", Local = m,
                        Options = m == null ? new Dictionary<string, string>() : new Dictionary<string, string>(HostValues(m)),
                    };
                }).ToList();
            }
            if (_received == null) return Array.Empty<SelectedMod>();
            return _received.mods.Select(w =>
            {
                var local = ModCatalog.Find(w.id);
                var same = local != null && local.ContentHash == w.hash;
                return new SelectedMod
                {
                    Id = w.id, Name = w.name, Version = w.version, ContentHash = w.hash, Url = w.url,
                    Local = same ? local : null, LocalDifferent = same ? null : local,
                    Options = same ? OptionValues.Complete(local, w.options)
                        : (IReadOnlyDictionary<string, string>)(w.options ?? new Dictionary<string, string>()),
                };
            }).ToList();
        }

        /// The host's status as it stands: the last one worked out, unless
        /// something has changed since (which always sets _statusDirty).
        private static StatusMsg HostStatus()
        {
            if (_statusDirty || _statusCache == null) _statusCache = BuildStatus();
            return _statusCache;
        }

        private static StatusMsg CurrentStatus() => IsHost ? HostStatus() : _status;

        /// Everyone's state against the selection, as the host last worked
        /// it out.
        public static IReadOnlyList<PlayerModStatus> Players
        {
            get
            {
                var s = CurrentStatus();
                if (s == null) return Array.Empty<PlayerModStatus>();
                if (ReferenceEquals(s, _playersFrom) && _playersCache != null) return _playersCache;
                _playersFrom = s;
                return _playersCache = s.players.Select(p => new PlayerModStatus
                {
                    PlayerId = ulong.TryParse(p.id, out var v) ? v : 0,
                    Name = p.name,
                    State = p.state == "ok" ? PlayerModState.Ok
                          : p.state == "vanilla" ? PlayerModState.Vanilla
                          : p.state == "pending" ? PlayerModState.Pending
                          : PlayerModState.Problem,
                    Detail = p.detail,
                }).ToList();
            }
        }

        /// Null when Start may go ahead as far as mods are concerned,
        /// otherwise why not.
        public static string StartBlockedReason
        {
            get
            {
                if (!InLobby) return null;
                var s = CurrentStatus();
                return s == null || s.ready ? null : s.reason;
            }
        }

        /// Start is held only while a change reaches everyone (an option
        /// settling, players confirming it); nobody is missing anything, and
        /// it clears by itself within a moment.
        public static bool Settling
        {
            get
            {
                if (!InLobby) return false;
                var s = CurrentStatus();
                return s != null && !s.ready && s.settling;
            }
        }

        /// Sets the host's selection, in apply order (a later mod's file
        /// wins). Ignores ids not in the local catalog. False when the
        /// selection can't be changed right now.
        public static bool SetSelection(IEnumerable<string> ids)
        {
            if (!CanChangeSelection) return false;
            var next = ids.Where(id => ModCatalog.Find(id)?.Kind == ModKind.Gameplay).Distinct().ToList();
            if (next.SequenceEqual(_selection)) return true;
            _selection.Clear();
            _selection.AddRange(next);
            HostApply("selection changed");
            return true;
        }

        public static bool SetSelected(string id, bool selected)
        {
            var next = _selection.ToList();
            if (selected && !next.Contains(id)) next.Add(id);
            if (!selected) next.Remove(id);
            return SetSelection(next);
        }

        /// Sets one option of a picked mod, as the host. The value is
        /// normalised the way the option declares (clamped, snapped to its
        /// step, matched to a choice). Changes go out once the host stops
        /// changing things for a moment, and are remembered for the next
        /// lobby this player hosts. False when the selection can't be changed
        /// right now, or the mod isn't picked or has no such option.
        public static bool SetOption(string modId, string key, string value)
        {
            if (!CanChangeSelection || !_selection.Contains(modId)) return false;
            var mod = ModCatalog.Find(modId);
            var opt = mod?.Manifest.Options.FirstOrDefault(o => o.Key == key);
            if (opt == null) return false;
            var values = HostValues(mod);
            var next = opt.Normalize(value);
            if (values[key] == next) return true;
            values[key] = next;
            if (!_optionsPending) _statusDirty = true; // Start waits for it
            _optionsPending = true;
            _optionsChangedAt = UnityEngine.Time.unscaledTime;
            _changeCounter++;
            return true;
        }

        /// Puts every option of a picked mod back to its default.
        public static bool ResetOptions(string modId)
        {
            if (!CanChangeSelection || !_selection.Contains(modId)) return false;
            var mod = ModCatalog.Find(modId);
            if (mod == null || mod.Manifest.Options.Count == 0) return false;
            var defaults = OptionValues.Defaults(mod);
            if (OptionValues.Signature(HostValues(mod)) == OptionValues.Signature(defaults)) return true;
            _options[modId] = defaults;
            if (!_optionsPending) _statusDirty = true; // Start waits for it
            _optionsPending = true;
            _optionsChangedAt = UnityEngine.Time.unscaledTime;
            _changeCounter++;
            return true;
        }

        /// The AI a lobby row's seat plays: a mod's AI as (mod id, AI key,
        /// what the dropdown shows), or null for the game's default. On the
        /// host, its own pick; elsewhere, the host's as last received.
        public static (string modId, string key, string label)? SeatAi(int slot)
        {
            if (!InLobby) return null;
            if (IsHost && _hostSession)
            {
                if (!_aiPicks.TryGetValue(slot, out var p)) return null;
                return (p.mod, p.key, Ais.Find(p.mod, p.key)?.Label ?? "AI: " + p.key);
            }
            var seat = _received?.ais?.FirstOrDefault(s => s != null && s.slot == slot);
            return seat == null ? ((string, string, string)?)null : (seat.mod, seat.key, "AI: " + Cap(seat.name, 40));
        }

        /// As the host, gives a lobby row's AI seat a mod's AI (null modId:
        /// the game's default). Picks the mod too, if it isn't yet. The row
        /// may still be empty, about to become an AI seat. False when the
        /// selection can't be changed now, or there's no such AI.
        public static bool SetSeatAi(int slot, string modId, string key)
        {
            if (!CanChangeSelection || slot < 0 || slot > 255) return false;
            if (modId == null)
            {
                if (!_aiPicks.Remove(slot)) return true;
            }
            else
            {
                var ai = Ais.Find(modId, key);
                if (ai == null) return false;
                if (_aiPicks.TryGetValue(slot, out var was) && was.mod == modId && was.key == key) return true;
                if (!_selection.Contains(modId) && !SetSelected(modId, true)) return false;
                _aiPicks[slot] = (modId, key, UnityEngine.Time.unscaledTime);
            }
            if (!_optionsPending) _statusDirty = true; // Start waits for it
            _optionsPending = true;
            _optionsChangedAt = UnityEngine.Time.unscaledTime;
            _changeCounter++;
            AiLobby.Redraw();
            return true;
        }

        /// The host's AI seats as the match will know them: each picked
        /// row that holds an AI, with its army number.
        private static List<AiSeat> HostAiSeats()
        {
            var seats = new List<AiSeat>();
            var players = LobbyManager.hostState?.players;
            if (players == null) return seats;
            foreach (var kv in _aiPicks.OrderBy(kv => kv.Key))
            {
                if (kv.Key >= players.Count || players[kv.Key].type != PlayerType.AI) continue;
                seats.Add(new AiSeat
                {
                    slot = kv.Key, army = players[kv.Key].armyID, mod = kv.Value.mod, key = kv.Value.key,
                    name = Ais.Find(kv.Value.mod, kv.Value.key)?.Ai.Name ?? kv.Value.key,
                });
            }
            return seats;
        }

        /// Picks for rows that no longer hold an AI go. A row that is still
        /// empty keeps its pick for a moment: the host picked on an empty
        /// row, and the AI is on its way.
        private static bool PruneAiPicks()
        {
            var players = LobbyManager.hostState?.players;
            if (players == null || _aiPicks.Count == 0) return false;
            var now = UnityEngine.Time.unscaledTime;
            var gone = _aiPicks.Where(kv => kv.Key >= players.Count ||
                                            players[kv.Key].type == PlayerType.Player || players[kv.Key].type == PlayerType.Observer ||
                                            players[kv.Key].type == PlayerType.Empty && now - kv.Value.at > AiAddGrace ||
                                            !_selection.Contains(kv.Value.mod))
                .Select(kv => kv.Key).ToList();
            foreach (var slot in gone) _aiPicks.Remove(slot);
            return gone.Count > 0;
        }

        private static string AiSeatsSignature()
        {
            var players = LobbyManager.hostState?.players;
            if (players == null || _aiPicks.Count == 0) return "";
            return string.Join(",", _aiPicks.Keys.OrderBy(k => k)
                .Select(k => k < players.Count ? $"{k}:{(int)players[k].type}:{players[k].armyID}" : $"{k}:-"));
        }

        /// The host's values for a mod it has (picked or not): this lobby's,
        /// else the ones it used last time, else the defaults.
        private static Dictionary<string, string> HostValues(ModInfo mod)
        {
            if (!_options.TryGetValue(mod.Id, out var values))
                _options[mod.Id] = values = OptionValues.Complete(mod, ModApiPlugin.RememberedOptions(mod.Id));
            else if (values.Count != mod.Manifest.Options.Count || mod.Manifest.Options.Any(o => !values.ContainsKey(o.Key) || o.Normalize(values[o.Key]) != values[o.Key]))
                _options[mod.Id] = values = OptionValues.Complete(mod, values); // the mod's options were edited
            return values;
        }

        // ---- lifecycle, driven by ModApiPlugin and the patches ----------------

        internal static void OnCreateLobby(string lobbyName)
        {
            // A lobby is made with the vanilla hash (the game records it for
            // the join check then); the selection goes on once we're in.
            EndSession();
            Overlay.Clear();
            _pendingLadderFlag = lobbyName != null && lobbyName.StartsWith("Ladder:", StringComparison.OrdinalIgnoreCase);
        }

        internal static void OnJoinLobby()
        {
            EndSession();
            Overlay.Clear();
        }

        internal static void OnLeftLobbyOrMatch()
        {
            if (LobbyManager.IsInLobby || NetworkManager.IsReplayPlayback) return;
            var was = _hostSession || _received != null || !Overlay.IsVanilla;
            EndSession();
            Overlay.Clear();
            LoaderBridge.SetActiveGameplayFolders(Array.Empty<string>());
            if (was) ModEvents.RaiseLobbyLeft();
        }

        private static void EndSession()
        {
            _selection.Clear();
            _hostResolved = new List<ModInfo>();
            _hostSelectionProblem = null;
            _options.Clear();
            _optionsPending = false;
            _aiPicks.Clear();
            _aiSeatsSig = "";
            _apiVersions.Clear();
            _reports.Clear();
            _hellos.Clear();
            _locked = false;
            _hostSession = false;
            _hostSessionIsLadder = false;
            _helloSent = false;
            _received = null;
            _status = null;
            _rosterSig = "";
            _abortPending = false;
            _startQueuedAt = -1f;
            _startButtonTouched = false;
            _joinedAt.Clear();
            _graceCheckAt = -1f;
            _statusCache = null;
            _selectionCache = null;
            _playersCache = null;
            _playersFrom = null;
            _changeCounter++;
        }

        /// Every frame from the plugin.
        internal static void Tick()
        {
            if (_abortPending)
            {
                _abortPending = false;
                ModApiPlugin.Log.LogError($"Leaving the match: {_abortReason}");
                try { EM.DOTS.Engine.Loader.EngineLoader.Instance?.QuitGame(); }
                catch (Exception e) { ModApiPlugin.Log.LogError($"Couldn't leave the match: {e.Message}"); }
                return;
            }

            if (!LobbyManager.IsInLobby)
            {
                // A cleanup the game deferred (a disconnect handled inside
                // the network update) can leave the overlay on after the
                // hooks ran: outside a lobby or replay, the game is vanilla.
                if (!Overlay.IsVanilla && !NetworkManager.IsReplayPlayback) OnLeftLobbyOrMatch();
                return;
            }

            // The host is in its own lobby once its own join has gone
            // through (clientState arrives with the first FullState). Only
            // then does the selection go on: the join check has already
            // recorded the vanilla hash.
            if (LobbyManager.isHostRunning && !_hostSession && LobbyManager.clientState != null && !MatchUnderway)
            {
                _hostSession = true;
                _hostSessionIsLadder = _pendingLadderFlag;
                _pendingLadderFlag = false;
                _rev = 0;
                // Every lobby starts vanilla; the host switches mods on.
                HostApply("hosting");
                ModEvents.RaiseLobbyEntered(true);
            }

            // Mods edited or added while in the lobby.
            if (_catalogSeen != ModCatalog.Version)
            {
                _catalogSeen = ModCatalog.Version;
                if (_hostSession && !_locked && !MatchUnderway) HostApply("mod files changed");
                else if (!LobbyManager.isHostRunning && _received != null && !MatchUnderway && !_received.locked) ClientEvaluate(sendReport: true);
                _changeCounter++;
            }

            // An AI seat with a mod's AI moved to another army number, was
            // removed, or became a player's: that goes out like an option.
            if (_hostSession && !_locked && !MatchUnderway && _aiPicks.Count > 0)
            {
                var pruned = PruneAiPicks();
                if (pruned || AiSeatsSignature() != _aiSeatsSig)
                {
                    if (!_optionsPending) _statusDirty = true;
                    _optionsPending = true;
                    _optionsChangedAt = UnityEngine.Time.unscaledTime;
                    _aiSeatsSig = AiSeatsSignature();
                    _changeCounter++;
                    if (pruned) AiLobby.Redraw();
                }
            }

            if (_optionsPending && _hostSession && !_locked && !MatchUnderway &&
                UnityEngine.Time.unscaledTime - _optionsChangedAt >= OptionsSettle)
            {
                HostApply("options or AI seats changed");
            }

            if (_hostSession)
            {
                var sig = RosterSignature();
                if (sig != _rosterSig)
                {
                    _rosterSig = sig;
                    // Reports from players who left go with them.
                    var present = new HashSet<ulong>(Humans().Select(p => p.id.value));
                    foreach (var gone in _reports.Keys.Where(k => !present.Contains(k)).ToList()) _reports.Remove(gone);
                    _hellos.RemoveWhere(h => !present.Contains(h));
                    foreach (var gone in _joinedAt.Keys.Where(k => !present.Contains(k)).ToList()) _joinedAt.Remove(gone);
                    var now = UnityEngine.Time.unscaledTime;
                    foreach (var id in present)
                    {
                        if (_joinedAt.ContainsKey(id)) continue;
                        _joinedAt[id] = now;
                        if (_graceCheckAt < 0f || now + HelloGrace < _graceCheckAt) _graceCheckAt = now + HelloGrace;
                    }
                    _statusDirty = true;
                }
                // A joiner's time to say hello ran out: they now read as
                // having no mod support, if they still haven't.
                if (_graceCheckAt >= 0f && UnityEngine.Time.unscaledTime >= _graceCheckAt)
                {
                    var now = UnityEngine.Time.unscaledTime;
                    var later = _joinedAt.Values.Select(t => t + HelloGrace).Where(t => t > now).ToList();
                    _graceCheckAt = later.Count > 0 ? later.Min() : -1f;
                    _statusDirty = true;
                }
                if (_statusDirty)
                {
                    _statusDirty = false;
                    var status = _statusCache = BuildStatus();
                    LobbyProtocol.Broadcast(LobbyProtocol.Status, status);
                    RefreshStartButton();
                    _changeCounter++;
                }
                TickQueuedStart();
            }
        }

        // ---- host --------------------------------------------------------------

        private static void HostApply(string why)
        {
            _hostResolved = _selection.Select(ModCatalog.Find).Where(m => m != null && m.Kind == ModKind.Gameplay).ToList();
            _selection.RemoveAll(id => _hostResolved.All(m => m.Id != id));

            var missingReqs = _hostResolved
                .SelectMany(m => m.Manifest.Requires.Where(r => !_selection.Contains(r)).Select(r => (m, r)))
                .ToList();
            _hostSelectionProblem = missingReqs.Count == 0
                ? null
                : string.Join("; ", missingReqs.Select(x => $"{x.m.Name} needs {ModCatalog.Find(x.r)?.Name ?? x.r} picked too"));

            _optionsPending = false;
            var options = HostOptions();
            var hadPicks = _aiPicks.Count;
            PruneAiPicks();
            _aiSeatsSig = AiSeatsSignature();
            Overlay.Apply(_hostResolved, options, HostAiSeats());
            if (_aiPicks.Count != hadPicks) AiLobby.Redraw();
            // A picked mod the host couldn't put on whole: the match would run
            // without it here and with it everywhere else.
            if (Overlay.Failed.Count > 0)
            {
                var failed = $"{Names(Overlay.Failed)} couldn't be applied on the host (see its log)";
                _hostSelectionProblem = _hostSelectionProblem == null ? failed : _hostSelectionProblem + "; " + failed;
            }
            // Next time this player hosts, the mods start as they were left.
            foreach (var kv in options) ModApiPlugin.RememberOptions(kv.Key, kv.Value);
            LoaderBridge.SetActiveGameplayFolders(_hostResolved.Where(m => m.DllsAreGameplay).Select(m => m.Folder).ToArray());
            _rev++;
            ModApiPlugin.Log.LogInfo($"Lobby gameplay mods ({why}), revision {_rev}: " +
                                     (_hostResolved.Count == 0 ? "none (vanilla)" : string.Join(", ", _hostResolved)));
            LobbyProtocol.Broadcast(LobbyProtocol.ModSet, HostModSet());
            _statusDirty = true;
            _changeCounter++;
            ModEvents.RaiseSelectionChanged();
            // The advert carries a "[mods]" marker while anything is picked.
            try { LobbyManager.RefreshServerAdvertisement(); } catch { }
        }

        private static ModSetMsg HostModSet() => new ModSetMsg
        {
            rev = _rev,
            locked = _locked,
            ladder = _hostSessionIsLadder,
            luaHash = Overlay.CurrentHash,
            mods = _hostResolved.Select(m => new WireMod
            {
                id = m.Id, name = Cap(m.Name, 80), version = Cap(m.Version, 40), hash = m.ContentHash, url = Cap(m.Manifest.Url, 300),
                options = m.Manifest.Options.Count == 0 ? null : new Dictionary<string, string>(HostValues(m)),
            }).ToList(),
            // What the overlay went on with, so every machine writes the
            // same seats file.
            ais = Overlay.AppliedAis.Count == 0 ? null : Overlay.AppliedAis.ToList(),
        };

        /// The picked mods' option values, as the overlay takes them.
        private static Dictionary<string, Dictionary<string, string>> HostOptions() =>
            _hostResolved.Where(m => m.Manifest.Options.Count > 0).ToDictionary(m => m.Id, HostValues, StringComparer.Ordinal);

        internal static bool HostSelectionActive => _hostSession && _hostResolved.Count > 0;

        internal static void HostReceived(PlayerID sender, byte type, Unity.Collections.NativeArray<byte> payload)
        {
            if (!_hostSession) return;
            if (!Humans().Any(p => p.id == sender)) return;
            if (type == LobbyProtocol.Hello)
            {
                if (!LobbyProtocol.TryRead<HelloMsg>(payload, out var hello)) return;
                _hellos.Add(sender.value);
                _apiVersions[sender.value] = Cap(hello.version, 20);
                LobbyProtocol.SendToPlayer(sender, LobbyProtocol.ModSet, HostModSet());
                _statusDirty = true;
            }
            else if (type == LobbyProtocol.Report)
            {
                if (!LobbyProtocol.TryRead<ReportMsg>(payload, out var report)) return;
                if (report.mods == null) report.mods = new List<ReportedMod>();
                if (report.mods.Count > 256) return;
                _hellos.Add(sender.value);
                _reports[sender.value] = report;
                _statusDirty = true;
            }
        }

        private static IEnumerable<LobbyPlayer> Humans()
        {
            var state = LobbyManager.hostState;
            if (state == null) return Enumerable.Empty<LobbyPlayer>();
            return state.players.Where(p => p.type == PlayerType.Player || p.type == PlayerType.Observer);
        }

        // Names and seat types too: the status shows both, and is only
        // worked out again when something in it has changed.
        private static string RosterSignature() =>
            string.Join(",", Humans().Select(p => p.id.value + ":" + (int)p.type + ":" + p.name));

        // A player who has just joined gets this long to say hello (their
        // client does on its first lobby state) before they count as having
        // no mod support. Until then they are only "checking", so a Start
        // pressed that moment waits for them rather than failing.
        private const float HelloGrace = 3f;
        private static readonly Dictionary<ulong, float> _joinedAt = new Dictionary<ulong, float>();
        private static float _graceCheckAt = -1f;

        private static bool JustJoined(ulong id) =>
            _joinedAt.TryGetValue(id, out var t) && UnityEngine.Time.unscaledTime - t < HelloGrace;

        private static StatusMsg BuildStatus()
        {
            var s = new StatusMsg { rev = _rev };
            var reasons = new List<string>();
            if (_hostSelectionProblem != null) reasons.Add(_hostSelectionProblem);
            if (_optionsPending) reasons.Add("options or AI seats changing");
            // A reason that won't clear by itself: someone is missing
            // something, or the pick itself is incomplete. The rest is a
            // change still on its way to everyone.
            var hard = _hostSelectionProblem != null;
            var selectionActive = _hostResolved.Count > 0;
            foreach (var p in Humans())
            {
                var row = new PlayerStatusMsg { id = p.id.value.ToString(), name = Cap(p.name, 64) };
                _reports.TryGetValue(p.id.value, out var report);
                if (!_hellos.Contains(p.id.value) && report == null && selectionActive && JustJoined(p.id.value))
                {
                    row.state = "pending";
                    row.detail = "checking…";
                    reasons.Add($"{row.name}: still checking mods");
                }
                else if (!_hellos.Contains(p.id.value) && report == null)
                {
                    row.state = "vanilla";
                    row.detail = "no mod support installed";
                    if (selectionActive) { reasons.Add($"{row.name} has no mod support (needs {Names(_hostResolved)})"); hard = true; }
                }
                else if (report == null || report.rev != _rev)
                {
                    row.state = "pending";
                    row.detail = "checking…";
                    if (selectionActive) reasons.Add($"{row.name}: still checking mods");
                }
                else
                {
                    var problems = new List<string>();
                    foreach (var m in _hostResolved)
                    {
                        var r = report.mods.FirstOrDefault(x => x.id == m.Id);
                        if (r == null || r.state == "missing") problems.Add($"missing {m.Name} {m.Version}".TrimEnd());
                        else if (r.state == "failed") problems.Add($"couldn't apply {m.Name} (see their log)");
                        else if (r.state != "ok" || r.hash != m.ContentHash)
                            problems.Add(string.IsNullOrEmpty(r.version) || r.version == m.Version
                                ? $"has a different copy of {m.Name}"
                                : $"has {m.Name} {r.version} (host {m.Version})");
                    }
                    if (problems.Count == 0 && selectionActive && report.luaHash != Overlay.CurrentHash)
                        problems.Add("Lua files differ from the host's");
                    // The framework's own Lua (match events, options files)
                    // comes with the API, so two API versions give different
                    // Lua for the same mods. Say so rather than leave them
                    // hunting for a difference in the mod.
                    if (problems.Count > 0 && _apiVersions.TryGetValue(p.id.value, out var api) && api != ModApiPlugin.Version)
                        problems.Add($"their Mod API is {api}, the host's {ModApiPlugin.Version}: both need the same Mod Manager release");
                    if (problems.Count == 0)
                    {
                        row.state = "ok";
                        row.detail = selectionActive ? "ready" : "";
                    }
                    else
                    {
                        row.state = "problem";
                        row.detail = Cap(string.Join(", ", problems), 300);
                        reasons.Add($"{row.name} {row.detail}");
                        hard = true;
                    }
                }
                s.players.Add(row);
            }
            s.ready = reasons.Count == 0;
            s.settling = !s.ready && !hard;
            s.reason = s.ready ? null : Cap("Gameplay mods: " + string.Join("; ", reasons), 600);
            return s;
        }

        private static string Names(IEnumerable<ModInfo> mods) => string.Join(", ", mods.Select(m => m.Name));

        /// For the patches: may the host start the match now?
        internal static bool GateOpen(out string reason, bool fresh = false)
        {
            reason = null;
            if (!_hostSession) return true;
            var s = fresh ? BuildStatus() : HostStatus();
            reason = s.reason;
            return s.ready;
        }

        /// The authoritative moment: the host's StartGame request is being
        /// handled. Re-checks, then freezes the selection for the match.
        internal static bool HostStartGame(out string reason)
        {
            if (!_hostSession) { reason = null; return true; }
            if (_optionsPending)
            {
                // Everyone has to confirm the new values first.
                HostApply("options or AI seats changed");
                reason = "Gameplay mod options or AI seats changed a moment ago; press Start again once everyone has them.";
                return false;
            }
            if (!GateOpen(out reason, fresh: true)) return false;
            var applied = Overlay.Applied;
            PruneAiPicks();
            if (applied.Count != _hostResolved.Count || applied.Where((m, i) => !ReferenceEquals(m, _hostResolved[i])).Any() ||
                _hostResolved.Any(m => m.Manifest.Options.Count > 0 &&
                                       OptionValues.Signature(Overlay.OptionsOf(m.Id)) != OptionValues.Signature(HostValues(m))) ||
                Ais.Signature(Overlay.AppliedAis) != Ais.Signature(HostAiSeats()))
            {
                reason = "Gameplay mods changed on disk a moment ago; press Start again.";
                HostApply("re-applied at start");
                return false;
            }
            _locked = true;
            LobbyProtocol.Broadcast(LobbyProtocol.ModSet, HostModSet());
            ModApiPlugin.Log.LogInfo($"Match starting with gameplay mods: {(applied.Count == 0 ? "none (vanilla)" : string.Join(", ", applied))}.");
            return true;
        }

        // Start pressed while a change was settling: the press goes through
        // once everyone has confirmed it.
        private static float _startQueuedAt = -1f;
        private const float StartQueueTimeout = 5f;

        /// The host pressed Start while the gate was only settling. True when
        /// the press was queued (and a line said so).
        internal static bool QueueStart()
        {
            if (!IsHost || !Settling) return false;
            if (_startQueuedAt < 0f)
            {
                try { EM.UI.LobbyInterface.Instance?.AddChatMessage("Gameplay mods: starting as soon as everyone has the latest change…"); }
                catch { }
            }
            _startQueuedAt = UnityEngine.Time.unscaledTime;
            return true;
        }

        private static readonly System.Reflection.MethodInfo StartPressedMi =
            HarmonyLib.AccessTools.Method(typeof(EM.UI.InterfaceManager), "OnLobbyStartGamePressed");

        /// Every frame: a queued Start goes through once the gate opens, and
        /// gives up with the reason if a real problem appears or it takes
        /// too long.
        private static void TickQueuedStart()
        {
            if (_startQueuedAt < 0f) return;
            if (!IsHost || !_hostSession || MatchUnderway) { _startQueuedAt = -1f; return; }
            if (_optionsPending || _statusDirty) return; // let the change go out first
            if (GateOpen(out var reason))
            {
                _startQueuedAt = -1f;
                try { StartPressedMi?.Invoke(EM.UI.InterfaceManager.Instance, null); }
                catch (Exception e) { ModApiPlugin.Log.LogError($"Queued Start failed: {e}"); }
                return;
            }
            if (!Settling || UnityEngine.Time.unscaledTime - _startQueuedAt > StartQueueTimeout)
            {
                _startQueuedAt = -1f;
                GamePatches.ShowError(reason ?? "Gameplay mods: players didn't confirm the change in time; press Start again.");
            }
        }

        // The Start button has been set by us rather than the game.
        private static bool _startButtonTouched;

        private static readonly HarmonyLib.AccessTools.FieldRef<EM.UI.LobbyInterface, Michsky.UI.Beam.ButtonManager> StartButtonRef =
            HarmonyLib.AccessTools.FieldRefAccess<EM.UI.LobbyInterface, Michsky.UI.Beam.ButtonManager>("hostStartButton");

        /// Sets the host's Start button the way the game's own UpdateData
        /// does (every seat filled is ready), and with the mods gate on top.
        /// Doesn't call UpdateData itself: that redraws every seat and would
        /// close a dropdown the host has open.
        internal static void RefreshStartButton()
        {
            try
            {
                var ui = EM.UI.LobbyInterface.Instance;
                var state = LobbyManager.CurrentState;
                if (ui == null || state == null || !LobbyManager.IsCurrentUserHost()) return;
                var button = StartButtonRef(ui);
                if (button == null || !button.gameObject.activeInHierarchy) return;
                var allReady = state.players.Take(state.maxPlayers).All(p => p.isReady || p.type == PlayerType.Empty);
                // No gameplay mods in play: the button is the game's alone.
                // Put back what we changed, once, if mods were just
                // switched off.
                if (_hostResolved.Count == 0 && _hostSelectionProblem == null)
                {
                    if (_startButtonTouched) button.Interactable(allReady);
                    _startButtonTouched = false;
                    return;
                }
                _startButtonTouched = true;
                // While a change is only settling the button stays live: a
                // press then waits for it (QueueStart) rather than the
                // button flickering grey for half a second.
                button.Interactable(allReady && (GateOpen(out _) || Settling));
            }
            catch { }
        }

        // ---- client ------------------------------------------------------------

        /// The first FullState after joining: tell the host we can take mods.
        /// Also the host's own client, over its loopback connection.
        internal static void ClientSawFullState()
        {
            if (_helloSent || !LobbyManager.IsInLobby) return;
            _helloSent = true;
            LobbyProtocol.SendToHost(LobbyProtocol.Hello, new HelloMsg());
        }

        internal static void ClientReceived(byte type, Unity.Collections.NativeArray<byte> payload)
        {
            if (type == LobbyProtocol.ModSet)
            {
                if (!LobbyProtocol.TryRead<ModSetMsg>(payload, out var set)) return;
                if (set.mods == null) set.mods = new List<WireMod>();
                if (set.mods.Count > 64) return;
                if (set.ais != null)
                {
                    if (set.ais.Count > 32) return;
                    set.ais = set.ais.Where(s => s != null && s.slot >= 0 && s.slot < 256 && s.army > 0 && s.army < 128 &&
                                                 ModManifest.IsValidId(s.mod) && s.key != null && s.key.Length <= 32).ToList();
                    foreach (var s in set.ais) s.name = Cap(s.name, 40);
                }
                // Once the match is loading, the files the VMs read must not move.
                if (MatchUnderway && !LobbyManager.isHostRunning)
                {
                    if (_received != null) return;
                }
                var seatsWere = Ais.Signature(_received?.ais);
                _received = set;
                ClientEvaluate(sendReport: true);
                _changeCounter++;
                if (!LobbyManager.isHostRunning && Ais.Signature(set.ais) != seatsWere) AiLobby.Redraw();
                if (!LobbyManager.isHostRunning) ModEvents.RaiseSelectionChanged();
            }
            else if (type == LobbyProtocol.Status)
            {
                if (!LobbyProtocol.TryRead<StatusMsg>(payload, out var status)) return;
                if (status.players == null) status.players = new List<PlayerStatusMsg>();
                _status = status;
                _changeCounter++;
            }
        }

        /// Matches the host's selection against this machine's mods, applies
        /// them when every one is here and identical, and reports back.
        private static void ClientEvaluate(bool sendReport)
        {
            if (_received == null) return;
            var report = new ReportMsg { rev = _received.rev };
            var resolved = new List<ModInfo>();
            var options = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var w in _received.mods)
            {
                var local = ModApiPlugin.ValidId(w.id) ? ModCatalog.Find(w.id) : null;
                if (local == null || local.Kind != ModKind.Gameplay)
                {
                    report.mods.Add(new ReportedMod { id = w.id, state = "missing" });
                    continue;
                }
                var same = local.ContentHash == w.hash;
                report.mods.Add(new ReportedMod { id = w.id, state = same ? "ok" : "different", version = local.Version, hash = local.ContentHash });
                if (!same) continue;
                resolved.Add(local);
                // Checked against this machine's copy, which is the host's:
                // only declared keys, only values the option can take.
                if (local.Manifest.Options.Count > 0)
                {
                    var given = w.options != null && w.options.Count <= ModOption.MaxOptions * 2 ? w.options : null;
                    options[local.Id] = OptionValues.Complete(local, given);
                }
            }
            var complete = resolved.Count == _received.mods.Count;

            // The host's own client shares the host's cache: the host applies.
            if (!LobbyManager.isHostRunning && !MatchUnderway)
            {
                Overlay.Apply(complete ? resolved : new List<ModInfo>(), options, _received.ais);
                if (Overlay.Failed.Count > 0)
                {
                    // Part of the pick is no better than none: back to vanilla,
                    // and the host is told which mod wouldn't go on.
                    foreach (var f in Overlay.Failed)
                    {
                        var r = report.mods.FirstOrDefault(x => x.id == f.Id);
                        if (r != null) r.state = "failed";
                    }
                    complete = false;
                    Overlay.Apply(new List<ModInfo>());
                }
                LoaderBridge.SetActiveGameplayFolders(complete
                    ? resolved.Where(m => m.DllsAreGameplay).Select(m => m.Folder).ToArray()
                    : Array.Empty<string>());
                if (!complete)
                    ModApiPlugin.Log.LogWarning("The host picked gameplay mods this machine doesn't have identically: " +
                                                string.Join(", ", report.mods.Where(m => m.state != "ok").Select(m => $"{m.id} ({m.state})")));
            }
            report.applied = complete;
            report.luaHash = Overlay.CurrentHash;
            if (sendReport) LobbyProtocol.SendToHost(LobbyProtocol.Report, report);
        }

        /// The match is loading on this client: last check that the files
        /// the VMs are about to read are the host's.
        internal static void ClientMatchLoading()
        {
            if (LobbyManager.isHostRunning || _received == null || _received.mods.Count == 0) return;
            var ok = Overlay.Applied.Count == _received.mods.Count &&
                     Overlay.Applied.Select(m => m.ContentHash).SequenceEqual(_received.mods.Select(w => w.hash)) &&
                     (string.IsNullOrEmpty(_received.luaHash) || _received.luaHash == Overlay.CurrentHash);
            if (ok) return;
            _abortReason = "this machine doesn't have the host's gameplay mods (" +
                           string.Join(", ", _received.mods.Select(w => $"{w.name} {w.version}")) +
                           "), so the match would desync.";
            _abortPending = true;
        }

        private static string Cap(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n) : s;
    }
}
