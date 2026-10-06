using System;
using System.Collections.Generic;
using System.Linq;
using EM.Network;
using EM.UI;
using HarmonyLib;
using Michsky.UI.Beam;
using Sanctuary.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SanctuaryHud
{
    // Gameplay mods: the page's second tab, and the lobby's Mods panel.
    //
    // The tab is information and defaults: which gameplay mods are installed,
    // what they are, what's wrong with them, and which ones are picked when
    // this player hosts. The pick itself happens in the lobby, where the host
    // switches mods on and everyone sees who has them.
    //
    // The lobby panel is an overlay inside the lobby screen rather than a
    // screen of its own: leaving the lobby window would make the game drop
    // its chat and roster listeners and clear the chat on the way back. Its
    // rows are the page's own row templates, so it looks like the rest.
    internal sealed partial class ModsPage
    {
        // What the tab was last built from; compared every frame the page is
        // up, so as plain values rather than a string built each time.
        private (int catalog, int lobby, string hash) _gameplayBuiltFrom = (-1, -1, null);

        private static (int catalog, int lobby, string hash) GameplayInputs() =>
            (ModCatalog.Version, Lobby.ChangeCounter, Overlay.CurrentHash);

        private bool GameplayChanged() => !GameplayInputs().Equals(_gameplayBuiltFrom);

        private void RebuildGameplayTab()
        {
            if (_gameList == null) return;
            _gameplayBuiltFrom = GameplayInputs();
            Clear(_gameList);

            InstallNotices(_gameList);
            Heading(_gameList, "Gameplay mods: the lobby host picks them");
            var live = Modding.ActiveGameplayMods;
            DescribeNext("Right now",
                "Outside a lobby the game always runs vanilla, so you can join anyone, and every lobby starts with none picked. " +
                "The host switches them on in the lobby's Mods panel; the pick is applied " +
                "on every player's machine before the match starts, and Start waits until everyone has identical copies. " +
                "The code is the game's Lua hash: two players with the same code run the same scripts.");
            InfoRow(_gameList,
                live.Count == 0 ? "Running vanilla" : "Live: " + string.Join(", ", live.Select(m => m.Name)),
                (live.Count == 0 ? "Vanilla   " : "Modded   ") + ModManagerPlugin.Short(Overlay.CurrentHash));
            Line(_gameList);

            var mods = ModCatalog.GameplayMods.ToList();
            if (mods.Count == 0)
            {
                DescribeNext("No gameplay mods",
                    "A gameplay mod is a folder under SanctuaryMods with a mod.json saying \"kind\": \"gameplay\" and Lua or " +
                    ".santp files laid out like LJ\\lua under its lua folder. See the Writing a mod guide.");
                InfoRow(_gameList, "No gameplay mods installed", "SanctuaryMods\\<mod>\\mod.json");
                ScrollToTop(_gameList);
                return;
            }

            Heading(_gameList, "Installed");
            foreach (var mod in mods)
            {
                var m = mod;
                DescribeNext(m.Name, GameplayDescription(m));
                InfoRow(_gameList, $"{m.Name} {m.Version}", GameplayFiles(m));
                UpdateRow(_gameList, m.FolderName, m.Name, true);
            }

            var troubled = ModCatalog.Mods.Where(m => m.Problems.Count > 0).ToList();
            if (troubled.Count > 0)
            {
                Line(_gameList);
                Heading(_gameList, "Problems");
                foreach (var m in troubled)
                {
                    DescribeNext(m.Name, string.Join("\n", m.Problems));
                    InfoRow(_gameList, m.Name, m.Problems[0]);
                }
            }
            ScrollToTop(_gameList);
        }

        /// Mistakes in how mods were installed (an archive left zipped, a DLL
        /// with no folder), at the top of both tabs where they can't be
        /// missed. Nothing when all is well.
        private void InstallNotices(Transform list)
        {
            var notices = ModCatalog.Notices;
            if (notices.Count == 0) return;
            Heading(list, "Check your SanctuaryMods folder");
            foreach (var n in notices)
            {
                var first = n.IndexOf(' ');
                DescribeNext("SanctuaryMods", n + "\n\nOpen Mods Folder, at the bottom of this page, opens it.");
                InfoRow(list, first > 0 ? n.Substring(0, first) : n, first > 0 ? ShortNotice(n.Substring(first + 1)) : "");
            }
            Line(list);
        }

        private static string ShortNotice(string rest)
        {
            var stop = rest.IndexOf('.');
            return stop > 0 ? rest.Substring(0, stop) : rest;
        }

        private static string GameplayFiles(ModInfo m)
        {
            var parts = new List<string>();
            if (m.LuaCount > 0) parts.Add($"{m.LuaCount} lua");
            if (m.SantpCount > 0) parts.Add($"{m.SantpCount} santp");
            if (m.DllsAreGameplay && m.Dlls.Count > 0) parts.Add($"{m.Dlls.Count} dll");
            if (m.Manifest.Options.Count > 0) parts.Add(m.Manifest.Options.Count == 1 ? "1 option" : $"{m.Manifest.Options.Count} options");
            if (m.Manifest.Ais.Count > 0) parts.Add(m.Manifest.Ais.Count == 1 ? "1 AI" : $"{m.Manifest.Ais.Count} AIs");
            if (m.Manifest.Author.Length > 0) parts.Add("by " + m.Manifest.Author);
            if (m.Manifest.IsForOtherGameVersion(Application.version)) parts.Add("made for game " + m.Manifest.GameVersion);
            return string.Join(", ", parts);
        }

        private static string GameplayDescription(ModInfo m)
        {
            var text = m.Manifest.Description.Length > 0 ? m.Manifest.Description + "\n\n" : "";
            text += $"Id {m.Id}, contents {m.ShortHash}. ";
            if (m.Manifest.GameVersion.Length > 0) text += $"Made for game version {m.Manifest.GameVersion}. ";
            if (m.Manifest.Synthesised) text += "This folder has no mod.json, so its name stands in for one. ";
            if (m.Manifest.Requires.Count > 0) text += $"Needs {string.Join(", ", m.Manifest.Requires)} picked too. ";
            if (m.Manifest.Url.Length > 0) text += $"Get it from {m.Manifest.Url}. ";
            text += m.Manifest.AiFolder.Length > 0
                ?"To play against it, host a lobby and pick it in an AI seat's Player/AI dropdown; everyone in the match needs an identical copy."
                : "To play it, host a lobby and switch it on in the lobby's Mods panel; everyone in the match needs an identical copy.";
            if (m.Manifest.Ais.Count > 0)
            {
                text += "\n\nAIs, given to AI seats by the host from each seat's Player/AI dropdown:";
                foreach (var a in m.Manifest.Ais) text += $"\n{a.Name}" + (a.Description.Length > 0 ? ": " + a.Description : "");
            }
            if (m.Manifest.Options.Count > 0)
            {
                text += "\n\nOptions, set by the host in the lobby's Mods panel:";
                foreach (var o in m.Manifest.Options) text += $"\n{o.Label}: {OptionDescription(o)}";
            }
            if (m.Problems.Count > 0) text += "\n\n" + string.Join("\n", m.Problems);
            return text;
        }

        // ---- the lobby panel ------------------------------------------------

        private static readonly AccessTools.FieldRef<LobbyInterface, ButtonManager> LobbySettingsButton =
            AccessTools.FieldRefAccess<LobbyInterface, ButtonManager>("settingsButton");

        private LobbyInterface _lobbyFor;
        private ButtonManager _lobbyButton;
        private GameObject _lobbyPanel;
        private Transform _lobbyList;
        private string _lobbySignature = "";
        private string _lobbyButtonText = "";
        // What the button's label was made from: (picked count, "!" shown).
        private (int count, bool bang) _lobbyButtonFrom = (-1, false);

        private void TickLobby()
        {
            var ui = LobbyInterface.Instance;
            var inLobby = ui != null && ui.isActiveAndEnabled && LobbyManager.IsInLobby && !Lobby.MatchUnderway;
            if (!inLobby)
            {
                if (_lobbyPanel != null) _lobbyPanel.SetActive(false);
                if (!LobbyManager.IsInLobby) { _announced = null; _announceKey = null; }
                return;
            }
            if (_tSwitchRow == null) return; // the page (and its templates) isn't built yet
            if (!ReferenceEquals(ui, _lobbyFor) || _lobbyButton == null) BuildLobbyButton(ui);

            AnnounceSelection(ui);

            var from = LobbyButtonInputs();
            if (!from.Equals(_lobbyButtonFrom) && _lobbyButton != null)
            {
                _lobbyButtonFrom = from;
                var label = LobbyButtonLabel(from);
                if (label != _lobbyButtonText)
                {
                    _lobbyButtonText = label;
                    SetButtonText(_lobbyButton, label);
                }
            }

            if (_lobbyPanel != null && _lobbyPanel.activeSelf)
            {
                SettlingBriefly(); // keeps the settling clock running
                // Rows are rebuilt only when the rows themselves change (a mod
                // picked or dropped, a player joining); otherwise the rows
                // there update in place. Neither happens under the pointer
                // while it's dragging a slider or typing. Whatever the rows
                // show changes the lobby's counter or the catalog's version
                // first, so the rest waits for one of those to move.
                var key = (Lobby.ChangeCounter, ModCatalog.Version, Lobby.CanChangeSelection, SettlingShown(), _pickerVersion);
                if (!(_lobbyPanelKey.HasValue && _lobbyPanelKey.Value.Equals(key)) && !LobbyPanelBusy())
                {
                    _lobbyPanelKey = key;
                    var structure = LobbyStructure();
                    if (structure != _lobbyStructure) RebuildLobbyPanel();
                    else
                    {
                        var values = LobbySignature();
                        if (values != _lobbySignature)
                        {
                            _lobbySignature = values;
                            RefreshLobbyValues();
                        }
                    }
                }
            }
        }

        // A change reaching everyone takes a moment (the host's option
        // settles, then each player confirms it). For that moment the panel
        // holds still rather than flashing "Start waits..." and back; only a
        // change that takes longer than this says it's updating.
        private const float SettleGrace = 1f;
        private float _settlingSince = -1f;

        /// Settling, and not for long yet: leave the panel as it was.
        private bool SettlingBriefly()
        {
            if (!Lobby.Settling) { _settlingSince = -1f; return false; }
            if (_settlingSince < 0f) _settlingSince = Time.unscaledTime;
            return Time.unscaledTime - _settlingSince < SettleGrace;
        }

        /// Settling long enough to say so.
        private bool SettlingShown() => Lobby.Settling && _settlingSince >= 0f && Time.unscaledTime - _settlingSince >= SettleGrace;

        /// What the lobby button's label says: how many mods are picked, and
        /// whether to flag a problem. The "!" is for someone missing
        /// something, not for a change on its way.
        private static (int count, bool bang) LobbyButtonInputs()
        {
            var n = Lobby.Selection.Count;
            return (n, n > 0 && Lobby.StartBlockedReason != null && !Lobby.Settling);
        }

        private static string LobbyButtonLabel((int count, bool bang) from) =>
            from.count == 0 ? "Mods" : from.bang ? $"Mods ({from.count}) !" : $"Mods ({from.count})";

        private string LobbySignature() =>
            $"{Lobby.ChangeCounter}|{ModCatalog.Version}|{Lobby.CanChangeSelection}|{SettlingShown()}|" +
            string.Join(",", Lobby.Players.Select(p => p.Name + p.State + p.Detail));

        // What the chat was last told: each mod's id and contents, and its
        // option values as shown.
        private List<(string id, string hash, string name, Dictionary<string, string> options)> _announced;
        private (int lobby, int catalog)? _announceKey;
        private (int, int, bool, bool, int)? _lobbyPanelKey;

        /// A line in the lobby chat whenever the pick changes, so nobody
        /// misses it: players see what they're about to play. A change of
        /// options alone says just what changed.
        private void AnnounceSelection(LobbyInterface ui)
        {
            if (!Lobby.HostHasModSupport) return;
            // Mid-change the host's values move with its controls; say it
            // once it has settled, so a dragged slider isn't a line per step.
            if (Lobby.Settling) return;
            // Nothing new since the last look.
            var key = (Lobby.ChangeCounter, ModCatalog.Version);
            if (_announceKey.HasValue && _announceKey.Value.Equals(key)) return;
            _announceKey = key;
            var sel = Lobby.Selection;
            var now = sel.Select(s => (s.Id, s.ContentHash, $"{s.Name} {s.Version}".TrimEnd(), OptionDisplay(s))).ToList();
            var was = _announced;
            if (was != null && was.Count == now.Count &&
                was.Zip(now, (a, b) => a.id == b.Item1 && a.hash == b.Item2 && SameOptions(a.options, b.Item4)).All(x => x))
                return;
            _announced = now;
            if (was == null && sel.Count == 0) return;

            // Same mods, same copies: only options moved.
            if (was != null && was.Count == now.Count && was.Zip(now, (a, b) => a.id == b.Item1 && a.hash == b.Item2).All(x => x))
            {
                var changes = new List<string>();
                for (var i = 0; i < now.Count; i++)
                {
                    var diff = now[i].Item4.Where(kv => !was[i].options.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        .Select(kv => $"{kv.Key} {kv.Value}").ToList();
                    if (diff.Count > 0) changes.Add($"{now[i].Item3}: {string.Join(", ", diff)}");
                }
                if (changes.Count > 0) ui.AddChatMessage("Gameplay mods: " + string.Join("; ", changes) + ".");
                return;
            }

            ui.AddChatMessage(sel.Count == 0
                ? "Gameplay mods: none, vanilla match."
                : "Gameplay mods: " + string.Join(", ", sel.Select(s => $"{s.Name} {s.Version}".TrimEnd() + OptionSummary(s))) + ". Mods button for details.");
        }

        /// A mod's option values as the chat shows them: label to shown value.
        private static Dictionary<string, string> OptionDisplay(SelectedMod s)
        {
            var result = new Dictionary<string, string>();
            var defs = s.OptionDefinitions;
            if (defs.Count > 0)
                foreach (var o in defs) result[o.Label] = o.Display(s.Options.TryGetValue(o.Key, out var v) ? v : null);
            else
                foreach (var kv in s.Options) result[kv.Key] = kv.Value;
            return result;
        }

        private static bool SameOptions(Dictionary<string, string> a, Dictionary<string, string> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

        private void BuildLobbyButton(LobbyInterface ui)
        {
            DestroyLobbyPanel();
            _lobbyFor = ui;
            var settings = LobbySettingsButton(ui);
            if (settings == null)
            {
                _log.LogWarning("Lobby mods panel: the lobby's Settings button was not found, so there is no Mods button.");
                return;
            }

            // A clone of the lobby's own Settings button, beside it. The
            // Settings button is the host's only; ours is everyone's.
            var go = Object.Instantiate(settings.gameObject, settings.transform.parent);
            go.name = "Mods Button";
            go.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
            go.SetActive(true);
            _lobbyButton = go.GetComponent<ButtonManager>();
            // Whatever the Settings button was wired to in the editor goes;
            // the listener LobbyInterface added at Start was never copied.
            _lobbyButton.onClick.RemoveAllListeners();
            for (var i = 0; i < _lobbyButton.onClick.GetPersistentEventCount(); i++)
                _lobbyButton.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            _lobbyButton.onClick.AddListener(ToggleLobbyPanel);
            _lobbyButton.Interactable(true);
            _lobbyButtonText = "";
            _lobbyButtonFrom = (-1, false);

            // Laid out by hand when the buttons aren't in a layout group:
            // just left of the Settings button.
            if (settings.transform.parent.GetComponent<LayoutGroup>() == null)
            {
                var src = (RectTransform)settings.transform;
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = src.anchoredPosition - new Vector2(src.rect.width + 15f, 0f);
            }
        }

        private void ToggleLobbyPanel()
        {
            if (_lobbyPanel != null && _lobbyPanel.activeSelf)
            {
                _lobbyPanel.SetActive(false);
                return;
            }
            if (_lobbyPanel == null) BuildLobbyPanel();
            _lobbyPanel.SetActive(true);
            _lobbyPanel.transform.SetAsLastSibling();
            RebuildLobbyPanel();
        }

        private void BuildLobbyPanel()
        {
            var root = new GameObject("Mods Lobby Panel", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.SetParent(_lobbyFor.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            // Dims the lobby and takes its clicks while the panel is up.
            root.GetComponent<Image>().color = new Color(0.02f, 0.03f, 0.06f, 0.88f);

            var box = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var brt = (RectTransform)box.transform;
            brt.SetParent(rt, false);
            // Most of the lobby screen, whatever its size: the players list,
            // chat and buttons stay just visible around it.
            brt.anchorMin = new Vector2(0.14f, 0.1f);
            brt.anchorMax = new Vector2(0.86f, 0.9f);
            brt.offsetMin = brt.offsetMax = Vector2.zero;
            box.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.11f, 0.97f);

            // The scroll view. Its own (invisible) image makes the whole view
            // take the mouse wheel and drags, not just the rows: without it
            // the gaps between rows let the wheel through to nothing.
            var view = new GameObject("View", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            var vrt = (RectTransform)view.transform;
            vrt.SetParent(brt, false);
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(40f, 140f);
            vrt.offsetMax = new Vector2(-70f, -40f);
            view.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

            var content = new GameObject("Layout Group", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var crt = (RectTransform)content.transform;
            crt.SetParent(vrt, false);
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = Vector2.zero;
            var vl = content.GetComponent<VerticalLayoutGroup>();
            vl.spacing = 15f;
            vl.childControlWidth = true;
            vl.childControlHeight = false;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = view.GetComponent<ScrollRect>();
            scroll.content = crt;
            scroll.viewport = vrt;
            scroll.horizontal = false;
            // Scrolls like the game's own settings list, with its scrollbar.
            var gameScroll = _gameList != null ? _gameList.GetComponentInParent<ScrollRect>(true) : null;
            if (gameScroll != null)
            {
                scroll.movementType = gameScroll.movementType;
                scroll.elasticity = gameScroll.elasticity;
                scroll.inertia = gameScroll.inertia;
                scroll.decelerationRate = gameScroll.decelerationRate;
                scroll.scrollSensitivity = Mathf.Max(gameScroll.scrollSensitivity, 60f);
            }
            else
            {
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 120f;
            }
            var gameBar = gameScroll != null ? gameScroll.verticalScrollbar : null;
            if (gameBar != null)
            {
                var bar = Object.Instantiate(gameBar.gameObject, brt, false);
                bar.name = "Scrollbar";
                var bart = (RectTransform)bar.transform;
                var width = Mathf.Max(((RectTransform)gameBar.transform).rect.width, 8f);
                bart.anchorMin = new Vector2(1f, 0f);
                bart.anchorMax = new Vector2(1f, 1f);
                bart.pivot = new Vector2(1f, 0.5f);
                bart.offsetMin = new Vector2(-40f - width, 140f);
                bart.offsetMax = new Vector2(-40f, -40f);
                bar.SetActive(true);
                scroll.verticalScrollbar = bar.GetComponent<Scrollbar>();
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            }
            _lobbyScroll = scroll;
            _lobbyList = crt;

            var footer = new GameObject("Footer", typeof(RectTransform), typeof(VerticalLayoutGroup));
            var frt = (RectTransform)footer.transform;
            frt.SetParent(brt, false);
            frt.anchorMin = new Vector2(0f, 0f);
            frt.anchorMax = new Vector2(1f, 0f);
            frt.pivot = new Vector2(0.5f, 0f);
            frt.offsetMin = new Vector2(40f, 30f);
            frt.offsetMax = new Vector2(-40f, 110f);
            var fl = footer.GetComponent<VerticalLayoutGroup>();
            fl.childControlWidth = true;
            fl.childControlHeight = false;
            fl.childForceExpandWidth = true;
            fl.childAlignment = TextAnchor.LowerCenter;
            ButtonRow(frt, "Close", () => { if (_lobbyPanel != null) _lobbyPanel.SetActive(false); });

            _lobbyPanel = root;
            root.SetActive(false);
        }

        private ScrollRect _lobbyScroll;
        private string _lobbyStructure = "";
        // One per row that shows something which changes without the rows
        // changing (option values, the status line, each player's state).
        private readonly List<Action> _lobbyUpdaters = new List<Action>();

        /// What decides which rows the panel has. Values shown in them (option
        /// values, statuses) are left out: those update in place.
        private string LobbyStructure()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Lobby.IsHost).Append('|').Append(Lobby.HostHasModSupport).Append('|')
              .Append(Lobby.IsLadderLobby).Append('|').Append(Lobby.CanChangeSelection).Append('|')
              .Append(ModCatalog.Version).Append('|').Append(PickerStructure()).Append('|');
            foreach (var s in Lobby.Selection)
            {
                sb.Append(s.Id).Append('@').Append(s.ContentHash)
                  .Append(s.Local != null ? "+" : s.LocalDifferent != null ? "~" : "-").Append(':');
                foreach (var o in s.OptionDefinitions) sb.Append(o.Key).Append(',');
                sb.Append(';');
            }
            sb.Append('|');
            foreach (var p in Lobby.Players) sb.Append(p.PlayerId).Append('=').Append(p.Name).Append(';');
            return sb.ToString();
        }

        private void RebuildLobbyPanel()
        {
            if (_lobbyList == null) return;
            _lobbyStructure = LobbyStructure();
            _lobbySignature = LobbySignature();

            // Where the list was scrolled to, kept across the rebuild.
            var content = (RectTransform)_lobbyList;
            var scrolledTo = _lobbyScrollToTop ? 0f : content.anchoredPosition.y;
            _lobbyScrollToTop = false;

            Clear(_lobbyList);
            _lobbyUpdaters.Clear();
            _keepEmptyValues = true;
            try { FillLobbyPanel(); }
            finally { _keepEmptyValues = false; }
            RefreshLobbyValues();

            // Lay the new rows out now rather than next frame, so the list is
            // never drawn half-built, then put the scroll position back.
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            if (_lobbyScroll != null && _lobbyScroll.viewport != null)
            {
                var max = Mathf.Max(0f, content.rect.height - _lobbyScroll.viewport.rect.height);
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Clamp(scrolledTo, 0f, max));
                _lobbyScroll.velocity = Vector2.zero;
            }
        }

        /// Every row that updates in place, from the lobby as it is now.
        private void RefreshLobbyValues()
        {
            foreach (var update in _lobbyUpdaters)
            {
                try { update(); }
                catch (Exception e) { _log.LogWarning($"Lobby mods panel: {e.Message}"); }
            }
        }

        private static void SetText(TMP_Text t, string s)
        {
            if (t != null && t.text != s) t.text = s ?? "";
        }

        private void FillLobbyPanel()
        {
            var host = Lobby.IsHost;
            if (_pickerMod != null && FillUnitPicker()) return;

            if (!Lobby.HostHasModSupport && !host)
            {
                Heading(_lobbyList, "Gameplay mods");
                InfoRow(_lobbyList, "The host has no mod support", "vanilla match");
                return;
            }

            Heading(_lobbyList, host ? "Gameplay mods: your pick for this match" : "Gameplay mods: the host's pick");
            if (Lobby.IsLadderLobby)
            {
                InfoRow(_lobbyList, "Ladder lobby: always vanilla", "no gameplay mods");
                return;
            }

            var status = InfoRow(_lobbyList, "", "");
            _lobbyUpdaters.Add(() =>
            {
                var (label, value) = StatusLine(host);
                SetText(status.label, label);
                SetText(status.value, value);
            });

            if (host)
            {
                var mods = ModCatalog.GameplayMods.ToList();
                if (mods.Count == 0) InfoRow(_lobbyList, "No gameplay mods installed", "SanctuaryMods\\<mod>\\mod.json");
                var selection = Lobby.Selection;
                foreach (var mod in mods)
                {
                    var m = mod;
                    var picked = selection.FirstOrDefault(s => s.Id == m.Id);
                    SwitchRow(_lobbyList, $"{m.Name} {m.Version}   <alpha=#80>{GameplayFiles(m)}", picked != null,
                        Lobby.CanChangeSelection, on => Lobby.SetSelected(m.Id, on));
                    if (picked != null) HostOptionRows(m);
                }
                foreach (var (file, owners) in Overlay.Conflicts(selection.Where(s => s.Local != null).Select(s => s.Local)))
                    InfoRow(_lobbyList, $"Both change {file}", $"{owners.Last().Name} wins");
            }
            else
            {
                foreach (var sel in Lobby.Selection)
                {
                    var s = sel;
                    var state = s.Local != null ? "you have it"
                        : s.LocalDifferent != null ? $"yours differs ({s.LocalDifferent.Version})"
                        : "missing" + (string.IsNullOrEmpty(s.Url) ? "" : ": " + s.Url);
                    InfoRow(_lobbyList, $"{s.Name} {s.Version}", state);
                    foreach (var option in s.OptionDefinitions)
                    {
                        var o = option;
                        var row = InfoRow(_lobbyList, OptionIndent + o.Label, "");
                        _lobbyUpdaters.Add(() => SetText(row.value, o.Display(CurrentOption(s.Id, o.Key))));
                        if (o.Type == ModOptionType.Units) PickerButton(s.Id, o, false);
                    }
                }
            }

            Line(_lobbyList);
            Heading(_lobbyList, "Players");
            foreach (var player in Lobby.Players)
            {
                var id = player.PlayerId;
                var row = InfoRow(_lobbyList, player.Name, "");
                _lobbyUpdaters.Add(() =>
                {
                    var p = Lobby.Players.FirstOrDefault(x => x.PlayerId == id);
                    if (p == null) return;
                    // A player re-checking a change that's still on its way
                    // keeps what they showed, rather than blinking "checking…".
                    if (p.State == PlayerModState.Pending && Lobby.Settling && !SettlingShown()) return;
                    SetText(row.value, PlayerText(p));
                });
            }
        }

        /// The line at the top of the panel: whether Start can go.
        private (string label, string value) StatusLine(bool host)
        {
            var blocked = Lobby.StartBlockedReason;
            if (Lobby.Selection.Count == 0) return ("None picked: a vanilla match, anyone can play", "");
            // A change on its way reads as ready for its first moment, and
            // as "updating" only if it takes longer.
            if (blocked == null || (Lobby.Settling && !SettlingShown())) return ("Everyone has them: ready to start", "");
            if (Lobby.Settling) return ("Updating everyone's copy…", host ? "Start still works" : "a moment");
            return ("Start waits until everyone has them", "see players below");
        }

        private static string PlayerText(PlayerModStatus p) =>
            p.State == PlayerModState.Ok ? (Lobby.Selection.Count == 0 ? "" : "has them all")
            : p.State == PlayerModState.Vanilla ? (Lobby.Selection.Count == 0 ? "vanilla" : "no mod support")
            : p.State == PlayerModState.Pending ? "checking…"
            : p.Detail;

        /// A picked mod's current value for one option, as everyone sees it.
        private static string CurrentOption(string modId, string key)
        {
            var s = Lobby.Selection.FirstOrDefault(x => x.Id == modId);
            return s != null && s.Options.TryGetValue(key, out var v) ? v : null;
        }

        private const string OptionIndent = "      ";

        /// A picked mod's options, under its switch: the host sets them
        /// here. Values go out once the host stops changing them for a
        /// moment, so a dragged slider sends one change, not fifty. Each
        /// control also follows the value if it changes some other way (a
        /// reset, a mod file edited), without a rebuild.
        private void HostOptionRows(ModInfo m)
        {
            var options = m.Manifest.Options;
            if (options.Count == 0) return;
            var editable = Lobby.CanChangeSelection;
            foreach (var option in options)
            {
                var o = option;
                string Value() => o.Normalize(CurrentOption(m.Id, o.Key));
                var v = Value();
                var label = OptionIndent + o.Label;
                if (!editable)
                {
                    var row = InfoRow(_lobbyList, label, "");
                    _lobbyUpdaters.Add(() => SetText(row.value, o.Display(Value())));
                    if (o.Type == ModOptionType.Units) PickerButton(m.Id, o, false);
                    continue;
                }
                switch (o.Type)
                {
                    case ModOptionType.Units:
                    {
                        var row = InfoRow(_lobbyList, label, "");
                        _lobbyUpdaters.Add(() => SetText(row.value, o.Display(Value())));
                        PickerButton(m.Id, o, true);
                        break;
                    }
                    case ModOptionType.Toggle:
                    {
                        var sw = SwitchRow(_lobbyList, label, v == "true", true, on => Lobby.SetOption(m.Id, o.Key, on ? "true" : "false"));
                        _lobbyUpdaters.Add(() =>
                        {
                            var on = Value() == "true";
                            if (sw.isOn == on) return;
                            sw.isOn = on;
                            sw.UpdateUI();
                        });
                        break;
                    }
                    case ModOptionType.Choice:
                    {
                        int IndexOf(string value) => Math.Max(0, o.Choices.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Value == value).i);
                        var selector = SelectorRow(_lobbyList, label, o.Choices.Select(c => c.Label).ToList(), IndexOf(v),
                            i => { if (i >= 0 && i < o.Choices.Count) Lobby.SetOption(m.Id, o.Key, o.Choices[i].Value); });
                        _lobbyUpdaters.Add(() =>
                        {
                            var i = IndexOf(Value());
                            if (selector.index == i) return;
                            selector.index = i;
                            selector.UpdateUI();
                        });
                        break;
                    }
                    default:
                    {
                        // Normalize hands back a number for a number option;
                        // anything else (an odd value from an older host)
                        // shows as 0, held to the range, not an exception.
                        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number);
                        if (o.Min.HasValue && o.Max.HasValue)
                        {
                            var (slider, box) = SliderRow(_lobbyList, label, (float)o.Min.Value, (float)o.Max.Value, (float)number, o.IsWhole,
                                f => Lobby.SetOption(m.Id, o.Key, f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                            _lobbyUpdaters.Add(() =>
                            {
                                if (!double.TryParse(Value(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return;
                                var now = (float)parsed;
                                if (Mathf.Approximately(slider.value, now)) return;
                                slider.SetValueWithoutNotify(now);
                                if (box != null && !box.isFocused) box.SetTextWithoutNotify(Value());
                            });
                        }
                        else
                        {
                            var typed = v;
                            TMP_InputField field = null;
                            field = TextRow(_lobbyList, label, v, s => typed = s, () =>
                            {
                                Lobby.SetOption(m.Id, o.Key, typed);
                                return o.Normalize(typed);
                            });
                            _lobbyUpdaters.Add(() =>
                            {
                                if (field == null || field.isFocused) return;
                                var now = Value();
                                if (field.text != now) { field.SetTextWithoutNotify(now); typed = now; }
                            });
                        }
                        break;
                    }
                }
            }
            ButtonRow(_lobbyList, $"Reset {m.Name} options", () => Lobby.ResetOptions(m.Id));
        }

        /// The option values in a line of chat: "Minutes 20, No air On".
        private static string OptionSummary(SelectedMod s)
        {
            if (s.Options.Count == 0) return "";
            var defs = s.OptionDefinitions;
            var parts = defs.Count > 0
                ? defs.Select(o => $"{o.Label} {o.Display(s.Options.TryGetValue(o.Key, out var v) ? v : null)}")
                : s.Options.Select(kv => $"{kv.Key} {kv.Value}");
            var list = parts.ToList();
            var text = string.Join(", ", list.Take(6));
            if (list.Count > 6) text += ", …";
            return " (" + text + ")";
        }

        private static string OptionDescription(ModOption o)
        {
            var text = o.Description.Length > 0 ? o.Description + " " : "";
            switch (o.Type)
            {
                case ModOptionType.Choice:
                    text += "One of: " + string.Join(", ", o.Choices.Select(c => c.Label)) + ".";
                    break;
                case ModOptionType.Number:
                    if (o.Min.HasValue || o.Max.HasValue)
                        text += $"From {(o.Min.HasValue ? ModOption.FormatNumber(o.Min.Value) : "any")} to {(o.Max.HasValue ? ModOption.FormatNumber(o.Max.Value) : "any")}";
                    if (o.Step.HasValue) text += $", in steps of {ModOption.FormatNumber(o.Step.Value)}";
                    text += ".";
                    break;
            }
            return text + $" Default: {o.Display(o.Default)}.";
        }

        /// A text box or a dragged slider in the lobby panel: rebuilding
        /// the panel now would tear it out from under the pointer.
        private bool LobbyPanelBusy()
        {
            if (Input.GetMouseButton(0)) return true;
            var events = UnityEngine.EventSystems.EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null || _lobbyPanel == null || !selected.transform.IsChildOf(_lobbyPanel.transform)) return false;
            var field = selected.GetComponent<TMP_InputField>();
            return field != null && field.isFocused;
        }

        private void DestroyLobbyPanel()
        {
            if (_lobbyPanel != null) Object.Destroy(_lobbyPanel);
            if (_lobbyButton != null) Object.Destroy(_lobbyButton.gameObject);
            _lobbyPanel = null;
            _lobbyButton = null;
            _lobbyList = null;
            _lobbyScroll = null;
            _lobbyFor = null;
            _lobbySignature = "";
            _lobbyStructure = "";
            _lobbyPanelKey = null;
            _lobbyUpdaters.Clear();
        }
    }
}
