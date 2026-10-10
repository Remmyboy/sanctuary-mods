using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SanctuaryHud;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sanctuary.ModApi
{
    // Gameplay mods' Lua panels (modapi/ui.lua), drawn on the game's HUD
    // canvas with the HUD mods' own building blocks (shared\HudCanvas.cs,
    // HudPanel.cs), so a mod gets a draggable panel in the game's font, at its
    // UI scale, with no DLL of its own.
    //
    // The Lua side keeps the whole description as JSON in the client VM's _G
    // (__ModApiUI), with a revision number beside it (__ModApiUIRev). This
    // reads the number ten times a second and the JSON only when it changes,
    // then brings what's drawn into line element by element, reusing every
    // object whose element is the same kind as before: a panel set every
    // second keeps its hover highlights and costs next to nothing. A click
    // calls back into the VM (UI._Click), which runs the mod's function.
    internal static class ModUi
    {
        private const float PollSeconds = 0.1f;
        private const string RevGlobal = "__ModApiUIRev";
        private const string StateGlobal = "__ModApiUI";
        private const float MinScale = 0.6f, MaxScale = 2.5f;

        private static readonly Color White = Color.white;

        private static ConfigEntry<string> _cfgPanels;
        private static Dictionary<string, SavedPanel> _saved;

        private static readonly Dictionary<string, PanelView> _panels = new Dictionary<string, PanelView>(StringComparer.Ordinal);
        private static ToastView _toast;
        private static string _rev;
        private static int _toastSeen;
        private static float _nextPoll;
        private static bool _active;
        private static bool _errorLogged;

        internal static void Init(ConfigFile config)
        {
            _cfgPanels = config.Bind("UI", "Panels", "",
                "Where you dragged gameplay mods' panels, and which you folded away, as JSON ({\"panel.id\": {\"x\": 20, \"y\": 300}}).");
        }

        /// Every frame, from the plugin.
        internal static void Tick()
        {
            try
            {
                if (!ModLua.Ready)
                {
                    if (_active) Clear();
                    return;
                }
                if (Time.unscaledTime >= _nextPoll)
                {
                    _nextPoll = Time.unscaledTime + PollSeconds;
                    Poll();
                }
                if (!_active) return;

                HudCanvas.SetShowing(!HudCore.MenuOpen());
                foreach (var view in _panels.Values) view.Place();
                _toast?.Tick();
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                ModApiPlugin.Log.LogWarning($"Mod panels: {e} (logged once)");
            }
        }

        /// The match is over (or the VM went away): everything goes, and the
        /// next match's panels start from nothing.
        private static void Clear()
        {
            foreach (var view in _panels.Values) view.Destroy();
            _panels.Clear();
            _toast?.Destroy();
            _toast = null;
            HudCanvas.Destroy();
            _rev = null;
            _toastSeen = 0;
            _active = false;
        }

        private static void Poll()
        {
            var rev = ModLua.GetGlobal(RevGlobal);
            if (rev == null)
            {
                // No mod in this match draws anything.
                if (_active) Clear();
                return;
            }
            var root = HudCanvas.Ensure();
            if (root == null) return;
            _active = true;

            // The canvas goes with the scene: rebuild from scratch then.
            var lost = _toast != null && !_toast.Alive;
            foreach (var v in _panels.Values) lost |= !v.Alive;
            if (lost)
            {
                foreach (var view in _panels.Values) view.Destroy();
                _panels.Clear();
                _toast?.Destroy();
                _toast = null;
                _rev = null;
            }
            if (rev == _rev) return;

            var json = ModLua.GetGlobal(StateGlobal);
            if (json == null) return;
            var state = JObject.Parse(json);
            _rev = rev;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (state["panels"] is JArray list)
            {
                foreach (var item in list.OfType<JObject>())
                {
                    var id = Str(item, "id");
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                    if (!_panels.TryGetValue(id, out var view))
                    {
                        view = new PanelView(id, root);
                        _panels[id] = view;
                    }
                    view.Apply(item);
                }
            }
            foreach (var gone in _panels.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _panels[gone].Destroy();
                _panels.Remove(gone);
            }

            // Only the newest notice not yet shown: one that came and went
            // while nothing was polling is stale by now.
            if (state["toasts"] is JArray toasts)
            {
                JObject newest = null;
                foreach (var t in toasts.OfType<JObject>())
                {
                    var id = (int)Num(t, "id", 0);
                    if (id > _toastSeen)
                    {
                        _toastSeen = id;
                        newest = t;
                    }
                }
                if (newest != null)
                {
                    if (_toast == null) _toast = new ToastView(root);
                    _toast.Show(Str(newest, "title"), Str(newest, "text"), Num(newest, "seconds", 6f), Col(newest, "color", HudCore.AccentColour));
                }
            }
        }

        private static void Click(PanelView view, int index, string button)
        {
            if (index <= 0) return;
            ModLua.Run("Import(\"modapi/ui.lua\").UI._Click(" + OptionValues.LuaString(view.Id) + ", " +
                       view.Rev.ToString(CultureInfo.InvariantCulture) + ", " + index.ToString(CultureInfo.InvariantCulture) +
                       ", " + OptionValues.LuaString(button) + ")");
        }

        // ---- saved positions ---------------------------------------------------------

        private sealed class SavedPanel
        {
            [JsonProperty("x", NullValueHandling = NullValueHandling.Ignore)] public float? X;
            [JsonProperty("y", NullValueHandling = NullValueHandling.Ignore)] public float? Y;
            [JsonProperty("folded", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Folded;
            [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)] public float? Scale;
        }

        private static Dictionary<string, SavedPanel> Saved
        {
            get
            {
                if (_saved != null) return _saved;
                try { _saved = JsonConvert.DeserializeObject<Dictionary<string, SavedPanel>>(_cfgPanels?.Value ?? ""); }
                catch (Exception e) { ModApiPlugin.Log.LogWarning($"UI.Panels isn't readable JSON ({e.Message}); mod panels start where their mods put them."); }
                return _saved = _saved ?? new Dictionary<string, SavedPanel>(StringComparer.Ordinal);
            }
        }

        /// The saved entry for a panel, made if it has none, to change and Save.
        private static SavedPanel Entry(string id) =>
            Saved.TryGetValue(id, out var entry) ? entry : new SavedPanel();

        private static void Save(string id, SavedPanel panel)
        {
            Saved[id] = panel;
            if (_cfgPanels == null) return;
            var text = JsonConvert.SerializeObject(Saved.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value));
            if (text != _cfgPanels.Value) _cfgPanels.Value = text;
        }

        // ---- JSON helpers --------------------------------------------------------------

        private static string Str(JObject o, string key) =>
            o.TryGetValue(key, out var t) && t.Type == JTokenType.String ? (string)t : null;

        private static float Num(JObject o, string key, float fallback) =>
            o.TryGetValue(key, out var t) && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (float)t : fallback;

        private static bool Flag(JObject o, string key, bool fallback) =>
            o.TryGetValue(key, out var t) && t.Type == JTokenType.Boolean ? (bool)t : fallback;

        private static Color Col(JObject o, string key, Color fallback)
        {
            var hex = Str(o, key);
            if (hex == null || (hex.Length != 6 && hex.Length != 8)) return fallback;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return fallback;
            if (hex.Length == 6) v = (v << 8) | 0xFF;
            return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }

        // ---- a panel -------------------------------------------------------------------

        private sealed class PanelView
        {
            internal readonly string Id;
            internal int Rev = -1;
            private readonly HudPanel _panel;
            private readonly PanelHeading _title;
            private readonly RectTransform _content;
            private readonly List<Node> _nodes = new List<Node>();
            private bool _visible, _hasTitle;
            private Vector2 _initial;
            private bool _alignRight;
            private int _measuredAt = -1;
            private float _measuredScale = -1f;

            internal bool Alive => _panel.Alive;

            internal PanelView(string id, RectTransform root)
            {
                Id = id;
                _panel = HudPanel.Create(root, "Mod panel " + id, () => false);
                _panel.EnableResize(MinScale, MaxScale);
                _title = PanelHeading.Create(_panel.Rect, "Title", 24f, HudCore.AccentColour, TextAlignmentOptions.MidlineLeft);
                _title.OnClick = ToggleFolded;
                var content = new GameObject("Content", typeof(RectTransform));
                content.transform.SetParent(_panel.Rect, false);
                Column(content, 4f);
                _content = (RectTransform)content.transform;
                if (Saved.TryGetValue(id, out var saved) && saved.Folded) _content.gameObject.SetActive(false);
            }

            private void ToggleFolded()
            {
                var folded = _content.gameObject.activeSelf;
                _content.gameObject.SetActive(!folded);
                HudCanvas.LayoutVersion++;
                var entry = Entry(Id);
                entry.Folded = folded;
                Save(Id, entry);
            }

            internal void Apply(JObject o)
            {
                var title = Str(o, "title");
                _hasTitle = !string.IsNullOrEmpty(title);
                if (_title.gameObject.activeSelf != _hasTitle)
                {
                    _title.gameObject.SetActive(_hasTitle);
                    HudCanvas.LayoutVersion++;
                }
                // Without a title nothing could unfold it.
                if (!_hasTitle && !_content.gameObject.activeSelf)
                {
                    _content.gameObject.SetActive(true);
                    HudCanvas.LayoutVersion++;
                }
                HudCanvas.SetText(_title.Text, title ?? "");
                _visible = Flag(o, "visible", true);
                _initial = new Vector2(Num(o, "x", 20f), Num(o, "y", 300f));
                _alignRight = Str(o, "align") == "right";

                var rev = (int)Num(o, "rev", 0);
                if (rev != Rev)
                {
                    Rev = rev;
                    Reconcile(_content, _nodes, o["items"], this);
                    // Elements came, went or changed size (not only text,
                    // which bumps it itself): the panel is laid out afresh.
                    HudCanvas.LayoutVersion++;
                }
                _panel.Show(_visible && (_hasTitle || _nodes.Count > 0));
            }

            internal void Place()
            {
                if (!_panel.Showing) return;
                // The player's size: saved when a resize ends, applied every frame.
                var resized = _panel.TakeResized();
                if (resized != null)
                {
                    var entry = Entry(Id);
                    entry.Scale = resized.Value;
                    Save(Id, entry);
                }
                Saved.TryGetValue(Id, out var s);
                _panel.SetScale(s != null && s.Scale.HasValue ? s.Scale.Value : 1f);

                Vector2 want;
                if (s != null && s.X.HasValue && s.Y.HasValue) want = new Vector2(s.X.Value, s.Y.Value);
                else if (_alignRight)
                {
                    // Measured from the right edge, once the width is known:
                    // laid out here only on the frames something changed it,
                    // as Place does.
                    var scale = _panel.Rect.localScale.x;
                    if (_measuredAt != HudCanvas.LayoutVersion || _measuredScale != scale)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(_panel.Rect);
                        _measuredAt = HudCanvas.LayoutVersion;
                        _measuredScale = scale;
                    }
                    var k = HudCanvas.UnitsPerLogical;
                    var width = _panel.Rect.rect.width * scale / k;
                    want = new Vector2(HudCanvas.Size.x / k - width - _initial.x, _initial.y);
                }
                else want = _initial;

                var at = _panel.Place(want);
                // Saved when a drag ends, never for where the mod put it.
                if (_panel.TakeDragged())
                {
                    var entry = Entry(Id);
                    entry.X = at.x;
                    entry.Y = at.y;
                    Save(Id, entry);
                }
            }

            internal void Destroy()
            {
                _panel.Destroy();
                _nodes.Clear();
            }

            internal void OnClick(int index, string button) => Click(this, index, button);
        }

        // ---- elements ------------------------------------------------------------------

        private sealed class Node
        {
            internal string Type;
            internal GameObject Go;
            internal TMP_Text Text;
            internal Image Image;
            internal UiButton Button;
            /// A bar's filled part (Image is its track).
            internal RectTransform Value;
            internal LayoutElement Layout;
            internal HorizontalOrVerticalLayoutGroup Group;
            internal readonly List<Node> Children = new List<Node>();
        }

        /// The text without the characters its font can't draw, which would
        /// otherwise show as boxes (a player's name with a symbol in it).
        private static string Printable(TMP_Text text, string s)
        {
            var font = text != null ? text.font : null;
            if (font == null || string.IsNullOrEmpty(s)) return s;
            StringBuilder kept = null;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var ok = !char.IsSurrogate(c) && (char.IsWhiteSpace(c) || font.HasCharacter(c, true, true));
                if (ok) { kept?.Append(c); continue; }
                if (kept == null) kept = new StringBuilder(s, 0, i, s.Length);
            }
            return kept == null ? s : kept.ToString().Trim();
        }

        private static void Reconcile(Transform parent, List<Node> nodes, JToken items, PanelView view)
        {
            var list = items is JArray array ? array.OfType<JObject>().ToList() : new List<JObject>();
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var type = Str(item, "t") ?? "space";
                if (i < nodes.Count && nodes[i].Type != type)
                {
                    Discard(nodes[i]);
                    nodes[i] = Create(parent, type);
                }
                else if (i >= nodes.Count) nodes.Add(Create(parent, type));
                var node = nodes[i];
                node.Go.transform.SetSiblingIndex(i);
                Update(node, item, view);
            }
            for (var i = nodes.Count - 1; i >= list.Count; i--)
            {
                Discard(nodes[i]);
                nodes.RemoveAt(i);
            }
        }

        private static void Discard(Node node)
        {
            // Out of the layout at once; the object itself goes at the end of the frame.
            node.Go.SetActive(false);
            UnityEngine.Object.Destroy(node.Go);
        }

        private static HorizontalOrVerticalLayoutGroup Row(GameObject go, float spacing)
        {
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return group;
        }

        private static HorizontalOrVerticalLayoutGroup Column(GameObject go, float spacing)
        {
            var group = go.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return group;
        }

        private static Node Create(Transform parent, string type)
        {
            var node = new Node { Type = type };
            switch (type)
            {
                case "text":
                    node.Text = HudCanvas.Text(parent, "Text", 20f, White, TextAlignmentOptions.MidlineLeft);
                    node.Go = node.Text.gameObject;
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    break;
                case "button":
                {
                    node.Image = HudCanvas.Fill(parent, "Button", UiButton.Normal);
                    node.Image.sprite = HudStyle.Shade;
                    node.Image.type = Image.Type.Sliced;
                    node.Image.raycastTarget = true;
                    node.Go = node.Image.gameObject;
                    var group = (HorizontalLayoutGroup)Row(node.Go, 0f);
                    group.padding = new RectOffset(14, 14, 5, 5);
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    node.Layout.minHeight = 34f;
                    group.childAlignment = TextAnchor.MiddleCenter;
                    node.Text = HudCanvas.Text(node.Go.transform, "Label", 20f, White, TextAlignmentOptions.Center);
                    node.Button = node.Go.AddComponent<UiButton>();
                    node.Button.Back = node.Image;
                    node.Button.Label = node.Text;
                    var button = node.Button;
                    HoverGlow.Add(node.Go).When = () => button.Enabled;
                    break;
                }
                case "row":
                case "column":
                    node.Go = new GameObject(type == "row" ? "Row" : "Column", typeof(RectTransform));
                    node.Go.transform.SetParent(parent, false);
                    node.Group = type == "row" ? Row(node.Go, 8f) : Column(node.Go, 4f);
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    break;
                case "fill":
                    node.Go = new GameObject("Fill", typeof(RectTransform));
                    node.Go.transform.SetParent(parent, false);
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    node.Layout.minWidth = 8f;
                    node.Layout.flexibleWidth = 1f;
                    break;
                case "rule":
                {
                    var colour = HudCore.AccentColour;
                    colour.a = 0.35f;
                    node.Image = HudCanvas.Fill(parent, "Rule", colour);
                    node.Go = node.Image.gameObject;
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    node.Layout.minHeight = 2f;
                    node.Layout.preferredHeight = 2f;
                    node.Layout.flexibleWidth = 1f;
                    break;
                }
                case "swatch":
                    node.Image = HudCanvas.Fill(parent, "Swatch", White);
                    node.Go = node.Image.gameObject;
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    break;
                case "bar":
                {
                    node.Image = HudCanvas.Fill(parent, "Bar", new Color(1f, 1f, 1f, 0.12f));
                    node.Go = node.Image.gameObject;
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    node.Layout.flexibleWidth = 1f;
                    // Anchored to the track's left edge; its right anchor is the value.
                    var value = HudCanvas.Fill(node.Go.transform, "Value", HudCore.AccentColour).rectTransform;
                    value.anchorMin = Vector2.zero;
                    value.anchorMax = new Vector2(0f, 1f);
                    value.offsetMin = value.offsetMax = Vector2.zero;
                    node.Value = value;
                    break;
                }
                default:
                    node.Type = "space";
                    node.Go = new GameObject("Space", typeof(RectTransform));
                    node.Go.transform.SetParent(parent, false);
                    node.Layout = node.Go.AddComponent<LayoutElement>();
                    break;
            }
            return node;
        }

        private static void Update(Node node, JObject o, PanelView view)
        {
            switch (node.Type)
            {
                case "text":
                {
                    node.Text.richText = Flag(o, "rich", false);
                    HudCanvas.SetText(node.Text, Printable(node.Text, Str(o, "text") ?? ""));
                    node.Text.fontSize = Num(o, "size", 20f);
                    node.Text.color = Col(o, "color", White);
                    var width = Num(o, "width", 0f);
                    node.Text.textWrappingMode = width > 0f ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                    node.Layout.preferredWidth = width > 0f ? width : -1f;
                    break;
                }
                case "button":
                {
                    node.Text.richText = false;
                    HudCanvas.SetText(node.Text, Printable(node.Text, Str(o, "text") ?? ""));
                    node.Text.fontSize = Num(o, "size", 20f);
                    // A colour tints the whole button, with its label lifted
                    // towards white so it reads on the tint.
                    var tint = Str(o, "color") != null ? Col(o, "color", White) : (Color?)null;
                    node.Button.Tint = tint;
                    node.Text.color = tint is Color t ? Color.Lerp(t, Color.white, 0.6f) : White;
                    var index = (int)Num(o, "id", 0);
                    node.Button.Enabled = Flag(o, "enabled", true) && index > 0;
                    node.Button.OnClick = button => view.OnClick(index, button);
                    node.Button.Refresh();
                    break;
                }
                case "row":
                case "column":
                    node.Group.spacing = Num(o, "spacing", node.Type == "row" ? 8f : 4f);
                    // A row holding a fill stretches to the width it sits in, so
                    // whatever comes after the fill lines up on the right.
                    var stretches = node.Type == "row" && o["items"] is JArray kids && kids.OfType<JObject>().Any(k => Str(k, "t") == "fill");
                    node.Layout.flexibleWidth = stretches ? 1f : -1f;
                    Reconcile(node.Go.transform, node.Children, o["items"], view);
                    break;
                case "swatch":
                {
                    node.Image.color = Col(o, "color", White);
                    var size = Num(o, "size", 16f);
                    node.Layout.minWidth = node.Layout.preferredWidth = size;
                    node.Layout.minHeight = node.Layout.preferredHeight = size;
                    break;
                }
                case "bar":
                {
                    node.Value.GetComponent<Image>().color = Col(o, "color", HudCore.AccentColour);
                    node.Value.anchorMax = new Vector2(Mathf.Clamp01(Num(o, "value", 0f)), 1f);
                    var width = Num(o, "width", 120f);
                    var height = Num(o, "height", 10f);
                    node.Layout.minWidth = node.Layout.preferredWidth = width;
                    node.Layout.minHeight = node.Layout.preferredHeight = height;
                    break;
                }
                case "space":
                {
                    var size = Num(o, "size", 8f);
                    node.Layout.minWidth = node.Layout.preferredWidth = size;
                    node.Layout.minHeight = node.Layout.preferredHeight = size;
                    break;
                }
            }
        }

        // A button: a faint plate that lights in the accent colour under the
        // mouse. Clicks that end a drag don't count.
        internal sealed class UiButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            internal static readonly Color Normal = new Color(1f, 1f, 1f, 0.08f);
            private static readonly Color Lit = new Color(0.239f, 0.686f, 1f, 0.35f);
            private static readonly Color Off = new Color(1f, 1f, 1f, 0.03f);

            internal Image Back;
            internal TMP_Text Label;
            internal Action<string> OnClick;
            internal bool Enabled = true;
            /// The mod's colour for this button, or null for the plain one.
            internal Color? Tint;
            private bool _hover;

            internal void Refresh()
            {
                if (Back != null)
                    Back.color = !Enabled ? Off
                        : Tint is Color t ? new Color(t.r, t.g, t.b, _hover ? 0.6f : 0.32f)
                        : _hover ? Lit : Normal;
                if (Label != null)
                {
                    var c = Label.color;
                    c.a = Enabled ? 1f : 0.4f;
                    Label.color = c;
                }
            }

            public void OnPointerEnter(PointerEventData eventData) { _hover = true; Refresh(); }
            public void OnPointerExit(PointerEventData eventData) { _hover = false; Refresh(); }
            private void OnDisable() { _hover = false; Refresh(); }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (!Enabled || eventData.dragging) return;
                var button = eventData.button == PointerEventData.InputButton.Left ? "left"
                    : eventData.button == PointerEventData.InputButton.Right ? "right" : null;
                if (button != null) OnClick?.Invoke(button);
            }
        }

        // ---- notices -------------------------------------------------------------------

        // A notice across the top of the screen: big title, a line under it,
        // fading out at the end. It never takes the mouse.
        private sealed class ToastView
        {
            private const float Fade = 0.6f;
            private readonly RectTransform _plate;
            private readonly CanvasGroup _group;
            private readonly TMP_Text _title, _text;
            private float _until;
            private float _length;

            internal bool Alive => _plate != null;

            internal ToastView(RectTransform root)
            {
                _plate = HudCanvas.Plate(root, "Mod notice");
                _plate.anchorMin = _plate.anchorMax = new Vector2(0.5f, 1f);
                _plate.pivot = new Vector2(0.5f, 1f);
                _plate.GetComponent<Image>().raycastTarget = false;
                var column = (VerticalLayoutGroup)Column(_plate.gameObject, 4f);
                column.padding = new RectOffset(32, 32, 16, 18);
                column.childAlignment = TextAnchor.UpperCenter;
                HudCanvas.FitToContents(_plate);
                _group = _plate.gameObject.AddComponent<CanvasGroup>();
                _group.blocksRaycasts = false;
                _group.interactable = false;
                _title = HudCanvas.Text(_plate, "Title", 44f, HudCore.AccentColour, TextAlignmentOptions.Center);
                _text = HudCanvas.Text(_plate, "Text", 26f, White, TextAlignmentOptions.Center);
                _plate.gameObject.SetActive(false);
            }

            internal void Show(string title, string text, float seconds, Color colour)
            {
                HudCanvas.SetText(_title, title ?? "");
                _title.color = colour;
                HudCanvas.SetText(_text, text ?? "");
                _text.gameObject.SetActive(!string.IsNullOrEmpty(text));
                _length = Mathf.Clamp(seconds, 1f, 60f);
                _until = Time.unscaledTime + _length;
                _plate.anchoredPosition = new Vector2(0f, -170f * HudCanvas.UnitsPerLogical);
                _plate.gameObject.SetActive(true);
                _group.alpha = 1f;
            }

            internal void Tick()
            {
                if (_plate == null || !_plate.gameObject.activeSelf) return;
                var left = _until - Time.unscaledTime;
                if (left <= 0f) _plate.gameObject.SetActive(false);
                else _group.alpha = Mathf.Clamp01(left / Fade);
            }

            internal void Destroy()
            {
                if (_plate != null) UnityEngine.Object.Destroy(_plate.gameObject);
            }
        }
    }
}
