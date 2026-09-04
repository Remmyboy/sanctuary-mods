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
    // The page lives in the menu canvas, so it exists only while the front
    // menu does; there is no in-match UI.
    internal sealed class ModsPage
    {
        private const string HarmonyId = "com.sanctuarydb.modmanager.page";
        private static ModsPage _current;

        private readonly ModManagerPlugin _owner;
        private readonly BepInEx.Logging.ManualLogSource _log;
        private Harmony _harmony;
        private Sprite _icon;

        private MainMenuInterface _builtFor;
        private GameObject _page;
        private GameObject _sidebarButton;
        private Transform _templates;
        private Transform _uiList, _luaList;
        private string _pluginSignature = "";

        // Templates lifted out of the cloned Settings screen before its
        // lists are emptied. They sit under an inactive holder so a clone can
        // be configured before its Awake runs (Awake fires on reparenting
        // into the live list).
        private GameObject _tHeading, _tLine, _tSpacer, _tSwitchRow, _tTextRow, _tButtonRow;

        // Per-plugin settings group, so toggling one plugin rebuilds only its
        // own rows and the switch just clicked keeps its animation.
        private readonly Dictionary<string, Transform> _pluginGroups = new Dictionary<string, Transform>();

        public ModsPage(ModManagerPlugin owner, BepInEx.Logging.ManualLogSource log)
        {
            _owner = owner;
            _log = log;
            _current = this;
        }

        public bool IsOpen => _page != null && _page.activeSelf;

        /// True while the front menu is showing (or our page is).
        public bool CanOpen
        {
            get
            {
                if (IsOpen) return true;
                var mmi = MainMenuInterface.Instance;
                return mmi != null && mmi.gameObject.activeInHierarchy && _page != null;
            }
        }

        // ---- lifecycle ------------------------------------------------------

        /// Called every frame by the plugin. Builds the page once the menu
        /// exists (and again if the menu scene was recreated), keeps the
        /// plugin list fresh while the page is open.
        public void Tick()
        {
            var mmi = MainMenuInterface.Instance;
            if (mmi == null) return;
            if (!ReferenceEquals(mmi, _builtFor) || _page == null)
            {
                try { Build(mmi); }
                catch (Exception e)
                {
                    _log.LogError($"Mods page could not be built: {e}");
                    _builtFor = mmi; // don't retry every frame
                    return;
                }
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
            if (im == null) return;
            // Hides every game interface (and, via the prefix, ours) without
            // showing another one; then ours goes on top of the background.
            im.TransitionTo(InterfaceManager.Window.Background);
            _page.SetActive(true);
            RebuildUiTab();
            RebuildLuaTab();
        }

        public void Close()
        {
            if (_page != null) _page.SetActive(false);
            var im = InterfaceManager.Instance;
            if (im != null) im.TransitionTo(InterfaceManager.Window.Main);
        }

        public void Destroy()
        {
            // A hot reload while the page is up would otherwise leave the
            // menu hidden with nothing in its place.
            if (IsOpen) Close();
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            if (_page != null) Object.Destroy(_page);
            if (_sidebarButton != null) Object.Destroy(_sidebarButton);
            if (_icon != null) { Object.Destroy(_icon.texture); Object.Destroy(_icon); }
            _page = null;
            _sidebarButton = null;
            _icon = null;
            _builtFor = null;
            if (ReferenceEquals(_current, this)) _current = null;
        }

        // The game switching screens itself (a lobby invite, a match
        // starting) must take our page down with the others.
        private static void TransitionPrefix()
        {
            var page = _current?._page;
            if (page != null && page.activeSelf) page.SetActive(false);
        }

        private string PluginSignature() =>
            string.Join(";", _owner.Plugins.Select(p => p.Guid + (p.Enabled ? "+" : "-")));

        // ---- construction ---------------------------------------------------

        private void Build(MainMenuInterface mmi)
        {
            _builtFor = mmi;
            _pluginGroups.Clear();
            if (_page != null) Object.Destroy(_page);
            if (_sidebarButton != null) Object.Destroy(_sidebarButton);

            var root = mmi.transform.parent; // InterfaceManager canvas
            var settings = root.Find("SettingsInterface")?.gameObject
                           ?? throw new InvalidOperationException("SettingsInterface not found under the menu canvas.");

            if (_harmony == null)
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(AccessTools.Method(typeof(InterfaceManager), nameof(InterfaceManager.TransitionTo)),
                    prefix: new HarmonyMethod(typeof(ModsPage), nameof(TransitionPrefix)));
            }
            if (_icon == null) _icon = MakeIcon();

            // -- the page: a clone of the Settings screen, kept inactive
            //    while it is rearranged so no Awake sees the half-built state.
            _page = Object.Instantiate(settings, root);
            _page.name = "ModsInterface";
            _page.SetActive(false);
            Object.DestroyImmediate(_page.GetComponent<SanctuaryUI.SettingsInterface>());

            var content = _page.transform.Find("Content");
            var categories = content.Find("Categories");
            var panels = (RectTransform)content.Find("Panels");
            var buttons = (RectTransform)content.Find("Buttons");

            // No description column: the list and the button row take the
            // full width, with the same side margins the tab bar has.
            Object.DestroyImmediate(content.Find("Description Area").gameObject);
            panels.anchoredPosition = new Vector2(0f, panels.anchoredPosition.y);
            panels.sizeDelta = new Vector2(-70f, panels.sizeDelta.y);
            buttons.anchoredPosition = new Vector2(0f, buttons.anchoredPosition.y);
            buttons.sizeDelta = new Vector2(-70f, buttons.sizeDelta.y);

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
            _tSwitchRow = TakeTemplate(_luaList, "EdgePanToggle", "SwitchRow");
            PrepareSwitchTemplate(_tSwitchRow);
            PrepareTextTemplate(_tTextRow);
            PrepareButtonTemplate(_tButtonRow);
            Clear(_uiList);
            Clear(_luaList);

            var pm = _page.GetComponent<PanelManager>();
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
            var settingsButton = mmi.transform.Find("Left Sidebar/Content/Button List/Settings");
            _sidebarButton = Object.Instantiate(settingsButton.gameObject, settingsButton.parent);
            _sidebarButton.name = "Mods";
            _sidebarButton.transform.SetSiblingIndex(settingsButton.GetSiblingIndex() + 1);
            var pb = _sidebarButton.GetComponent<PanelButton>();
            RenamePanelButton(pb, "Mods", _icon);
            pb.onClick.AddListener(Open);
            settingsButton.GetComponentInParent<PanelButtonDimmer>()?.FetchButtons();

            _pluginSignature = "";
            _log.LogInfo("Mods page built into the front menu.");
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
        private static Sprite MakeIcon()
        {
            const int size = 64;
            const float stroke = 3f; // half-width; the game's icons are ~6px lines at this size
            var c = new Vector2(size / 2f, size / 2f);
            var r = 25f;
            Vector2 V(float deg) => c + new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * r;
            var segs = new List<(Vector2 a, Vector2 b)>();
            for (var i = 0; i < 6; i++) segs.Add((V(30 + 60 * i), V(30 + 60 * (i + 1))));
            foreach (var deg in new[] { 30f, 150f, 270f }) segs.Add((c, V(deg)));

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Mods (64x)",
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
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
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

        private static void Place(GameObject go, Transform list) => go.transform.SetParent(list, false);

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

            Heading(_uiList, "UI mods can be switched on and off freely, even mid-match");
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
                SwitchRow(_uiList, p.Name, p.Enabled, true, on =>
                {
                    _owner.SetPluginEnabled(p, on);
                    RebuildPluginGroup(p);
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
                _pluginGroups[p.Guid] = group;
                FillPluginGroup(p, group);
            }
            ScrollToTop(_uiList);
        }

        private void RebuildPluginGroup(ModManagerPlugin.PluginEntry plugin)
        {
            if (!_pluginGroups.TryGetValue(plugin.Guid, out var group) || group == null) { RebuildUiTab(); return; }
            Clear(group);
            FillPluginGroup(plugin, group);
            _pluginSignature = PluginSignature();
        }

        /// One row per config entry the mod bound, only while it is loaded
        /// (the settings live on the running instance). Booleans get the
        /// game's switch; everything else is edited as text and committed
        /// through the entry's own serializer, so a half-typed value simply
        /// doesn't take until it parses.
        private void FillPluginGroup(ModManagerPlugin.PluginEntry plugin, Transform group)
        {
            if (!plugin.Enabled) return;
            List<ConfigEntryBase> entries;
            try { entries = ModManagerPlugin.ConfigEntriesOf(plugin).ToList(); }
            catch (Exception e)
            {
                InfoRow(group, "Settings unavailable", e.Message);
                return;
            }
            if (entries.Count == 0) return;

            foreach (var entry in entries.OrderBy(e => e.Definition.Section).ThenBy(e => e.Definition.Key))
            {
                var e = entry;
                var label = $"{e.Definition.Section}  ·  {e.Definition.Key}";
                if (e.SettingType == typeof(bool))
                {
                    SwitchRow(group, label, e.BoxedValue is bool b && b, true, v => e.BoxedValue = v);
                }
                else
                {
                    TextRow(group, label, e.GetSerializedValue(),
                        s => { try { e.SetSerializedValue(s); } catch { /* keep typing */ } },
                        () => { try { return e.GetSerializedValue(); } catch { return null; } });
                }
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
                SwitchRow(_luaList, $"{m.Name}   <alpha=#80>{files}</alpha>", m.Enabled, !locked, on =>
                {
                    _owner.SetModEnabled(m, on);
                    RebuildLuaTab();
                });
            }
            ScrollToTop(_luaList);
        }
    }
}
