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
        private string _gameplaySignature = "";

        private string GameplaySignature() =>
            $"{ModCatalog.Version}|{Lobby.ChangeCounter}|{string.Join(";", Modding.DefaultSelection)}|{Overlay.CurrentHash}";

        private void RebuildGameplayTab()
        {
            if (_gameList == null) return;
            _gameplaySignature = GameplaySignature();
            Clear(_gameList);

            Heading(_gameList, "Gameplay mods: the lobby host picks them");
            var live = Modding.ActiveGameplayMods;
            DescribeNext("Right now",
                "Outside a lobby the game always runs vanilla, so you can join anyone. In a lobby the host's pick is applied " +
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

            Heading(_gameList, "Picked when you host");
            foreach (var mod in mods)
            {
                var m = mod;
                DescribeNext(m.Name, GameplayDescription(m));
                SwitchRow(_gameList, $"{m.Name} {m.Version}   <alpha=#80>{GameplayFiles(m)}", _owner.IsDefault(m), true, on =>
                {
                    _owner.SetDefault(m, on);
                    _gameplaySignature = GameplaySignature();
                });
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

        private static string GameplayFiles(ModInfo m)
        {
            var parts = new List<string>();
            if (m.LuaCount > 0) parts.Add($"{m.LuaCount} lua");
            if (m.SantpCount > 0) parts.Add($"{m.SantpCount} santp");
            if (m.DllsAreGameplay && m.Dlls.Count > 0) parts.Add($"{m.Dlls.Count} dll");
            if (m.Manifest.Options.Count > 0) parts.Add(m.Manifest.Options.Count == 1 ? "1 option" : $"{m.Manifest.Options.Count} options");
            if (m.Manifest.Author.Length > 0) parts.Add("by " + m.Manifest.Author);
            return string.Join(", ", parts);
        }

        private static string GameplayDescription(ModInfo m)
        {
            var text = m.Manifest.Description.Length > 0 ? m.Manifest.Description + "\n\n" : "";
            text += $"Id {m.Id}, contents {m.ShortHash}. ";
            if (m.Manifest.Synthesised) text += "This folder has no mod.json, so its name stands in for one. ";
            if (m.Manifest.Requires.Count > 0) text += $"Needs {string.Join(", ", m.Manifest.Requires)} picked too. ";
            if (m.Manifest.Url.Length > 0) text += $"Get it from {m.Manifest.Url}. ";
            text += "Switched on here, it is picked straight away when you host a lobby; you can still change the pick there.";
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
        private string _announcedSelection;

        private void TickLobby()
        {
            var ui = LobbyInterface.Instance;
            var inLobby = ui != null && ui.isActiveAndEnabled && LobbyManager.IsInLobby && !Lobby.MatchUnderway;
            if (!inLobby)
            {
                if (_lobbyPanel != null) _lobbyPanel.SetActive(false);
                if (!LobbyManager.IsInLobby) _announcedSelection = null;
                return;
            }
            if (_tSwitchRow == null) return; // the page (and its templates) isn't built yet
            if (!ReferenceEquals(ui, _lobbyFor) || _lobbyButton == null) BuildLobbyButton(ui);

            AnnounceSelection(ui);

            var label = LobbyButtonLabel();
            if (label != _lobbyButtonText && _lobbyButton != null)
            {
                _lobbyButtonText = label;
                SetButtonText(_lobbyButton, label);
            }

            if (_lobbyPanel != null && _lobbyPanel.activeSelf)
            {
                var sig = LobbySignature();
                if (sig != _lobbySignature && !LobbyPanelBusy()) RebuildLobbyPanel();
            }
        }

        private static string LobbyButtonLabel()
        {
            var n = Lobby.Selection.Count;
            if (n == 0) return "Mods";
            return Lobby.StartBlockedReason != null ? $"Mods ({n}) !" : $"Mods ({n})";
        }

        private static string LobbySignature() =>
            $"{Lobby.ChangeCounter}|{ModCatalog.Version}|{Lobby.CanChangeSelection}|" +
            string.Join(",", Lobby.Players.Select(p => p.Name + p.State + p.Detail));

        /// A line in the lobby chat whenever the pick changes, so nobody
        /// misses it: players see what they're about to play.
        private void AnnounceSelection(LobbyInterface ui)
        {
            if (!Lobby.HostHasModSupport) return;
            var sel = Lobby.Selection;
            // The host's own values change the moment it touches a control;
            // everyone else's arrive settled. Wait for the host's to settle
            // too, so a dragged slider isn't a line of chat per step.
            if (Lobby.IsHost && Lobby.StartBlockedReason?.Contains("options changing") == true) return;
            var sig = string.Join(",", sel.Select(s => s.Id + "@" + s.ContentHash + OptionSummary(s)));
            if (sig == _announcedSelection) return;
            var first = _announcedSelection == null;
            _announcedSelection = sig;
            if (first && sel.Count == 0) return;
            ui.AddChatMessage(sel.Count == 0
                ? "Gameplay mods: none, vanilla match."
                : "Gameplay mods: " + string.Join(", ", sel.Select(s => $"{s.Name} {s.Version}".TrimEnd() + OptionSummary(s))) + ". Mods button for details.");
        }

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

            // A scroll view: mask, content with a vertical layout like the
            // settings lists, and a close button pinned below it.
            var view = new GameObject("View", typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect));
            var vrt = (RectTransform)view.transform;
            vrt.SetParent(brt, false);
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(40f, 140f);
            vrt.offsetMax = new Vector2(-40f, -40f);

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
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
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

        private void RebuildLobbyPanel()
        {
            if (_lobbyList == null) return;
            _lobbySignature = LobbySignature();
            Clear(_lobbyList);

            var host = Lobby.IsHost;
            var blocked = Lobby.StartBlockedReason;

            if (!Lobby.HostHasModSupport && !host)
            {
                Heading(_lobbyList, "Gameplay mods");
                InfoRow(_lobbyList, "The host has no mod support", "vanilla match");
                return;
            }

            Heading(_lobbyList, host ? "Gameplay mods: your pick for this match" : "Gameplay mods: the host's pick");
            InfoRow(_lobbyList,
                Lobby.Selection.Count == 0 ? "None picked: a vanilla match, anyone can play"
                    : blocked == null ? "Everyone has them: ready to start" : "Start waits until everyone has them",
                blocked == null ? "" : "see players below");

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
                    if (picked != null) HostOptionRows(m, picked.Options);
                }
                foreach (var (file, owners) in Overlay.Conflicts(Lobby.Selection.Where(s => s.Local != null).Select(s => s.Local)))
                    InfoRow(_lobbyList, $"Both change {file}", $"{owners.Last().Name} wins");
            }
            else
            {
                foreach (var s in Lobby.Selection)
                {
                    var state = s.Local != null ? "you have it"
                        : s.LocalDifferent != null ? $"yours differs ({s.LocalDifferent.Version})"
                        : "missing" + (string.IsNullOrEmpty(s.Url) ? "" : ": " + s.Url);
                    InfoRow(_lobbyList, $"{s.Name} {s.Version}", state);
                    foreach (var o in s.OptionDefinitions)
                    {
                        s.Options.TryGetValue(o.Key, out var v);
                        InfoRow(_lobbyList, OptionIndent + o.Label, o.Display(v));
                    }
                }
            }

            Line(_lobbyList);
            Heading(_lobbyList, "Players");
            foreach (var p in Lobby.Players)
            {
                var text = p.State == PlayerModState.Ok ? (Lobby.Selection.Count == 0 ? "" : "has them all")
                    : p.State == PlayerModState.Vanilla ? (Lobby.Selection.Count == 0 ? "vanilla" : "no mod support")
                    : p.State == PlayerModState.Pending ? "checking…"
                    : p.Detail;
                InfoRow(_lobbyList, p.Name, text);
            }
        }

        private const string OptionIndent = "      ";

        /// A picked mod's options, under its switch: the host sets them
        /// here. Values go out once the host stops changing them for a
        /// moment, so a dragged slider sends one change, not fifty.
        private void HostOptionRows(ModInfo m, IReadOnlyDictionary<string, string> values)
        {
            var options = m.Manifest.Options;
            if (options.Count == 0) return;
            var editable = Lobby.CanChangeSelection;
            foreach (var option in options)
            {
                var o = option;
                values.TryGetValue(o.Key, out var raw);
                var v = o.Normalize(raw);
                var label = OptionIndent + o.Label;
                if (!editable)
                {
                    InfoRow(_lobbyList, label, o.Display(v));
                    continue;
                }
                switch (o.Type)
                {
                    case ModOptionType.Toggle:
                        SwitchRow(_lobbyList, label, v == "true", true, on => Lobby.SetOption(m.Id, o.Key, on ? "true" : "false"));
                        break;
                    case ModOptionType.Choice:
                        var index = o.Choices.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Value == v).i;
                        SelectorRow(_lobbyList, label, o.Choices.Select(c => c.Label).ToList(), index,
                            i => { if (i >= 0 && i < o.Choices.Count) Lobby.SetOption(m.Id, o.Key, o.Choices[i].Value); });
                        break;
                    default:
                        var number = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                        if (o.Min.HasValue && o.Max.HasValue)
                        {
                            SliderRow(_lobbyList, label, (float)o.Min.Value, (float)o.Max.Value, (float)number, o.IsWhole,
                                f => Lobby.SetOption(m.Id, o.Key, f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                        }
                        else
                        {
                            var typed = v;
                            TextRow(_lobbyList, label, v, s => typed = s, () =>
                            {
                                Lobby.SetOption(m.Id, o.Key, typed);
                                return o.Normalize(typed);
                            });
                        }
                        break;
                }
            }
            if (options.Any(o => o.Normalize(values.TryGetValue(o.Key, out var x) ? x : null) != o.Default))
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
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
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
            _lobbyFor = null;
            _lobbySignature = "";
        }
    }
}
