using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The small parts a control panel on the HUD canvas is built from:
    // fixed-size cells and labels for a layout row, column rules, buttons
    // that may also be switches, a slider and a filled bar. Sizes are canvas
    // units (twice the 1080-logical pixels the IMGUI panels were drawn in).
    internal static class HudControls
    {
        internal static readonly Color TextMid = new Color(1f, 1f, 1f, 0.78f);
        internal static readonly Color TextDim = new Color(1f, 1f, 1f, 0.5f);

        /// An empty cell of a fixed size in a layout row or column; a size
        /// below 0 is left to the layout.
        internal static RectTransform Cell(Transform parent, string name, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Size(go, width, height);
            return (RectTransform)go.transform;
        }

        /// A cell that takes whatever width its row has left over.
        internal static RectTransform Flexible(Transform parent)
        {
            var rt = Cell(parent, "Space", 0f, -1f);
            rt.GetComponent<LayoutElement>().flexibleWidth = 1f;
            return rt;
        }

        /// A text in a layout row, a fixed width when width is above 0.
        internal static TMP_Text Label(Transform parent, string name, string text, float size, Color colour,
            TextAlignmentOptions alignment, float width = -1f, float height = -1f, FontStyles style = FontStyles.Normal)
        {
            var label = HudCanvas.Text(parent, name, size, colour, alignment, style);
            label.text = text ?? "";
            Size(label.gameObject, width, height);
            return label;
        }

        /// A column rule: a thin line down the middle of a cell width wide.
        /// Each row draws its own at the row's full height, so together
        /// they read as one line down the table.
        internal static RectTransform Rule(Transform parent, float width, float height, float alpha)
        {
            var cell = Cell(parent, "Rule", width, height);
            var line = HudCanvas.Fill(cell, "Line", new Color(1f, 1f, 1f, alpha));
            var rt = line.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(2f, 0f);
            return cell;
        }

        /// A line across a column, height tall with the line in its middle.
        internal static RectTransform HRule(Transform parent, float height, float alpha)
        {
            var cell = Cell(parent, "Rule", -1f, height);
            cell.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var line = HudCanvas.Fill(cell, "Line", new Color(1f, 1f, 1f, alpha));
            var rt = line.rectTransform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(0f, 2f);
            return cell;
        }

        /// A horizontal row in a layout column, its children sized by
        /// their own layout elements.
        internal static RectTransform Row(Transform parent, string name, float spacing, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        /// A vertical column in a layout, its children at their own sizes.
        internal static RectTransform Column(Transform parent, string name, float spacing)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var group = go.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        internal static LayoutElement Size(GameObject go, float width, float height)
        {
            var layout = go.GetComponent<LayoutElement>();
            if (layout == null) layout = go.AddComponent<LayoutElement>();
            if (width >= 0f) layout.minWidth = layout.preferredWidth = width;
            if (height >= 0f) layout.minHeight = layout.preferredHeight = height;
            return layout;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// Whether type on this colour wants to be dark.
        internal static bool Light(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b > 0.62f;

        internal static readonly Color DarkText = new Color(0.02f, 0.07f, 0.12f);

        private static Sprite _disc;

        /// A white disc, anti-aliased, for a slider's knob.
        internal static Sprite Disc
        {
            get
            {
                if (_disc != null) return _disc;
                const int size = 32;
                var texture = Generated.Keep(new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                });
                var pixels = new Color[size * size];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size / 2f, size / 2f));
                        pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(size / 2f - 0.5f - d));
                    }
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                _disc = Generated.Keep(Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect));
                return _disc;
            }
        }
    }

    // A button: a faint plate that lights under the mouse. Switched on
    // (SetOn) it is a switch shown solid in a colour — the accent, or an
    // army's own — with type dark or light to suit it. Left clicks only,
    // and none that end a drag of the panel.
    internal sealed class HudButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private static readonly Color Rest = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Lit = new Color(1f, 1f, 1f, 0.17f);

        internal Image Back;
        internal TMP_Text Label;
        internal RawImage Icon;
        internal Action OnClick = null;   // set by the mods that have buttons
        private bool _hover, _on, _plain;
        private Color _colour, _plainText;

        internal static HudButton Create(Transform parent, string name, string label, float width, float height, float size = 22f)
        {
            var back = HudCanvas.Fill(parent, name, Rest);
            back.raycastTarget = true;
            HudControls.Size(back.gameObject, width, height);
            var button = back.gameObject.AddComponent<HudButton>();
            button.Back = back;
            button._colour = AccentColour;
            if (label != null)
            {
                button.Label = HudCanvas.Text(back.transform, "Label", size, HudControls.TextMid, TextAlignmentOptions.Center);
                button.Label.text = label;
                button.Label.overflowMode = TextOverflowModes.Ellipsis;
                button.Label.margin = new Vector4(6f, 0f, 6f, 0f);
                HudControls.Stretch(button.Label.rectTransform);
            }
            button.Refresh();
            return button;
        }

        /// A white icon centred on the button, size square, tinted as the
        /// label would be.
        internal HudButton WithIcon(Texture texture, float size)
        {
            var go = new GameObject("Icon", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            Icon = go.AddComponent<RawImage>();
            Icon.texture = texture;
            Icon.raycastTarget = false;
            var rt = Icon.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            Refresh();
            return this;
        }

        /// Takes whatever width its row has left over, from its own as the least.
        internal HudButton Flexible()
        {
            GetComponent<LayoutElement>().flexibleWidth = 1f;
            return this;
        }

        /// A heading that takes a click: no plate until the mouse is over
        /// it, its text in the given colour, or the accent while on.
        internal HudButton Plain(TextAlignmentOptions alignment, Color text)
        {
            _plain = true;
            _plainText = text;
            Back.canvasRenderer.cullTransparentMesh = false;
            if (Label != null)
            {
                Label.alignment = alignment;
                Label.margin = Vector4.zero;
            }
            Refresh();
            return this;
        }

        internal void SetOn(bool on) => SetOn(on, AccentColour);

        internal void SetOn(bool on, Color colour)
        {
            colour.a = 1f;
            if (on == _on && colour == _colour) return;
            _on = on;
            _colour = colour;
            Refresh();
        }

        internal void SetLabel(string text) => HudCanvas.SetText(Label, text);

        private void Refresh()
        {
            Color back, type;
            if (_plain)
            {
                back = _hover ? Rest : Color.clear;
                type = _on ? AccentColour : _hover ? Color.white : _plainText;
            }
            else if (_on)
            {
                back = _hover ? Color.Lerp(_colour, Color.white, 0.15f) : _colour;
                type = HudControls.Light(_colour) ? HudControls.DarkText : Color.white;
            }
            else
            {
                back = _hover ? Lit : Rest;
                type = _hover ? Color.white : HudControls.TextMid;
            }
            if (Back != null) Back.color = back;
            if (Label != null) Label.color = type;
            if (Icon != null) Icon.color = type;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hover = true;
            Refresh();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hover = false;
            Refresh();
        }

        private void OnDisable()
        {
            _hover = false;
            Refresh();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
            OnClick?.Invoke();
        }
    }

    // A thin slider: a track filled in a colour up to a round knob. It
    // follows the mouse from the press, with no drag threshold, and the
    // owner hears each move (OnChange) and the release (OnRelease). While
    // it is held, Set leaves it alone, so the owner can pass the live value
    // every frame.
    internal sealed class HudSlider : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IInitializePotentialDragHandler
    {
        private const float Knob = 24f, Track = 8f;

        private RectTransform _track, _fill, _knob;
        private Image _fillImage;

        internal bool Held { get; private set; }
        /// Where the knob is, 0 to 1.
        internal float Value { get; private set; }
        internal Action<float> OnChange = null;   // set by the mods that have sliders
        internal Action OnRelease = null;
        /// Where the mouse may put the knob, given where it is over the
        /// track (0 to 1): a seek bar that only goes forward, say.
        internal Func<float, float> Limit = null;

        internal static HudSlider Create(Transform parent, string name, float width, float height, Color colour)
        {
            // The whole cell catches the mouse, not just the thin track.
            var hit = HudCanvas.Fill(parent, name, Color.clear);
            hit.raycastTarget = true;
            hit.canvasRenderer.cullTransparentMesh = false;
            HudControls.Size(hit.gameObject, width, height);
            var slider = hit.gameObject.AddComponent<HudSlider>();

            var track = HudCanvas.Fill(hit.transform, "Track", new Color(1f, 1f, 1f, 0.16f));
            slider._track = track.rectTransform;
            slider._track.anchorMin = new Vector2(0f, 0.5f);
            slider._track.anchorMax = new Vector2(1f, 0.5f);
            slider._track.offsetMin = new Vector2(Knob / 2f, -Track / 2f);
            slider._track.offsetMax = new Vector2(-Knob / 2f, Track / 2f);

            slider._fillImage = HudCanvas.Fill(slider._track, "Fill", colour);
            slider._fill = slider._fillImage.rectTransform;
            slider._fill.anchorMin = Vector2.zero;
            slider._fill.anchorMax = new Vector2(0f, 1f);
            slider._fill.offsetMin = Vector2.zero;
            slider._fill.offsetMax = Vector2.zero;

            var knob = HudCanvas.Fill(slider._track, "Knob", new Color(0.95f, 0.96f, 0.98f, 1f));
            knob.sprite = HudControls.Disc;
            slider._knob = knob.rectTransform;
            slider._knob.sizeDelta = new Vector2(Knob, Knob);
            slider.Show(0f);
            return slider;
        }

        /// Puts the knob at value (0 to 1), unless the mouse has it.
        internal void Set(float value)
        {
            if (!Held) Show(value);
        }

        internal void SetColour(Color colour) => _fillImage.color = colour;

        private void Show(float value)
        {
            Value = Mathf.Clamp01(value);
            _fill.anchorMax = new Vector2(Value, 1f);
            _knob.anchorMin = _knob.anchorMax = new Vector2(Value, 0.5f);
            _knob.anchoredPosition = Vector2.zero;
        }

        private void Follow(PointerEventData eventData)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_track, eventData.position, eventData.pressEventCamera, out var local)) return;
            var rect = _track.rect;
            var value = Mathf.Clamp01(rect.width > 0f ? (local.x - rect.xMin) / rect.width : 0f);
            Show(Limit != null ? Limit(value) : value);
            OnChange?.Invoke(Value);
        }

        public void OnInitializePotentialDrag(PointerEventData eventData) => eventData.useDragThreshold = false;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            Held = true;
            Follow(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Held) Follow(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Release();
        }

        private void Update()
        {
            // A release outside the window may never reach OnPointerUp.
            if (Held && !Input.GetMouseButton(0)) Release();
        }

        // Hidden mid-drag: let go without acting on it.
        private void OnDisable() => Held = false;

        private void Release()
        {
            if (!Held) return;
            Held = false;
            OnRelease?.Invoke();
        }
    }

    // A filled bar with its figure over it: a store and how full it is.
    internal sealed class HudBar
    {
        private Image _fill;
        private TMP_Text _text;

        internal static HudBar Create(Transform parent, string name, float width, float height, float size)
        {
            var back = HudCanvas.Fill(parent, name, new Color(1f, 1f, 1f, 0.1f));
            HudControls.Size(back.gameObject, width, height);
            var bar = new HudBar();
            bar._fill = HudCanvas.Fill(back.transform, "Fill", Color.white);
            bar._fill.sprite = HudCanvas.White;
            bar._fill.type = Image.Type.Filled;
            bar._fill.fillMethod = Image.FillMethod.Horizontal;
            bar._fill.fillOrigin = 0;
            HudControls.Stretch(bar._fill.rectTransform);
            bar._text = HudCanvas.Text(back.transform, "Text", size, Color.white, TextAlignmentOptions.Center);
            HudControls.Stretch(bar._text.rectTransform);
            return bar;
        }

        internal void Set(float fraction, Color colour, string text)
        {
            _fill.fillAmount = Mathf.Clamp01(fraction);
            if (_fill.color != colour) _fill.color = colour;
            HudCanvas.SetText(_text, text);
        }
    }
}
