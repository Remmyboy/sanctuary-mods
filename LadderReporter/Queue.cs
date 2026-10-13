using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EM.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The ladder queue, held by the game itself: 1v1, 2v2 and 3v3, joined
    // from a panel in the main menu or from the site's Play page, which hands
    // its Queue and Leave clicks over the local bridge (POST /queue) when it
    // sees a game that can take them.
    //
    // While anything is queued the game polls the site's queue endpoint
    // (POST /api/mm/queue, bearer session) every five seconds. That poll is
    // the queue heartbeat and carries the game's own presence, so a 1v1 found
    // from here launches with no browser tab open: the answer carries the
    // match object, which goes to ApplyMatch exactly as a pushed one does,
    // and the poll speeds up until the launch is under way. Team games
    // aren't launched (the site pairs them as manual matches), so for those
    // the panel says who hosts and links to the match page.
    //
    // The site's answer is also what /status relays to the Play page, which
    // shows it instead of polling the site itself: a queued player costs the
    // site one poll every five seconds wherever they queued. Idle, the panel
    // shows the public counts (CDN-cached on the site) once a minute.
    public partial class LadderReporterPlugin
    {
        private static readonly string[] QueueModes = { "1v1", "2v2", "3v3" };
        private static readonly string[] AllFactions = { "EDA", "Chosen", "Guard" };

        private const float QueuePollS = 5f;
        private const float QueueLaunchPollS = 2.5f;   // through a 1v1's countdown and launch
        private const float QueueCountsS = 60f;
        private const float QueueRelayS = 30f;         // the last answer stays on /status after the queue ends

        private ConfigEntry<bool> _cfgQueuePanel;
        private ConfigEntry<string> _cfgQueueFactions;

        // Asked for and not sent yet: the next poll carries them.
        private readonly HashSet<string> _qJoin = new HashSet<string>();
        private readonly HashSet<string> _qLeave = new HashSet<string>();
        private string[] _qFactionsOnce;               // the Play page's pick, for the next join

        // The site's last answer.
        private JObject _qStatus;
        private string _qStatusJson;                   // as /status relays it
        private DateTime _qStatusAtUtc;
        private float _qStatusAt = -999f;
        private QueueOpen _qOpen;
        private bool _qHolding;                        // polling: something queued or launching

        private bool _qInFlight;
        private float _qNextPoll;
        private string _qError;
        private float _qWaitingForSessionSince = -1f;
        private bool _qSyncWanted, _qWasOnPlay;
        private float _qNextSync;

        // The public counts, while nothing is queued.
        private readonly int[] _qWaiting = new int[3];
        private int _qLiveGames = -1;
        private float _qNextCounts;
        private bool _qCountsInFlight;

        private sealed class QueueOpen
        {
            public string Id, Mode, MapName, HostId, HostName;
            public bool Auto;
        }

        private void AwakeQueue()
        {
            _cfgQueuePanel = Config.Bind("Queue", "Panel", false,
                "Queue for the ladder from the game: Ranked 1v1, 2v2 and 3v3 buttons on the Play screen, with how many " +
                "are waiting, and a small reminder on the home screen while you're queued. The site's Play page uses " +
                "the game's queue whether or not this is on.");
            _cfgQueueFactions = Config.Bind("Queue", "Factions", "",
                "What you can be launched as in a 1v1 queued from the game: EDA, Chosen and/or Guard, " +
                "comma-separated. Empty for any. A queue started from the site uses the site's pick.");
        }

        // ---- asking ----------------------------------------------------------

        /// Why the game can't queue right now, or null.
        private string QueueBlocked()
        {
            if (!_cfgMmEnabled.Value) return "Matchmaking is off in this game (F8 > Ladder Reporter > Matchmaking).";
            if (MockMode) return "Matchmaking.MockFile is set; the mock file is the match source.";
            if (!UsingSteam) return "Steam isn't up, so the ladder can't tell who you are.";
            return null;
        }

        /// Main thread: a click on the panel, or the Play page's over the bridge.
        private void RequestQueue(IEnumerable<string> join, IEnumerable<string> leave, string[] factions)
        {
            foreach (var mode in leave)
            {
                _qJoin.Remove(mode);
                _qLeave.Add(mode);
            }
            foreach (var mode in join)
            {
                _qLeave.Remove(mode);
                _qJoin.Add(mode);
            }
            if (factions != null) _qFactionsOnce = factions;
            _qError = null;
            _qNextPoll = 0f;
            Logger.LogInfo($"Queue: join [{string.Join(",", _qJoin)}] leave [{string.Join(",", _qLeave)}].");
        }

        private bool InQueue(string mode) =>
            _qStatus?["queues"]?[mode]?["inQueue"]?.Type == JTokenType.Boolean && (bool)_qStatus["queues"][mode]["inQueue"];

        private bool AnyQueued => QueueModes.Any(InQueue);

        // The pushed-or-polled 1v1 this game is launching, from the countdown
        // until the game starts.
        private bool LaunchUnderWay =>
            _match != null && _match.Mode == "auto" && (_match.Status == "countdown" || _match.Status == "launch") &&
            _phase != Phase.Started;

        // ---- every frame -----------------------------------------------------

        private void UpdateQueue()
        {
            if (_cfgQueuePanel == null) return;
            var now = Time.realtimeSinceStartup;
            var blocked = QueueBlocked();
            if (blocked != null)
            {
                if (_qJoin.Count > 0) _qError = blocked;
                _qJoin.Clear();
                _qLeave.Clear();
                _qHolding = false;
                return;
            }

            // A lobby joined or a game started that isn't the ladder's: a
            // match found now couldn't be played, so the queue goes.
            var state = CurrentState();
            if (_phase == Phase.Idle && (state == "lobby" || state == "ingame" || state == "loading"))
            {
                var queued = Enumerable.Range(0, QueueModes.Length).Where(ModeQueued).Select(i => QueueModes[i]).ToList();
                if (queued.Count > 0)
                {
                    Logger.LogInfo($"Queue: {(state == "lobby" ? "joined a lobby" : "another game is " + state)}; leaving the ladder queue.");
                    RequestQueue(Enumerable.Empty<string>(), queued, null);
                    Overlay("QUEUE", state == "lobby"
                        ? "Left the ranked queue: you joined a lobby."
                        : "Left the ranked queue: you started another game.", 10f);
                }
            }

            // The game it found has been played (or this is some other one):
            // the panel stops naming it.
            if (_qOpen != null && !_qHolding && state == "ingame") _qOpen = null;

            // Arriving on the Play screen: ask the site once what this
            // account is queued in, so a queue started on the website shows
            // (and is held) here too.
            var onPlay = QueueMenuWindow() == InterfaceManager.Window.Play;
            if (onPlay && !_qWasOnPlay && now >= _qNextSync) _qSyncWanted = true;
            _qWasOnPlay = onPlay;

            _qHolding = _qJoin.Count > 0 || _qLeave.Count > 0 || AnyQueued || LaunchUnderWay;
            if ((_qHolding || _qSyncWanted) && !_qInFlight && now >= _qNextPoll && StartQueuePoll(now))
            {
                _qSyncWanted = false;
                _qNextSync = now + 30f;
            }
            if (!_qHolding && QueueCountsWanted() && !_qCountsInFlight && now >= _qNextCounts) StartCounts(now);
        }

        /// False while it is still signing in.
        private bool StartQueuePoll(float now)
        {
            if (_mmToken == null)
            {
                // The first queue of the session signs in first; EnsureSession
                // throttles itself and says why it can't.
                if (_qWaitingForSessionSince < 0f) _qWaitingForSessionSince = now;
                if (now - _qWaitingForSessionSince > 45f && _qError == null) _qError = "Can't sign in to the ladder: is Steam online?";
                EnsureSession();
                return false;
            }
            _qWaitingForSessionSince = -1f;
            _qInFlight = true;
            StartCoroutine(QueuePoll());
            return true;
        }

        private IEnumerator QueuePoll()
        {
            var join = _qJoin.ToArray();
            var leave = _qLeave.ToArray();
            var factions = _qFactionsOnce ?? ConfiguredFactions();
            var body = new JObject
            {
                ["join"] = new JArray(join.Cast<object>().ToArray()),
                ["leave"] = new JArray(leave.Cast<object>().ToArray()),
                ["factions"] = new JArray(factions.Cast<object>().ToArray()),
                ["mod"] = new JObject { ["state"] = CurrentState(), ["modVersion"] = ModVersion, ["gameVersion"] = _gameVersion },
            };
            var req = Post("/api/mm/queue", body, _mmToken);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            _qInFlight = false;
            var now = Time.realtimeSinceStartup;
            try
            {
                if ((int)req.responseCode == 401)
                {
                    _mmToken = null;   // expired: sign in again, then this poll again
                    _qNextPoll = now;
                    yield break;
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    LogHttp("queue", req);
                    _qError = "Can't reach the ladder site; still trying.";
                    _qNextPoll = now + 10f;
                    yield break;
                }
                // Sent: the site has them now (or refused them, which the
                // status below shows).
                foreach (var m in join) _qJoin.Remove(m);
                foreach (var m in leave) _qLeave.Remove(m);
                if (join.Length > 0) _qFactionsOnce = null;
                _qError = null;
                ApplyQueueAnswer(JObject.Parse(req.downloadHandler.text));
                _qNextPoll = now + (LaunchUnderWay ? QueueLaunchPollS : QueuePollS);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Queue: answer unreadable: {e.Message}");
                _qNextPoll = now + 10f;
            }
            finally
            {
                req.Dispose();
            }
        }

        private void ApplyQueueAnswer(JObject answer)
        {
            var wasQueued = AnyQueued;
            _qStatus = answer["status"] as JObject;
            _qStatusJson = _qStatus?.ToString(Newtonsoft.Json.Formatting.None);
            _qStatusAt = Time.realtimeSinceStartup;
            _qStatusAtUtc = DateTime.UtcNow;
            if (_qStatus?["queues"] is JObject queues)
            {
                for (var i = 0; i < QueueModes.Length; i++)
                {
                    var w = queues[QueueModes[i]]?["waiting"];
                    if (w != null && w.Type == JTokenType.Integer) _qWaiting[i] = (int)w;
                }
            }
            var live = _qStatus?["liveGames"];
            if (live != null && live.Type == JTokenType.Integer) _qLiveGames = (int)live;

            var open = answer["open"] as JObject;
            var previous = _qOpen;
            _qOpen = open == null
                ? null
                : new QueueOpen
                {
                    Id = (string)open["id"],
                    Mode = (string)open["mode"],
                    MapName = (string)open["mapName"],
                    Auto = open["auto"]?.Type == JTokenType.Boolean && (bool)open["auto"],
                    HostId = (string)open["host"]?["steamId"],
                    HostName = (string)open["host"]?["name"],
                };
            // A team game (or a 1v1 the site paired as manual, or one that
            // fell back to manual before it launched) is the player's to
            // host or join; say so once, loudly.
            var fellBack = previous != null && _qOpen != null && previous.Id == _qOpen.Id && previous.Auto && !_qOpen.Auto;
            if (_qOpen != null && !_qOpen.Auto && (fellBack || (previous?.Id != _qOpen.Id && wasQueued)))
            {
                var hosting = _qOpen.HostId == LocalSteamId;
                Logger.LogInfo($"Queue: {_qOpen.Mode} match {_qOpen.Id} found on {_qOpen.MapName}, host {_qOpen.HostName}.");
                RestoreWindow();
                Overlay("MATCH FOUND", $"{_qOpen.Mode} on {_qOpen.MapName}. " +
                    (hosting ? "You're hosting: create the lobby, the site has the teams." : $"{_qOpen.HostName} is hosting: join their lobby.") +
                    " The match page has the details.", 40f);
            }

            // The 1v1 to launch: the same object the match room pushes, and
            // the same checks. A null here is no news (the match room may be
            // the one driving it); a cancelled match comes back as no open
            // match, which mid-launch is the cancel.
            if (answer["match"] is JObject mo)
            {
                MmMatch m;
                string problem;
                try
                {
                    m = MmMatch.Parse(mo);
                    problem = m.Problem();
                }
                catch (Exception e)
                {
                    m = null;
                    problem = e.Message;
                }
                if (problem != null) Logger.LogWarning($"Queue: the site's match is malformed ({problem}); ignored.");
                else ApplyPushedMatch(m);
            }
            else if (_qOpen == null && _match != null && LaunchUnderWay)
            {
                ApplyPushedMatch(null);
            }
        }

        private string[] ConfiguredFactions()
        {
            var picked = (_cfgQueueFactions.Value ?? "").Split(',')
                .Select(f => AllFactions.FirstOrDefault(a => string.Equals(a, f.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(f => f != null).Distinct().ToArray();
            return picked.Length > 0 ? picked : AllFactions;
        }

        // ---- the public counts -------------------------------------------------

        private void StartCounts(float now)
        {
            _qCountsInFlight = true;
            _qNextCounts = now + QueueCountsS;
            StartCoroutine(FetchCounts());
        }

        private IEnumerator FetchCounts()
        {
            var req = UnityWebRequest.Get(_cfgMmBaseUrl.Value.TrimEnd('/') + "/api/queue-counts");
            req.timeout = 15;
            _inFlight.Add(req);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            _qCountsInFlight = false;
            try
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    LogHttp("queue counts", req);
                    yield break;
                }
                var o = JObject.Parse(req.downloadHandler.text);
                for (var i = 0; i < QueueModes.Length; i++)
                {
                    var w = o["waiting"]?[QueueModes[i]];
                    if (w != null && w.Type == JTokenType.Integer) _qWaiting[i] = (int)w;
                }
                var live = o["liveGames"];
                if (live != null && live.Type == JTokenType.Integer) _qLiveGames = (int)live;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Queue: counts unreadable: {e.Message}");
            }
            finally
            {
                req.Dispose();
            }
        }

        // ---- the bridge ------------------------------------------------------

        /// /status's `queue`: the site's last answer while the game holds a
        /// queue (and a while after, so the page sees it end), else null.
        private string QueueRelayJson()
        {
            if (_qStatusJson == null) return null;
            if (!_qHolding && Time.realtimeSinceStartup - _qStatusAt > QueueRelayS) return null;
            return _qStatusJson;
        }

        // ---- the panel ---------------------------------------------------------

        private static readonly AccessTools.FieldRef<InterfaceManager, InterfaceManager.Window> QueueWindow =
            AccessTools.FieldRefAccess<InterfaceManager, InterfaceManager.Window>("currentWindow");

        private RectTransform _qPanel;
        private HudButton _qButton;
        private TMP_Text _qLine;
        private Image _qDot;
        private string _qPanelErr;
        private bool _qPanelLogged;

        // The menu, with nothing launching: the window it is on, or null.
        private InterfaceManager.Window? QueueMenuWindow()
        {
            if (!_cfgQueuePanel.Value || QueueBlocked() != null || CurrentState() != "menu") return null;
            if (_phase != Phase.Idle && _phase != Phase.Started) return null;
            var im = InterfaceManager.Instance;
            return im == null ? (InterfaceManager.Window?)null : QueueWindow(im);
        }

        // The one sign, on every menu screen, that you're in the ranked
        // queue (or that a match was found for you to host or join), with
        // the way out of it. The queue itself is on the Play screen
        // (PlayRanked.cs).
        private bool QueuePanelWanted() => QueueMenuWindow() != null && (AnyModeQueued || MatchToHost);

        // Counts are worth fetching while the Play screen is up.
        private bool QueueCountsWanted() => QueueMenuWindow() == InterfaceManager.Window.Play;

        private void UpdateQueuePanel()
        {
            UpdatePlayRanked();
            try
            {
                var show = QueuePanelWanted();
                if (!show)
                {
                    if (_qPanel != null && _qPanel.gameObject.activeSelf) _qPanel.gameObject.SetActive(false);
                    return;
                }
                var root = HudCanvas.EnsureOwn();
                if (root == null) return;
                var built = _qPanel == null;
                if (built) BuildQueuePanel(root);
                if (built || !_qPanel.gameObject.activeSelf)
                {
                    _qPanel.gameObject.SetActive(true);
                    if (!_qPanelLogged)
                    {
                        _qPanelLogged = true;
                        Logger.LogInfo("Queue: queue strip shown in the menu.");
                    }
                }
                FillQueuePanel();
                var size = HudCanvas.Size;
                var at = new Vector2(Mathf.Round(size.x - _qPanel.rect.width - 48f), Mathf.Round(size.y - _qPanel.rect.height - 36f));
                if (_qPanel.anchoredPosition != at) _qPanel.anchoredPosition = at;
            }
            catch (Exception e)
            {
                if (_qPanelErr == e.Message) return;
                _qPanelErr = e.Message;
                Logger.LogWarning($"Queue: strip: {e}");
            }
        }

        private void BuildQueuePanel(RectTransform root)
        {
            // The menu has no HUD to borrow the game's typeface from.
            var im = InterfaceManager.Instance;
            if (im != null) HudCanvas.TakeFont(im.GetComponentInChildren<TMP_Text>(true));

            _qPanel = HudCanvas.Plate(root, "Ranked queue");
            var group = _qPanel.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(28, 14, 14, 14);
            group.spacing = 20f;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            HudCanvas.FitToContents(_qPanel);

            var dotCell = HudControls.Cell(_qPanel, "Dot", 16f, 56f);
            _qDot = HudCanvas.Fill(dotCell, "Fill", AccentColour);
            _qDot.sprite = HudStyle.Shade;
            _qDot.type = Image.Type.Sliced;
            var drt = _qDot.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
            drt.sizeDelta = new Vector2(16f, 16f);

            _qLine = HudControls.Label(_qPanel, "Line", "", 28f, Color.white, TextAlignmentOptions.MidlineLeft);
            _qButton = HudButton.Create(_qPanel, "Action", "LEAVE QUEUE", 230f, 56f, 24f);
            _qButton.OnClick = QueueStripAction;
        }

        // Leave every queue; with a match to host instead, its page.
        private void QueueStripAction()
        {
            if (!AnyModeQueued && MatchToHost)
            {
                OpenQueueMatch();
                return;
            }
            Logger.LogInfo("Queue: left from the queue strip.");
            RequestQueue(Enumerable.Empty<string>(), Enumerable.Range(0, QueueModes.Length).Where(ModeQueued).Select(i => QueueModes[i]).ToList(), null);
        }

        private void FillQueuePanel()
        {
            string line, action;
            if (AnyModeQueued)
            {
                var times = Enumerable.Range(0, QueueModes.Length).Select(ModeTime).Where(t => t != null);
                line = "<color=#3DAFFF>RANKED</color>   SEARCHING   " + string.Join("   ", times);
                action = "LEAVE QUEUE";
            }
            else
            {
                line = _qOpen.HostId == LocalSteamId
                    ? $"<color=#3DAFFF>MATCH FOUND</color>   {_qOpen.Mode.ToUpperInvariant()} ON {_qOpen.MapName.ToUpperInvariant()}   YOU'RE HOSTING"
                    : $"<color=#3DAFFF>MATCH FOUND</color>   {_qOpen.Mode.ToUpperInvariant()} ON {_qOpen.MapName.ToUpperInvariant()}   {_qOpen.HostName} HOSTS";
                action = "MATCH PAGE";
            }
            HudCanvas.SetText(_qLine, line);
            _qButton.SetLabel(action);
            // A slow pulse: searching, not stuck.
            var c = AccentColour;
            c.a = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 2f));
            _qDot.color = c;
        }

        private void ToggleQueue(string mode)
        {
            var queued = (InQueue(mode) || _qJoin.Contains(mode)) && !_qLeave.Contains(mode);
            if (queued) RequestQueue(Enumerable.Empty<string>(), new[] { mode }, null);
            else RequestQueue(new[] { mode }, Enumerable.Empty<string>(), null);
        }

        // ---- what both views say ---------------------------------------------

        private string LiveGamesText() =>
            _qLiveGames < 0 ? "" : _qLiveGames == 1 ? "1 game live" : $"{_qLiveGames} games live";

        /// Queued in mode i, or asked to be (and not asked to leave).
        private bool ModeQueued(int i)
        {
            var mode = QueueModes[i];
            return (InQueue(mode) && !_qLeave.Contains(mode)) || _qJoin.Contains(mode);
        }

        /// "1V1  0:42" while queued in mode i, "1V1  ..." while the join is
        /// on its way, else null.
        private string ModeTime(int i)
        {
            if (!ModeQueued(i)) return null;
            var mode = QueueModes[i];
            if (!InQueue(mode)) return $"{mode.ToUpperInvariant()}  ...";
            var secs = _qStatus["queues"][mode]["queuedSeconds"];
            var s = (secs != null && secs.Type == JTokenType.Integer ? (int)secs : 0) +
                    (int)(DateTime.UtcNow - _qStatusAtUtc).TotalSeconds;
            return $"{mode.ToUpperInvariant()}  {s / 60}:{s % 60:00}";
        }

        private bool AnyModeQueued => Enumerable.Range(0, QueueModes.Length).Any(ModeQueued);

        // A match found that this game won't launch: someone hosts it by hand.
        private bool MatchToHost => _qOpen != null && !_qOpen.Auto;

        private string QueueLine()
        {
            if (_qError != null) return _qError;
            if (MatchToHost)
            {
                return _qOpen.HostId == LocalSteamId
                    ? $"Match found: {_qOpen.Mode} on {_qOpen.MapName}. You're hosting."
                    : $"Match found: {_qOpen.Mode} on {_qOpen.MapName}. {_qOpen.HostName} is hosting.";
            }
            if (AnyModeQueued) return "Searching. A 1v1 launches by itself; for a team game this says who hosts.";
            return "Pick a mode to queue; you can queue for several. The number is how many are waiting.";
        }

        private void OpenQueueMatch()
        {
            var id = _qOpen?.Id;
            if (id == null || UploadId(id) != id) return;
            var url = _cfgMmBaseUrl.Value.TrimEnd('/') + "/ladder/match/" + id;
            Logger.LogInfo($"Queue: opening {url}");
            Application.OpenURL(url);
        }
    }
}
