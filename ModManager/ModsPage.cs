using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EM.UI;
using HarmonyLib;
using Michsky.UI.Beam;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SanctuaryHud
{
    // The manager's UI: a "Mods" entry in the front menu's sidebar that opens
    // a full page built from the game's own Settings screen. Nothing here is
    // drawn from scratch — the page is a clone of SettingsInterface with its
    // content replaced, and every row is a clone of one of that screen's rows
    // (a switch row, a slider row with just its text input kept, the section
    // heading, the buttons), so it matches the game exactly and follows any
    // restyling the game does.
    //
    // The page lives in the menu canvas. That canvas survives into a match
    // (the pause menu's Settings button opens the same Settings screen
    // there), so the hotkey opens the page full-screen mid-match too, over
    // the menu background, and closing it returns to whatever was showing.
    internal sealed class ModsPage
    {
        private const string HarmonyId = "com.sanctuarydb.modmanager.page";
        private static ModsPage _current;

        private readonly ModManagerPlugin _owner;
        private readonly BepInEx.Logging.ManualLogSource _log;
        private Harmony _harmony;
        private Sprite _icon, _cover;

        // Since 0.0.1.20 the front menu is a side bar shown beside each
        // screen (SideBarInterface) rather than a MainMenuInterface page of
        // its own; the page is built once per side bar, i.e. per menu scene.
        private SideBarInterface _builtFor;
        private SideBarInterface _failedFor;   // the side bar a build failed on; not retried
        private GameObject _page;
        private GameObject _sidebarButton;
        private Transform _templates;
        private Transform _uiList, _luaList;
        private string _pluginSignature = "";
        private bool _sidebarRegistered;

        // The InterfaceManager window that was up when the page opened
        // (Home, Play or Replays in the front menu, None or InGameMenu during
        // a match), restored on close.
        private InterfaceManager.Window _returnWindow = InterfaceManager.Window.Home;
        private static readonly AccessTools.FieldRef<InterfaceManager, InterfaceManager.Window> CurrentWindow =
            AccessTools.FieldRefAccess<InterfaceManager, InterfaceManager.Window>("currentWindow");
        private static readonly AccessTools.FieldRef<InterfaceManager, InterfaceManager.Window> ReturnWindow =
            AccessTools.FieldRefAccess<InterfaceManager, InterfaceManager.Window>("returnWindow");

        // Templates lifted out of the cloned Settings screen before its
        // lists are emptied. They sit under an inactive holder so a clone can
        // be configured before its Awake runs (Awake fires on reparenting
        // into the live list).
        private GameObject _tHeading, _tLine, _tSpacer, _tSwitchRow, _tTextRow, _tButtonRow, _tSliderRow, _tSelectorRow;

        // Per-plugin settings group, so toggling one plugin rebuilds only its
        // own rows and the switch just clicked keeps its animation.
        private readonly Dictionary<string, Transform> _pluginGroups = new Dictionary<string, Transform>();
        private readonly Dictionary<string, TMP_Text> _sectionLabels = new Dictionary<string, TMP_Text>();

        // Mods whose settings are unfolded; everything starts folded so the
        // tab is one row per mod until you open the one you want.
        private readonly HashSet<string> _expanded = new HashSet<string>();

        public ModsPage(ModManagerPlugin owner, BepInEx.Logging.ManualLogSource log)
        {
            _owner = owner;
            _log = log;
            _current = this;
        }

        /// Ours is the menu's current screen. Not the page's activeSelf: the
        /// PanelManager keeps a panel active through its out-animation.
        public bool IsOpen => _page != null && _open;
        private bool _open;

        private const string PanelName = "Mods";
        private PanelManager _registeredIn;

        /// True while a side-bar screen of the front menu (Settings included)
        /// is showing, during a match (pause menu included), or while our
        /// page is up. Not from the lobby or loading screens: the page would
        /// replace them, and they are not ours to restore.
        public bool CanOpen
        {
            get
            {
                if (IsOpen) return true;
                var im = InterfaceManager.Instance;
                if (_page == null || im == null) return false;
                switch (CurrentWindow(im))
                {
                    case InterfaceManager.Window.Home:
                    case InterfaceManager.Window.Play:
                    case InterfaceManager.Window.Replays:
                    case InterfaceManager.Window.Settings:
                        return true;
                    case InterfaceManager.Window.InGameMenu:
                        return SanctuaryUI.SanctuaryUIManager.Instance != null;
                    default:
                        return InMatch;
                }
            }
        }

        /// The game hands the menu canvas over to the match by transitioning
        /// to Window.None once the map has loaded; the in-game UI manager
        /// only exists during a match.
        private static bool InMatch
        {
            get
            {
                var im = InterfaceManager.Instance;
                return im != null && CurrentWindow(im) == InterfaceManager.Window.None
                       && SanctuaryUI.SanctuaryUIManager.Instance != null;
            }
        }

        // ---- lifecycle ------------------------------------------------------

        /// Called every frame by the plugin. Builds the page once the menu
        /// exists (and again if the menu scene was recreated), keeps the
        /// plugin list fresh while the page is open.
        public void Tick()
        {
            var bar = SideBarInterface.Instance;
            if (bar == null) return;
            if ((!ReferenceEquals(bar, _builtFor) || _page == null) && !ReferenceEquals(bar, _failedFor))
            {
                try { Build(bar); }
                catch (Exception e)
                {
                    _log.LogError($"Mods page could not be built: {e}");
                    // Nothing half-built left to open, and no retry every
                    // frame: the next menu (a new scene) gets another go.
                    Unregister();
                    if (_page != null) Object.Destroy(_page);
                    if (_sidebarButton != null) Object.Destroy(_sidebarButton);
                    _page = null;
                    _sidebarButton = null;
                    _failedFor = bar;
                    return;
                }
            }

            if (!_sidebarRegistered && _sidebarButton != null && _sidebarButton.activeInHierarchy)
            {
                // The sidebar dims the other buttons while one is hovered
                // and re-lights them afterwards; FetchButtons only takes
                // active buttons, so it has to run once the menu is up.
                var pb = _sidebarButton.GetComponent<PanelButton>();
                ForceNormal(pb);
                _sidebarButton.GetComponentInParent<PanelButtonDimmer>()?.FetchButtons();
                _sidebarRegistered = true;
            }

            if (IsOpen)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (PluginSignature() != _pluginSignature) RebuildUiTab();
            }
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        public void Open()
        {
            if (_page == null) return;
            var im = InterfaceManager.Instance;
            var pm = im != null ? im.GetComponent<PanelManager>() : null;
            if (pm == null) return;
            // From Settings, back goes where Settings' own back would have.
            var from = CurrentWindow(im);
            _returnWindow = from == InterfaceManager.Window.Settings ? ReturnWindow(im) : from;
            // Front menu or match, by the screen we go back to: the game's UI
            // manager outlives a match, so its Instance can't tell them apart.
            var frontMenu = _returnWindow == InterfaceManager.Window.Home || _returnWindow == InterfaceManager.Window.Play
                            || _returnWindow == InterfaceManager.Window.Replays;
            // Since 0.0.1.20 every screen is a panel of the InterfaceManager's
            // PanelManager, animated in and out by name, and ours is one of
            // them. currentWindow goes to Background, a screen the game never
            // stays on, so its next TransitionTo (a side bar button, a lobby
            // invite, escape in a match) always goes through and animates our
            // panel out like any other.
            //
            // Outside a match only currentWindow moves: the side bar stays
            // beside the page, as beside Settings, where a real TransitionTo
            // would play its hide animation (and a Show() in the same frame
            // does not undo that). In a match the real one is wanted: it
            // hides the side bar and puts up the backdrop over the game.
            if (frontMenu) CurrentWindow(im) = InterfaceManager.Window.Background;
            else im.TransitionTo(InterfaceManager.Window.Background);
            RebuildUiTab();
            RebuildLuaTab();
            pm.OpenPanel(PanelName);
            _open = true;
        }

        public void Close()
        {
            if (!_open) return;
            // TransitionTo opens the screen we came from, which animates ours
            // out; the prefix clears _open.
            var im = InterfaceManager.Instance;
            if (im != null) im.TransitionTo(_returnWindow);
            _open = false;
        }

        public void Destroy()
        {
            // A hot reload while the page is up would otherwise leave the
            // menu on an empty screen.
            if (IsOpen) Close();
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            Unregister();
            if (_page != null) Object.Destroy(_page);
            if (_sidebarButton != null) Object.Destroy(_sidebarButton);
            if (_icon != null) { Object.Destroy(_icon.texture); Object.Destroy(_icon); }
            if (_cover != null) { Object.Destroy(_cover.texture); Object.Destroy(_cover); }
            _page = null;
            _sidebarButton = null;
            _icon = null;
            _cover = null;
            _builtFor = null;
            if (ReferenceEquals(_current, this)) _current = null;
        }

        // Any screen change (a side bar button, a lobby invite, a match
        // starting) takes our panel out: the PanelManager animates it away,
        // and this only has to note that it is no longer open.
        private static void TransitionPrefix()
        {
            if (_current != null) _current._open = false;
        }

        /// Takes our panel back out of the menu's PanelManager, so it never
        /// holds a destroyed Animator. Ours is always appended last, and is
        /// never the current panel by the time this runs, so no index moves.
        private void Unregister()
        {
            try
            {
                var pm = _registeredIn;
                _registeredIn = null;
                if (pm == null) return;
                var at = pm.panels.FindIndex(p => p.panelName == PanelName);
                if (at >= 0 && at != pm.currentPanelIndex) pm.panels.RemoveAt(at);
            }
            catch (Exception e) { _log.LogWarning($"Mods page: could not unregister its panel: {e.Message}"); }
        }

        private string PluginSignature() =>
            string.Join(";", _owner.Plugins.Select(p => p.Guid + (p.Enabled ? "+" : "-")));

        // ---- construction ---------------------------------------------------

        private void Build(SideBarInterface bar)
        {
            _builtFor = bar;
            _pluginGroups.Clear();
            _open = false;
            Unregister();
            if (_page != null) Object.Destroy(_page);
            if (_sidebarButton != null) Object.Destroy(_sidebarButton);

            // The Settings screen as the menu's PanelManager holds it: the
            // Animator it plays In/Out on, with SettingsInterface on it or
            // inside it.
            var menu = InterfaceManager.Instance?.GetComponent<PanelManager>()
                       ?? throw new InvalidOperationException("The menu's PanelManager was not found.");
            var settingsItem = menu.panels.Find(p => p.panelName == InterfaceManager.Window.Settings.ToString())
                               ?? throw new InvalidOperationException("The menu has no Settings panel.");
            var settings = settingsItem.panelObject.gameObject;
            var root = settings.transform.parent; // the InterfaceManager's list of screens

            if (_harmony == null)
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(AccessTools.Method(typeof(InterfaceManager), nameof(InterfaceManager.TransitionTo)),
                    prefix: new HarmonyMethod(typeof(ModsPage), nameof(TransitionPrefix)));
            }
            if (_icon == null) _icon = MakeIcon();
            if (_cover == null) _cover = MakeCover();

            // -- the page: a clone of the Settings screen, kept inactive
            //    while it is rearranged so no Awake sees the half-built state.
            //    It is cloned under an inactive holder, since cloning the live
            //    screen straight into the menu runs every Awake at once: the
            //    resolution Dropdown's then throws, re-adding the Canvas its
            //    original's Awake already added. That row is cleared below.
            var holder = new GameObject("ModsInterface Holder");
            holder.SetActive(false);
            try
            {
                _page = Object.Instantiate(settings, holder.transform, false);
                _page.SetActive(false);
                _page.transform.SetParent(root, false);
            }
            finally { Object.DestroyImmediate(holder); }
            // Right after Settings, not last: the screens draw under the side
            // bar, and each one's full-screen backdrop would hide it.
            _page.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
            _page.name = "ModsInterface";
            var settingsClone = _page.GetComponentInChildren<SanctuaryUI.SettingsInterface>(true)
                                ?? throw new InvalidOperationException("The Settings panel has no SettingsInterface.");
            var screen = settingsClone.transform;
            Object.DestroyImmediate(settingsClone);

            var content = screen.Find("Content");
            var categories = content.Find("Categories");
            var panels = (RectTransform)content.Find("Panels");
            var buttons = (RectTransform)content.Find("Buttons");

            // The screen's title badge reads MODS, and the description
            // column stays, as on the Settings screen: it shows what the page
            // is for, and whichever mod or setting the pointer is over.
            RetitleScreen(screen, content, "Mods");
            SetUpDescription(content.Find("Description Area"));

            _templates = new GameObject("Templates", typeof(RectTransform)).transform;
            _templates.SetParent(_page.transform, false);
            _templates.gameObject.SetActive(false);

            // Tabs: Graphics becomes UI Mods, Controls becomes Lua Mods; the rest go.
            var uiTab = categories.Find("Graphics").GetComponent<PanelButton>();
            var luaTab = categories.Find("Controls").GetComponent<PanelButton>();
            Object.DestroyImmediate(categories.Find("General").gameObject);
            Object.DestroyImmediate(categories.Find("Audio").gameObject);
            RenamePanelButton(uiTab, "UI Mods", _icon);
            RenamePanelButton(luaTab, "Lua Mods", FindSprite("General (64x)"));

            var uiPanel = panels.Find("Graphics");
            var luaPanel = panels.Find("Controls");
            Object.DestroyImmediate(panels.Find("General").gameObject);
            Object.DestroyImmediate(panels.Find("Audio").gameObject);
            uiPanel.name = "UI Mods";
            luaPanel.name = "Lua Mods";
            _uiList = uiPanel.Find("Content/List/Layout Group");
            _luaList = luaPanel.Find("Content/List/Layout Group");

            // Lift the row templates out before emptying the lists.
            _tHeading = TakeTemplate(_uiList, "Display Header", "Heading");
            _tLine = TakeTemplate(_uiList, "Line", "Line");
            _tSpacer = TakeTemplate(_uiList, "Spacer", "Spacer");
            _tTextRow = TakeTemplate(_uiList, "UI Scale", "TextRow");
            _tButtonRow = TakeTemplate(_uiList, "ApplyButton", "ButtonRow");
            _tSelectorRow = TakeTemplate(_uiList, "Window Mode", "SelectorRow");
            PrepareSelectorTemplate(_tSelectorRow);
            _tSwitchRow = TakeTemplate(_luaList, "EdgePanToggle", "SwitchRow");
            // The slider row is a second copy of UI Scale, taken before the
            // text template strips the slider out of the first.
            _tSliderRow = Object.Instantiate(_tTextRow, _templates);
            _tSliderRow.name = "SliderRow";
            PrepareSwitchTemplate(_tSwitchRow);
            PrepareSliderTemplate(_tSliderRow);
            PrepareTextTemplate(_tTextRow);
            PrepareButtonTemplate(_tButtonRow);
            Clear(_uiList);
            Clear(_luaList);

            var pm = screen.GetComponent<PanelManager>();
            pm.panels = new List<PanelManager.PanelItem>
            {
                new PanelManager.PanelItem { panelName = "UI Mods", panelObject = uiPanel.GetComponent<Animator>(), panelButton = uiTab },
                new PanelManager.PanelItem { panelName = "Lua Mods", panelObject = luaPanel.GetComponent<Animator>(), panelButton = luaTab },
            };
            pm.currentPanelIndex = 0;

            // Bottom buttons: Back stays; the reset button becomes "Open mods
            // folder" and gains a "Rescan" sibling in a right-aligned row.
            var back = buttons.Find("BackButton").GetComponent<ButtonManager>();
            back.onClick.AddListener(Close);
            var open = buttons.Find("Reset Settings Button").GetComponent<ButtonManager>();
            var rescan = Object.Instantiate(open.gameObject, buttons).GetComponent<ButtonManager>();
            var right = new GameObject("Right", typeof(RectTransform)).GetComponent<RectTransform>();
            right.SetParent(buttons, false);
            right.anchorMin = right.anchorMax = new Vector2(1f, 0f);
            right.pivot = new Vector2(1f, 0f);
            right.anchoredPosition = Vector2.zero;
            var hl = right.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 15f;
            hl.childControlWidth = hl.childControlHeight = false;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;
            hl.childAlignment = TextAnchor.LowerRight;
            var fit = right.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rescan.transform.SetParent(right, false);
            open.transform.SetParent(right, false);
            foreach (var b in new[] { rescan, open })
            {
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = Vector2.zero;
            }
            SetButtonText(rescan, "Rescan");
            SetButtonText(open, "Open Mods Folder");
            rescan.onClick.AddListener(() => { _owner.Rescan(); RebuildLuaTab(); RebuildUiTab(); });
            open.onClick.AddListener(_owner.OpenModsFolder);

            // -- the sidebar entry: a clone of the Settings button, right after it.
            var settingsButton = bar.settingsButton?.transform
                                 ?? throw new InvalidOperationException("The side bar has no Settings button.");
            _sidebarButton = Object.Instantiate(settingsButton.gameObject, settingsButton.parent);
            _sidebarButton.name = "Mods";
            _sidebarButton.transform.SetSiblingIndex(settingsButton.GetSiblingIndex() + 1);
            var pb = _sidebarButton.GetComponent<PanelButton>();
            RenamePanelButton(pb, "Mods", _icon);
            pb.onClick.AddListener(Open);
            // At start-up the game holds its Settings button disabled until
            // the menu is ready, then re-enables it by reference — which a
            // clone taken in that window never gets. So the clone is put in
            // the normal state here, and registered with the sidebar's
            // hover-dimming once the menu is actually showing (see Tick).
            ForceNormal(pb);
            _sidebarRegistered = false;

            // -- one more screen of the menu. Appended, so the game's own
            //    panels keep their indices; culled (inactive) until opened.
            // With our side bar button as its button, the PanelManager lights
            // it (and moves the side bar's indicator line to it) while the
            // page is up, and puts it back when another screen opens.
            menu.panels.Add(new PanelManager.PanelItem
            {
                panelName = PanelName,
                panelButton = pb,
                panelObject = _page.GetComponent<Animator>()
                              ?? throw new InvalidOperationException("The cloned Settings panel has no Animator."),
            });
            _registeredIn = menu;

            _pluginSignature = "";
            _log.LogInfo("Mods page built into the front menu.");
        }

        /// The screen's own title ("Settings" in its badge), outside the
        /// description column, renamed. Its localisation goes first or it
        /// would write the old word back.
        private void RetitleScreen(Transform screen, Transform content, string title)
        {
            var description = content.Find("Description Area");
            var found = false;
            foreach (var tmp in screen.GetComponentsInChildren<TMP_Text>(true))
            {
                if (description != null && tmp.transform.IsChildOf(description)) continue;
                if (!string.Equals(tmp.text?.Trim(), "Settings", StringComparison.OrdinalIgnoreCase)) continue;
                var loc = tmp.GetComponent<LocalizedObject>();
                if (loc != null) Object.DestroyImmediate(loc);
                tmp.text = title;
                found = true;
            }
            if (!found) _log.LogWarning("Mods page: the Settings title was not found; the badge keeps its text.");
        }

        private SettingsDescriptionManager _description;
        private string _pendingTitle, _pendingText;

        private const string PageDescription =
            "Switch mods on and off, and change their settings. Click a mod to show its settings. " +
            "UI mods run on this PC only; Lua mods change the game's own scripts, so everyone in a lobby needs the same ones.";

        /// The description column, with our text as its resting state and
        /// the mod's icon as its picture. Rows fill it on hover (see Place).
        private void SetUpDescription(Transform area)
        {
            _description = area != null ? area.GetComponentInChildren<SettingsDescriptionManager>(true) : null;
            if (_description == null)
            {
                _log.LogWarning("Mods page: the Settings description column was not found; rows show no descriptions.");
                return;
            }
            foreach (var loc in area.GetComponentsInChildren<LocalizedObject>(true)) Object.DestroyImmediate(loc);
            var t = Traverse.Create(_description);
            t.Field("title").SetValue("Mods");
            t.Field("description").SetValue(PageDescription);
            t.Field("titleKey").SetValue("");
            t.Field("descriptionKey").SetValue("");
            t.Field("cover").SetValue(_cover);
            _description.useLocalization = false;
            _description.localizedObject = null;
            var cover = t.Field("coverImage").GetValue<Image>();
            if (cover != null) cover.preserveAspect = true;
        }

        /// Rows describe themselves in the description column while the
        /// pointer is over them, through the row's own SettingsElement hover
        /// events, the way the Settings screen's rows do.
        private void Describe(GameObject row, string title, string text)
        {
            if (_description == null || string.IsNullOrEmpty(text)) return;
            var element = row.GetComponent<SettingsElement>();
            if (element == null) return;
            var d = _description;
            element.onHover.AddListener(() => d.UpdateUI(title, text, null));
            element.onLeave.AddListener(d.SetDefault);
        }

        private GameObject TakeTemplate(Transform list, string childName, string newName)
        {
            var t = list.Find(childName) ?? throw new InvalidOperationException($"Settings row '{childName}' not found.");
            t.SetParent(_templates, false);
            t.name = newName;
            return t.gameObject;
        }

        private static void Clear(Transform list)
        {
            for (var i = list.childCount - 1; i >= 0; i--) Object.DestroyImmediate(list.GetChild(i).gameObject);
        }

        /// The switch row: drop the game's description hook and the
        /// localisation that would overwrite our label.
        private static void PrepareSwitchTemplate(GameObject row)
        {
            Object.DestroyImmediate(row.GetComponent<SettingsDescription>());
            var text = row.transform.Find("Text");
            Object.DestroyImmediate(text.GetComponent<LocalizedObject>());
            var sw = row.transform.Find("Switch").GetComponent<SwitchManager>();
            sw.saveValue = false;
            sw.invokeOnEnable = false;
        }

        /// The slider row becomes a text row: the slider goes, its text
        /// input stays and grows to the whole control width (a TMP input
        /// field scrolls with the caret, so long values are reachable).
        private static void PrepareTextTemplate(GameObject row)
        {
            const float inputWidth = 420f;
            Object.DestroyImmediate(row.GetComponent<SettingsDescription>());
            Object.DestroyImmediate(row.GetComponent<SliderInputHandler>());
            var slider = row.transform.Find("Slider");
            slider.name = "Input";
            Object.DestroyImmediate(slider.GetComponent<SliderManager>());
            Object.DestroyImmediate(slider.GetComponent<Slider>());
            foreach (var gone in new[] { "Fill Area", "Handle Slide Area", "Indicator" })
            {
                var c = slider.Find(gone);
                if (c != null) Object.DestroyImmediate(c.gameObject);
            }
            var sliderRect = (RectTransform)slider;
            sliderRect.sizeDelta = new Vector2(inputWidth, sliderRect.sizeDelta.y);
            var label = (RectTransform)row.transform.Find("Text");
            label.sizeDelta = new Vector2(-(inputWidth + 60f), label.sizeDelta.y);

            var input = slider.Find("Text Input");
            Object.DestroyImmediate(input.GetComponent<SliderInput>());
            var inputRect = (RectTransform)input;
            inputRect.anchorMin = Vector2.zero;
            inputRect.anchorMax = Vector2.one;
            inputRect.pivot = new Vector2(0.5f, 0.5f);
            inputRect.sizeDelta = Vector2.zero;
            inputRect.anchoredPosition = Vector2.zero;
            input.SetAsLastSibling(); // above the static frame, not under it
            var bg = input.Find("Background")?.GetComponent<Image>();
            if (bg != null) bg.enabled = false; // the frame behind shows through
            var field = input.GetComponent<TMP_InputField>();
            field.contentType = TMP_InputField.ContentType.Standard;
            field.characterLimit = 0;
            field.lineType = TMP_InputField.LineType.SingleLine;
            foreach (var tmp in input.GetComponentsInChildren<TMP_Text>(true))
            {
                tmp.fontStyle = FontStyles.Normal;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.characterSpacing = 0f;
                tmp.margin = new Vector4(12f, 0f, 12f, 0f);
                tmp.overflowMode = TextOverflowModes.Overflow;
            }
        }

        /// The slider row stays a slider: the game's settings binding goes,
        /// the Beam slider and its value box stay. The box is driven here
        /// (SliderInput, which synced it to the game's own setting, goes
        /// too), so a typed number moves the slider and vice versa.
        private static void PrepareSliderTemplate(GameObject row)
        {
            Object.DestroyImmediate(row.GetComponent<SettingsDescription>());
            Object.DestroyImmediate(row.GetComponent<SliderInputHandler>());
            var slider = row.transform.Find("Slider");
            var sm = slider.GetComponent<SliderManager>();
            sm.saveValue = false;
            sm.invokeOnEnable = false;
            sm.useRoundValue = true;
            sm.usePercent = false;
            var input = slider.Find("Text Input");
            if (input != null)
            {
                Object.DestroyImmediate(input.GetComponent<SliderInput>());
                var field = input.GetComponent<TMP_InputField>();
                field.contentType = TMP_InputField.ContentType.DecimalNumber;
            }
        }

        /// The selector row (the game's Window Mode row: a label and a
        /// left/right chooser, which is how the settings screen offers a
        /// fixed list) with the game's setting binding removed. Items are
        /// filled per row.
        private static void PrepareSelectorTemplate(GameObject row)
        {
            Object.DestroyImmediate(row.GetComponent<SettingsDescription>());
            Object.DestroyImmediate(row.GetComponent<SelectorInputHandler>());
            var text = row.transform.Find("Text");
            if (text != null) Object.DestroyImmediate(text.GetComponent<LocalizedObject>());
            var selector = row.GetComponentInChildren<HorizontalSelector>(true);
            Object.DestroyImmediate(selector.GetComponent<LocalizedObject>());
            selector.saveSelected = false;
            selector.invokeOnAwake = false;
            selector.useLocalization = false;
            selector.loopSelection = true;
            selector.items.Clear();
        }

        /// A fixed choice: the game's own left/right selector. Options are
        /// shown as given; `selected` is the index shown first.
        private void SelectorRow(Transform list, string label, IList<string> options, int selected, Action<int> onChanged)
        {
            var go = Spawn(_tSelectorRow);
            go.transform.Find("Text").GetComponent<TMP_Text>().text = label;
            var selector = go.GetComponentInChildren<HorizontalSelector>(true);
            selector.items.Clear();
            foreach (var o in options) selector.items.Add(new HorizontalSelector.Item { itemTitle = o });
            selector.defaultIndex = Mathf.Clamp(selected, 0, Math.Max(0, options.Count - 1));
            selector.index = selector.defaultIndex;
            selector.onValueChanged.AddListener(i => onChanged(i));
            // Awake runs on placement (the templates sit inactive) and
            // initialises from the items above; a second pass is harmless
            // and covers a template that was already awake.
            Place(go, list);
            selector.InitializeSelector();
        }

        private static void PrepareButtonTemplate(GameObject row)
        {
            var bm = row.GetComponent<ButtonManager>();
            bm.useLocalization = false;
            bm.isInteractable = true;
        }

        private static void RenamePanelButton(PanelButton pb, string text, Sprite icon)
        {
            pb.useLocalization = false;
            pb.useCustomText = false;
            pb.buttonText = text;
            if (icon != null)
            {
                // UpdateUI re-applies these to every state's image, so the
                // images themselves are not the place to set a sprite.
                pb.buttonIcon = icon;
                pb.selectedIcon = icon;
            }
            if (pb.gameObject.activeInHierarchy) pb.UpdateUI();
        }

        /// Interactable, unselected, showing its Normal state — whatever
        /// state the source button was cloned in.
        private static void ForceNormal(PanelButton pb)
        {
            pb.isInteractable = true;
            pb.isSelected = false;
            foreach (var cg in pb.GetComponentsInChildren<CanvasGroup>(true))
            {
                if (cg.transform.parent != pb.transform) continue;
                cg.alpha = cg.name == "Normal" ? 1f : 0f;
            }
        }

        private static void SetButtonText(ButtonManager bm, string text)
        {
            bm.useLocalization = false;
            bm.buttonText = text;
            if (bm.gameObject.activeInHierarchy) bm.UpdateUI();
        }

        private static Sprite FindSprite(string name) =>
            Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);

        /// The sidebar's icons are 64px white line drawings tinted by the
        /// game, so ours is drawn the same way: a wireframe cube ("package"),
        /// rasterised from line segments with an anti-aliased edge.
        // The game's icons are ~6px lines at 64px: a half-width of 3.
        private static Sprite MakeIcon() => DrawCube("Mods (64x)", 64, 25f, 3f, new Color32(255, 255, 255, 255), 0f);

        /// The description column's picture: the same cube, drawn at the
        /// size and weight of the Settings screen's hex, in the menu's accent
        /// blue with a soft glow, rather than the side bar icon blown up.
        private static Sprite MakeCover() => DrawCube("Mods cover", 512, 78f, 13f, new Color32(0x3D, 0xAF, 0xFF, 255), 0.45f);

        /// A cube (hexagon with three spokes) as a distance field: `stroke`
        /// is the half-width of the lines, with a 1px anti-aliased edge, and
        /// `glow` the strength of a halo fading out from them.
        private static Sprite DrawCube(string name, int size, float r, float stroke, Color32 ink, float glow)
        {
            var c = new Vector2(size / 2f, size / 2f);
            Vector2 V(float deg) => c + new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * r;
            var segs = new List<(Vector2 a, Vector2 b)>();
            for (var i = 0; i < 6; i++) segs.Add((V(30 + 60 * i), V(30 + 60 * (i + 1))));
            foreach (var deg in new[] { 30f, 150f, 270f }) segs.Add((c, V(deg)));
            var glowReach = r * 0.35f;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var d = float.MaxValue;
                foreach (var (a, b) in segs)
                {
                    var ab = b - a;
                    var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    d = Mathf.Min(d, (p - (a + ab * t)).magnitude);
                }
                var alpha = Mathf.Clamp01(stroke + 0.5f - d); // 1px anti-aliased edge
                if (glow > 0f && d > stroke)
                {
                    var halo = Mathf.Exp(-(d - stroke) / glowReach * 2.5f) * glow;
                    alpha = Mathf.Max(alpha, halo);
                }
                px[y * size + x] = new Color32(ink.r, ink.g, ink.b, (byte)(alpha * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        // ---- rows -----------------------------------------------------------

        /// Instantiated under the inactive holder, configured by the caller,
        /// then moved into the list, at which point Awake runs.
        private GameObject Spawn(GameObject template)
        {
            var go = Object.Instantiate(template, _templates);
            go.name = template.name;
            return go;
        }

        /// Into the live list, taking the description set up for it by the
        /// caller (a setting's config description, a mod's summary).
        private void Place(GameObject go, Transform list)
        {
            if (_pendingText != null)
            {
                Describe(go, _pendingTitle, _pendingText);
                _pendingTitle = _pendingText = null;
            }
            go.transform.SetParent(list, false);
        }

        /// The next row placed describes itself with this.
        private void DescribeNext(string title, string text)
        {
            _pendingTitle = title;
            _pendingText = string.IsNullOrEmpty(text) ? null : text;
        }

        private void Heading(Transform list, string text)
        {
            var go = Spawn(_tHeading);
            go.GetComponent<TMP_Text>().text = text;
            Place(go, list);
        }

        private void Line(Transform list)
        {
            Place(Spawn(_tLine), list);
            Place(Spawn(_tSpacer), list);
        }

        private void SwitchRow(Transform list, string label, bool isOn, bool interactable, Action<bool> onChanged)
        {
            var go = Spawn(_tSwitchRow);
            go.transform.Find("Text").GetComponent<TMP_Text>().text = label;
            var sw = go.transform.Find("Switch").GetComponent<SwitchManager>();
            sw.isOn = isOn;
            sw.isInteractable = interactable;
            sw.onValueChanged.AddListener(v => onChanged(v));
            Place(go, list);
        }

        /// A mod's section header: the switch row restyled as a heading,
        /// with the on/off switch inline and the rest of the row a button
        /// that folds the mod's settings away or back. The switch is a
        /// child button, so a click on it does not reach the row.
        private TMP_Text SectionRow(Transform list, string name, bool isOn, bool expanded,
            Action<bool> onChanged, Func<bool> onToggleExpand)
        {
            var go = Spawn(_tSwitchRow);
            go.name = "Section " + name;
            var text = go.transform.Find("Text");
            var tmp = text.GetComponent<TMP_Text>();
            tmp.fontSize *= 1.25f;
            var um = text.GetComponent<UIManagerText>();
            if (um != null) um.fontType = UIManagerText.FontType.Semibold;
            else tmp.fontStyle = FontStyles.Bold;
            tmp.text = SectionLabel(name, expanded);

            var sw = go.transform.Find("Switch").GetComponent<SwitchManager>();
            sw.isOn = isOn;
            sw.isInteractable = true;
            sw.onValueChanged.AddListener(v => onChanged(v));

            // The click target: the row's SettingsElement, the Beam widget
            // that takes clicks anywhere on a settings row (with hover
            // highlight and sound). Its inspector onClick flips the switch,
            // so the event is replaced wholesale, persistent listeners
            // included, and the row folds instead. The switch still toggles
            // through its own pointer handler, which the row never sees.
            var element = go.GetComponent<SettingsElement>();
            if (element != null)
            {
                element.onClick = new UnityEngine.Events.UnityEvent();
                element.onClick.AddListener(() => tmp.text = SectionLabel(name, onToggleExpand()));
            }
            else _log.LogWarning($"Section '{name}': the row has no SettingsElement, so it cannot fold.");
            Place(go, list);
            return tmp;
        }

        private static string SectionLabel(string name, bool expanded) =>
            (expanded ? "-  " : "+  ") + name; // TMP has no closing alpha tag, so no dimming here

        /// A switch row without the switch: a label with an optional value
        /// on the right.
        private void InfoRow(Transform list, string label, string value = null)
        {
            var go = Spawn(_tSwitchRow);
            var text = go.transform.Find("Text");
            text.GetComponent<TMP_Text>().text = label;
            Object.DestroyImmediate(go.transform.Find("Switch").gameObject);
            if (!string.IsNullOrEmpty(value))
            {
                var v = Object.Instantiate(text.gameObject, go.transform);
                v.name = "Value";
                var rt = (RectTransform)v.transform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.anchoredPosition = new Vector2(-20f, 0f);
                rt.sizeDelta = new Vector2(-40f, 0f);
                var tmp = v.GetComponent<TMP_Text>();
                tmp.text = value;
                tmp.alignment = TextAlignmentOptions.MidlineRight;
                tmp.fontStyle = FontStyles.Normal;
                tmp.characterSpacing = 0f;
                var um = v.GetComponent<UIManagerText>();
                if (um != null) um.colorType = UIManagerText.ColorType.Accent;
            }
            Place(go, list);
        }

        private void TextRow(Transform list, string label, string value, Action<string> onEdited, Func<string> onEndEdit)
        {
            var go = Spawn(_tTextRow);
            go.transform.Find("Text").GetComponent<TMP_Text>().text = label;
            var field = go.transform.Find("Input/Text Input").GetComponent<TMP_InputField>();
            field.text = value;
            field.onValueChanged.AddListener(s => onEdited(s));
            field.onEndEdit.AddListener(_ =>
            {
                // Show the value as the entry serialises it, so a rejected
                // edit visibly snaps back.
                var canonical = onEndEdit();
                if (canonical != null && field.text != canonical) field.SetTextWithoutNotify(canonical);
            });
            Place(go, list);
        }

        /// A slider with a value box, for a setting that declares a range.
        /// Whole numbers when the setting is integral; otherwise a tenth.
        private void SliderRow(Transform list, string label, float min, float max, float value, bool whole, Action<float> onChanged)
        {
            var go = Spawn(_tSliderRow);
            go.transform.Find("Text").GetComponent<TMP_Text>().text = label;
            var sliderT = go.transform.Find("Slider");
            var sm = sliderT.GetComponent<SliderManager>();
            var slider = sliderT.GetComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            var field = sliderT.Find("Text Input")?.GetComponent<TMP_InputField>();
            string Show(float v) => whole ? Mathf.RoundToInt(v).ToString(System.Globalization.CultureInfo.InvariantCulture) : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);   // as the box parses it
            if (field != null) field.SetTextWithoutNotify(Show(slider.value));
            sm.onValueChanged.AddListener(v =>
            {
                if (field != null) field.SetTextWithoutNotify(Show(v));
                onChanged(v);
            });
            if (field != null)
            {
                field.onEndEdit.AddListener(s =>
                {
                    if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        slider.value = Mathf.Clamp(v, min, max);   // fires the listener above, which rewrites the box
                    else field.SetTextWithoutNotify(Show(slider.value));
                });
            }
            Place(go, list);
            sm.UpdateUI();
        }

        /// The row for one setting, by its type and declared constraints:
        /// a switch for a bool, a selector for a value list, a slider for a
        /// range, and a text box for the rest, committed through the entry's
        /// own serializer so a half-typed value doesn't take until it parses.
        private void SettingRow(Transform group, string label, ConfigEntryBase e)
        {
            DescribeNext(label, e.Description?.Description);
            if (e.SettingType == typeof(bool))
            {
                SwitchRow(group, label, e.BoxedValue is bool b && b, true, v => e.BoxedValue = v);
            }
            else if (e.Description.AcceptableValues is AcceptableValueList<string> sl && sl.AcceptableValues.Length > 0)
            {
                var options = sl.AcceptableValues;
                var current = Array.IndexOf(options, e.BoxedValue as string);
                SelectorRow(group, label, options, current < 0 ? 0 : current, i => e.BoxedValue = options[i]);
            }
            else if (e.Description.AcceptableValues is AcceptableValueRange<int> ir)
            {
                SliderRow(group, label, ir.MinValue, ir.MaxValue, Convert.ToSingle(e.BoxedValue), true, v => e.BoxedValue = Mathf.RoundToInt(v));
            }
            else if (e.Description.AcceptableValues is AcceptableValueRange<float> fr)
            {
                SliderRow(group, label, fr.MinValue, fr.MaxValue, Convert.ToSingle(e.BoxedValue), false, v => e.BoxedValue = v);
            }
            else
            {
                var t = Bind(e);
                TextRow(group, label, t.Value, t.OnEdited, t.OnEndEdit);
            }
        }

        private struct TextBinding
        {
            public string Value;
            public Action<string> OnEdited;
            public Func<string> OnEndEdit;
        }

        private static TextBinding Bind(ConfigEntryBase e) => new TextBinding
        {
            Value = e.GetSerializedValue(),
            OnEdited = s => { try { e.SetSerializedValue(s); } catch { /* keep typing */ } },
            OnEndEdit = () => { try { return e.GetSerializedValue(); } catch { return null; } },
        };

        /// A key binding: a KeyCode, or a string the mod describes as a hotkey.
        private static bool IsHotkey(ConfigEntryBase e) =>
            e.SettingType == typeof(KeyCode) ||
            (e.SettingType == typeof(string) && (e.Description.Description ?? "").IndexOf("hotkey", StringComparison.OrdinalIgnoreCase) >= 0);

        /// Two text settings side by side on one row, for short values.
        /// The row template has one label and one box; a second pair is
        /// cloned in, and each pair takes half the row.
        private void PairTextRow(Transform list, string labelA, TextBinding a, string labelB, TextBinding? b)
        {
            const float boxWidth = 170f;
            var go = Spawn(_tTextRow);
            var text = (RectTransform)go.transform.Find("Text");
            var input = (RectTransform)go.transform.Find("Input");
            var text2 = b == null ? null : (RectTransform)Object.Instantiate(text.gameObject, go.transform).transform;
            var input2 = b == null ? null : (RectTransform)Object.Instantiate(input.gameObject, go.transform).transform;

            void Half(RectTransform lbl, RectTransform box, float from, string label, TextBinding binding)
            {
                // Label spans its half up to the box; the box is right-aligned
                // in the half at a fixed width.
                lbl.anchorMin = new Vector2(from, 0f);
                lbl.anchorMax = new Vector2(from + 0.5f, 1f);
                lbl.pivot = new Vector2(0f, 0.5f);
                lbl.offsetMin = new Vector2(from > 0f ? 20f : lbl.offsetMin.x, lbl.offsetMin.y);
                lbl.offsetMax = new Vector2(-(boxWidth + 30f), lbl.offsetMax.y);
                lbl.GetComponent<TMP_Text>().text = label;

                box.anchorMin = new Vector2(from + 0.5f, box.anchorMin.y);
                box.anchorMax = new Vector2(from + 0.5f, box.anchorMax.y);
                box.pivot = new Vector2(1f, 0.5f);
                box.anchoredPosition = new Vector2(-20f, box.anchoredPosition.y);
                box.sizeDelta = new Vector2(boxWidth, box.sizeDelta.y);

                var field = box.Find("Text Input").GetComponent<TMP_InputField>();
                field.SetTextWithoutNotify(binding.Value);
                field.onValueChanged.AddListener(s => binding.OnEdited(s));
                field.onEndEdit.AddListener(_ =>
                {
                    var canonical = binding.OnEndEdit();
                    if (canonical != null && field.text != canonical) field.SetTextWithoutNotify(canonical);
                });
            }

            Half(text, input, 0f, labelA, a);
            if (b != null) Half(text2, input2, 0.5f, labelB, b.Value);
            Place(go, list);
        }

        /// "HideGameEconomyBars" -> "Hide game economy bars", with the
        /// acronyms and abbreviations the mods use kept readable.
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "eta", "ETA" }, { "ui", "UI" }, { "url", "URL" }, { "pos", "position" }, { "x", "X" }, { "y", "Y" },
            { "mm", "matchmaking" }, { "cfg", "config" }, { "api", "API" }, { "id", "ID" }, { "vsync", "VSync" },
        };

        internal static string Humanise(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var parts = new List<string>();
            var word = new System.Text.StringBuilder();
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                // A digit after a single letter stays attached ("T1", "T3"),
                // after a word it starts one ("Tier 4").
                var digitEdge = i > 0 && char.IsDigit(c) != char.IsDigit(key[i - 1]) && !(char.IsDigit(c) && word.Length == 1);
                var startsWord = word.Length > 0 && (
                    (char.IsUpper(c) && (!char.IsUpper(key[i - 1]) || (i + 1 < key.Length && char.IsLower(key[i + 1])))) ||
                    digitEdge ||
                    c == '_' || c == ' ');
                if (startsWord) { parts.Add(word.ToString()); word.Clear(); }
                if (c != '_' && c != ' ') word.Append(c);
            }
            if (word.Length > 0) parts.Add(word.ToString());

            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (Words.TryGetValue(p, out var fixedWord)) p = fixedWord;
                else if (p.Length > 1 && p.ToUpperInvariant() == p) { /* an acronym, or T1: as written */ }
                else p = p.ToLowerInvariant();
                parts[i] = i == 0 && char.IsLower(p[0]) ? char.ToUpperInvariant(p[0]) + p.Substring(1) : p;
            }
            return string.Join(" ", parts);
        }

        private void ButtonRow(Transform list, string text, Action onClick)
        {
            var go = Spawn(_tButtonRow);
            var bm = go.GetComponent<ButtonManager>();
            bm.buttonText = text;
            bm.onClick.AddListener(() => onClick());
            Place(go, list);
            bm.UpdateUI();
        }

        /// A rebuilt list should read from the top.
        private static void ScrollToTop(Transform list)
        {
            var scroll = list.GetComponentInParent<ScrollRect>();
            if (scroll == null) return;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 1f;
        }

        // ---- content --------------------------------------------------------

        private void RebuildUiTab()
        {
            if (_uiList == null) return;
            _pluginSignature = PluginSignature();
            Clear(_uiList);
            _pluginGroups.Clear();
            _sectionLabels.Clear();

            if (_owner.Plugins.Count == 0)
            {
                InfoRow(_uiList, "No UI mods loaded");
                ScrollToTop(_uiList);
                return;
            }

            var first = true;
            foreach (var plugin in _owner.Plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!first) Line(_uiList);
                first = false;
                var p = plugin;
                DescribeNext(p.Name, PluginSummary(p));
                _sectionLabels[p.Guid] = SectionRow(_uiList, p.Name, p.Enabled, _expanded.Contains(p.Guid),
                    on =>
                    {
                        _owner.SetPluginEnabled(p, on);
                        RebuildPluginGroup(p);
                    },
                    () =>
                    {
                        if (!_expanded.Add(p.Guid)) _expanded.Remove(p.Guid);
                        var expanded = _expanded.Contains(p.Guid);
                        if (_pluginGroups.TryGetValue(p.Guid, out var g) && g != null) g.gameObject.SetActive(expanded);
                        return expanded;
                    });

                var group = new GameObject("Settings " + p.Guid, typeof(RectTransform)).transform;
                var vl = group.gameObject.AddComponent<VerticalLayoutGroup>();
                vl.spacing = 15f;
                vl.childControlWidth = true;
                vl.childControlHeight = false;
                vl.childForceExpandWidth = true;
                vl.childForceExpandHeight = false;
                var fit = group.gameObject.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                group.SetParent(_uiList, false);
                group.gameObject.SetActive(_expanded.Contains(p.Guid));
                _pluginGroups[p.Guid] = group;
                FillPluginGroup(p, group);
            }
            ScrollToTop(_uiList);
        }

        /// A UI mod's hover text: its version, and what the row does.
        private static string PluginSummary(ModManagerPlugin.PluginEntry p)
        {
            string version = null;
            try { version = p.Type != null ? BepInEx.MetadataHelper.GetMetadata(p.Type)?.Version?.ToString() : null; }
            catch { }
            return (version != null ? $"Version {version}. " : "") +
                   (p.Enabled ? "Running. " : "Switched off. ") +
                   "The switch starts or stops it straight away; click the row to show or hide its settings.";
        }

        private void RebuildPluginGroup(ModManagerPlugin.PluginEntry plugin)
        {
            if (!_pluginGroups.TryGetValue(plugin.Guid, out var group) || group == null) { RebuildUiTab(); return; }
            Clear(group);
            FillPluginGroup(plugin, group);
            group.gameObject.SetActive(_expanded.Contains(plugin.Guid));
            if (_sectionLabels.TryGetValue(plugin.Guid, out var label) && label != null)
                label.text = SectionLabel(plugin.Name, _expanded.Contains(plugin.Guid));
            _pluginSignature = PluginSignature();
        }

        /// One row per config entry the mod bound. Booleans get the game's
        /// switch, allowed-value lists a selector, ranges a slider; everything
        /// else is edited as text and committed through the entry's own
        /// serializer, so a half-typed value simply doesn't take until it
        /// parses.
        private void FillPluginGroup(ModManagerPlugin.PluginEntry plugin, Transform group)
        {
            List<ConfigEntryBase> entries;
            try { entries = ModManagerPlugin.ConfigEntriesOf(plugin).ToList(); }
            catch (Exception e)
            {
                InfoRow(group, "Settings unavailable", e.Message);
                return;
            }
            if (entries.Count == 0) return;

            // Settings come back in the order the mod bound them, which is
            // the order its author meant them to be read in, so that order
            // is kept: sections by first appearance, each under its own
            // heading, and entries within a section as bound. Keys are shown
            // as words ("HideGameEconomyBars" as "Hide game economy bars").
            var sections = new List<string>();
            var bySection = new Dictionary<string, List<ConfigEntryBase>>();
            foreach (var e in entries)
            {
                var s = e.Definition.Section ?? "";
                if (!bySection.TryGetValue(s, out var l)) { bySection[s] = l = new List<ConfigEntryBase>(); sections.Add(s); }
                l.Add(e);
            }

            var first = true;
            foreach (var section in sections)
            {
                var list = bySection[section];
                if (!first) Line(group);
                first = false;
                if (sections.Count > 1 || !string.IsNullOrEmpty(section)) Heading(group, Humanise(section));

                // A run of hotkeys reads better two to a line: the game's
                // own key format is short, and a long column of them is what
                // BuildHotkeys' structure and unit lists would otherwise be.
                var keys = list.Where(IsHotkey).ToList();
                if (keys.Count >= 4 && keys.Count == list.Count)
                {
                    for (var i = 0; i < keys.Count; i += 2)
                    {
                        var a = keys[i];
                        var b = i + 1 < keys.Count ? keys[i + 1] : null;
                        PairTextRow(group, Humanise(a.Definition.Key), Bind(a), b == null ? null : Humanise(b.Definition.Key), b == null ? null : Bind(b));
                    }
                    continue;
                }

                foreach (var e in list) SettingRow(group, Humanise(e.Definition.Key), e);
            }

            var pl = plugin;
            ButtonRow(group, "Reset " + plugin.Name + " to defaults", () =>
            {
                foreach (var entry in entries)
                {
                    try { entry.BoxedValue = entry.DefaultValue; }
                    catch (Exception ex) { _log.LogWarning($"Could not reset {entry.Definition}: {ex.Message}"); }
                }
                RebuildPluginGroup(pl);
            });
        }

        private void RebuildLuaTab()
        {
            if (_luaList == null) return;
            Clear(_luaList);
            var locked = _owner.Locked;

            Heading(_luaList, "Lua mods need everyone in the lobby to run the same set");
            var vanilla = _owner.HashNow == _owner.HashVanilla;
            InfoRow(_luaList,
                locked ? "In a lobby or match — leave it to change mods" : "Applied at the next match launch",
                (vanilla ? "Vanilla   " : "Modded   ") + ModManagerPlugin.Short(_owner.HashNow));
            Line(_luaList);

            if (_owner.Mods.Count == 0)
            {
                InfoRow(_luaList, "No Lua mods found", "SanctuaryMods\\<Mod>\\<files laid out like LJ\\lua>");
                ScrollToTop(_luaList);
                return;
            }

            foreach (var mod in _owner.Mods)
            {
                var m = mod;
                var files = $"{m.LuaCount} lua" + (m.SantpCount > 0 ? $", {m.SantpCount} santp — not hash-checked" : "");
                DescribeNext(m.Name,
                    $"{m.LuaCount} Lua file(s)" + (m.SantpCount > 0 ? $" and {m.SantpCount} unit template(s), which the lobby's check does not cover" : "") +
                    ". A change applies at the next match launch, and everyone in the lobby needs the same Lua mods switched on.");
                SwitchRow(_luaList, $"{m.Name}   <alpha=#80>{files}", m.Enabled, !locked, on =>
                {
                    _owner.SetModEnabled(m, on);
                    RebuildLuaTab();
                });
            }
            ScrollToTop(_luaList);
        }
    }
}
