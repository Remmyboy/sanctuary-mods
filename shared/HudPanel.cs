using System;
using System.Collections.Generic;
using SanctuaryUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // A draggable panel on the HUD canvas, in the game's panel colour with
    // the accent hairline, sized to its rows: the shape the eco panels and
    // the idle panel share. It is anchored top-left and positioned in the
    // 1080-logical pixels the panels have always saved (HudCanvas converts),
    // kept wholly on screen, and dragged with the mouse unless locked; the
    // drag is a uGUI drag, so nothing of it reaches the map.
    //
    // On the right half of the screen the panel keeps its right edge where
    // it is as it changes width, laying itself out from that edge.
    internal sealed class HudPanel
    {
        internal const float Pad = 12f;

        private RectTransform _rect;
        private VerticalLayoutGroup _column;
        private PanelDrag _drag;
        private bool _rightAligned;

        internal RectTransform Rect => _rect;
        internal bool Alive => _rect != null;
        internal bool Showing => _rect != null && _rect.gameObject.activeSelf;
        internal bool RightAligned => _rightAligned;
        /// True while the mouse is dragging the panel.
        internal bool Dragging => _drag != null && _drag.Dragging;

        internal static HudPanel Create(RectTransform root, string name, Func<bool> locked)
        {
            var rt = HudCanvas.Plate(root, name);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            var column = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);
            column.spacing = 6f;
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            HudCanvas.FitToContents(rt);
            var drag = rt.gameObject.AddComponent<PanelDrag>();
            drag.Locked = locked;
            rt.gameObject.SetActive(false);
            return new HudPanel { _rect = rt, _column = column, _drag = drag };
        }

        internal void Show(bool showing)
        {
            if (_rect == null) return;
            if (_rect.gameObject.activeSelf != showing) _rect.gameObject.SetActive(showing);
        }

        internal void Destroy()
        {
            if (_rect != null) UnityEngine.Object.Destroy(_rect.gameObject);
            _rect = null;
        }

        /// The panel's own size on top of the canvas's.
        internal void SetScale(float scale)
        {
            if (_rect != null) _rect.localScale = new Vector3(scale, scale, 1f);
        }

        /// A row of the panel's column: a horizontal group, created once.
        internal RectTransform Row(string name, float spacing, TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_rect, false);
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        /// Lays the panel out for this frame and puts it at the saved
        /// position (1080-logical pixels, top-left, as GUI.Window kept it),
        /// kept on screen. Returns the position it ended up at in the same
        /// units, for saving — after a drag, or after the screen's edge or
        /// a change of width moved it.
        internal Vector2 Place(Vector2 logical)
        {
            if (_rect == null) return logical;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
            var k = HudCanvas.UnitsPerLogical;
            var size = HudCanvas.Size;
            var width = _rect.rect.width * _rect.localScale.x;
            var height = _rect.rect.height * _rect.localScale.y;

            // A drag moves the pivot; the saved position is the left edge.
            var x = _drag != null && _drag.Moved ? _rect.anchoredPosition.x - (_rightAligned ? width : 0f) : logical.x * k;
            var y = _drag != null && _drag.Moved ? -_rect.anchoredPosition.y : logical.y * k;
            if (_drag != null) _drag.Moved = false;

            // On the right half the panel hangs from its right edge, so a
            // change of width leaves that edge where it was.
            var centre = x + width / 2f;
            var right = centre > size.x / 2f;
            if (right != _rightAligned)
            {
                _rightAligned = right;
                _column.childAlignment = right ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
                _rect.pivot = new Vector2(right ? 1f : 0f, 1f);
            }

            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, size.x - width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, size.y - height));
            _rect.anchoredPosition = new Vector2(x + (_rightAligned ? width : 0f), -y);
            return new Vector2(x / k, y / k);
        }

        /// The drag handler: moves the plate with the mouse, in canvas
        /// units, and tells the panel it moved.
        private sealed class PanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            internal Func<bool> Locked;
            internal bool Dragging;
            internal bool Moved;
            private RectTransform _rect;

            private void Awake() => _rect = (RectTransform)transform;

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (Locked != null && Locked()) return;
                Dragging = true;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (!Dragging) return;
                var canvas = HudCanvas.Canvas;
                var k = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
                _rect.anchoredPosition += eventData.delta / k;
                Moved = true;
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                Dragging = false;
                Moved = true;
            }
        }
    }

    // One tile of such a panel: a unit's build-menu art on the game's plate
    // (or its name where the art isn't loaded), a progress bar along the
    // top, a figure top-right, the count bottom-right, a tag bottom-left,
    // a pause mark over it, a highlight while hovered, and the game's own
    // tooltip. Left and right clicks go to the panel through OnClick.
    internal sealed class PanelTile : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        internal const float Width = 108f;
        internal const float Height = 116f;
        private const float Art = 96f;

        private static readonly Color Back = new Color(0.1f, 0.12f, 0.15f, 0.9f);
        private static readonly Color Dim = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color ProgressColour = new Color(1f, 0.8f, 0.2f, 0.95f);

        private Image _hover, _plate, _icon, _track, _fill, _pauseA, _pauseB;
        private TMP_Text _fallback, _rate, _count, _tag;
        private GameObject _rateBox, _countBox, _tagBox;
        private TooltipTrigger _tooltip;

        internal Action<PointerEventData.InputButton> OnClick;

        internal static PanelTile Create(Transform parent, string name)
        {
            var back = HudCanvas.Fill(parent, name, Back);
            back.raycastTarget = true;
            var go = back.gameObject;
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = Width;
            layout.preferredHeight = Height;
            layout.minWidth = Width;
            layout.minHeight = Height;
            var tile = go.AddComponent<PanelTile>();

            // The art sits a little below the top edge, where the figure and
            // the progress bar go.
            var art = new GameObject("Art", typeof(RectTransform));
            art.transform.SetParent(go.transform, false);
            var artRect = (RectTransform)art.transform;
            artRect.anchorMin = artRect.anchorMax = new Vector2(0.5f, 1f);
            artRect.pivot = new Vector2(0.5f, 1f);
            artRect.anchoredPosition = new Vector2(0f, -14f);
            artRect.sizeDelta = new Vector2(Art, Art);
            tile._plate = Stretched(art.transform, "Plate");
            tile._icon = Stretched(art.transform, "Icon");
            tile._fallback = HudCanvas.Text(art.transform, "Name", 22f, new Color(1f, 1f, 1f, 0.8f), TextAlignmentOptions.Center);
            Fill(tile._fallback.rectTransform);
            tile._fallback.enableWordWrapping = true;
            tile._fallback.gameObject.SetActive(false);

            var track = AccentColour;
            track.a = 0.14f;
            tile._track = HudCanvas.Fill(go.transform, "Progress", track);
            var trackRect = tile._track.rectTransform;
            trackRect.anchorMin = new Vector2(0f, 1f);
            trackRect.anchorMax = new Vector2(1f, 1f);
            trackRect.pivot = new Vector2(0.5f, 1f);
            trackRect.offsetMin = new Vector2(6f, -10f);
            trackRect.offsetMax = new Vector2(-6f, -4f);
            tile._fill = HudCanvas.Fill(tile._track.transform, "Fill", ProgressColour);
            tile._fill.sprite = HudCanvas.White;
            tile._fill.type = Image.Type.Filled;
            tile._fill.fillMethod = Image.FillMethod.Horizontal;
            tile._fill.fillOrigin = 0;
            Fill(tile._fill.rectTransform);

            tile._rateBox = Box(go.transform, "Rate", new Vector2(1f, 1f), new Vector2(-2f, -12f), 24f, TextAlignmentOptions.MidlineRight, out tile._rate);
            tile._countBox = Box(go.transform, "Count", new Vector2(1f, 0f), new Vector2(-2f, 2f), 26f, TextAlignmentOptions.MidlineRight, out tile._count);
            tile._tagBox = Box(go.transform, "Tag", new Vector2(0f, 0f), new Vector2(2f, 2f), 20f, TextAlignmentOptions.MidlineLeft, out tile._tag);

            var mark = new Color(1f, 1f, 1f, 0.9f);
            tile._pauseA = HudCanvas.Fill(go.transform, "Pause", mark);
            tile._pauseB = HudCanvas.Fill(go.transform, "Pause", mark);
            Centre(tile._pauseA.rectTransform, new Vector2(-9f, 0f), new Vector2(10f, 32f));
            Centre(tile._pauseB.rectTransform, new Vector2(9f, 0f), new Vector2(10f, 32f));
            tile._pauseA.gameObject.SetActive(false);
            tile._pauseB.gameObject.SetActive(false);

            tile._hover = HudCanvas.Fill(go.transform, "Hover", new Color(1f, 1f, 1f, 0.12f));
            Fill(tile._hover.rectTransform);
            tile._hover.gameObject.SetActive(false);

            go.SetActive(false);
            return tile;
        }

        private static Image Stretched(Transform parent, string name)
        {
            var image = HudCanvas.Fill(parent, name, Color.white);
            Fill(image.rectTransform);
            image.enabled = false;
            return image;
        }

        private static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Centre(RectTransform rt, Vector2 at, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = at;
            rt.sizeDelta = size;
        }

        /// A text on a dark box in one corner, so it reads over the art;
        /// the box grows with the text.
        private static GameObject Box(Transform parent, string name, Vector2 corner, Vector2 inset, float size, TextAlignmentOptions alignment, out TMP_Text text)
        {
            var box = HudCanvas.Fill(parent, name, new Color(0f, 0f, 0f, 0.6f));
            var rt = box.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = corner;
            rt.anchoredPosition = inset;
            var group = box.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(4, 4, 0, 0);
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var fitter = box.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            text = HudCanvas.Text(box.transform, "Text", size, Color.white, alignment);
            return box.gameObject;
        }

        internal void Show(bool showing)
        {
            if (gameObject.activeSelf != showing) gameObject.SetActive(showing);
        }

        /// The tile's look for this frame.
        /// progress: 0..1 for a bar along the top, below 0 for none.
        /// rate, count, tag: null or below 0 for none.
        internal void Set(uint plate, uint icon, string fallback, float alpha,
            float progress, string rate, bool rateDim, int count, bool countDim, string tag, bool paused,
            string tipTitle, string tipBody)
        {
            var iconSprite = SpriteFor(icon);
            var plateSprite = iconSprite != null ? SpriteFor(plate) : null;
            var tint = new Color(1f, 1f, 1f, alpha);
            _plate.sprite = plateSprite;
            _plate.color = tint;
            _plate.enabled = plateSprite != null;
            _icon.sprite = iconSprite;
            _icon.color = tint;
            _icon.enabled = iconSprite != null;
            var showFallback = iconSprite == null;
            if (_fallback.gameObject.activeSelf != showFallback) _fallback.gameObject.SetActive(showFallback);
            if (showFallback)
            {
                HudCanvas.SetText(_fallback, fallback ?? "");
                _fallback.color = new Color(1f, 1f, 1f, 0.7f * alpha);
            }

            var showBar = progress >= 0f;
            if (_track.gameObject.activeSelf != showBar) _track.gameObject.SetActive(showBar);
            if (showBar) _fill.fillAmount = Mathf.Clamp01(progress);

            Corner(_rateBox, _rate, rate, rateDim);
            Corner(_countBox, _count, count >= 0 ? count.ToString() : null, countDim);
            Corner(_tagBox, _tag, tag, true);

            if (_pauseA.gameObject.activeSelf != paused)
            {
                _pauseA.gameObject.SetActive(paused);
                _pauseB.gameObject.SetActive(paused);
            }

            if (tipTitle != null)
            {
                if (_tooltip == null) _tooltip = gameObject.AddComponent<TooltipTrigger>();
                _tooltip.usesDictionaryTitle = false;
                _tooltip.usesDictionaryDescription = false;
                _tooltip.title = tipTitle;
                _tooltip.description = tipBody ?? "";
            }
            else if (_tooltip != null)
            {
                Destroy(_tooltip);
                _tooltip = null;
            }
        }

        private static void Corner(GameObject box, TMP_Text text, string value, bool dim)
        {
            var on = !string.IsNullOrEmpty(value);
            if (box.activeSelf != on) box.SetActive(on);
            if (!on) return;
            HudCanvas.SetText(text, value);
            text.color = dim ? Dim : Color.white;
        }

        public void OnPointerEnter(PointerEventData eventData) => _hover.gameObject.SetActive(true);
        public void OnPointerExit(PointerEventData eventData) => _hover.gameObject.SetActive(false);
        private void OnDisable() => _hover.gameObject.SetActive(false);

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.dragging) return;
            OnClick?.Invoke(eventData.button);
        }
    }

    // A line of text in the panel that takes a click, with a highlight
    // while hovered: the idle panel's FACTORIES heading.
    internal sealed class PanelHeading : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private Image _hover;
        internal TMP_Text Text;
        internal Action OnClick;

        internal static PanelHeading Create(Transform parent, string name, float size, Color colour, TextAlignmentOptions alignment)
        {
            var back = HudCanvas.Fill(parent, name, Color.clear);
            back.raycastTarget = true;
            var go = back.gameObject;
            var heading = go.AddComponent<PanelHeading>();
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(4, 4, 2, 2);
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            heading._hover = HudCanvas.Fill(go.transform, "Hover", new Color(1f, 1f, 1f, 0.12f));
            heading._hover.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var hrt = heading._hover.rectTransform;
            hrt.anchorMin = Vector2.zero;
            hrt.anchorMax = Vector2.one;
            hrt.offsetMin = Vector2.zero;
            hrt.offsetMax = Vector2.zero;
            heading._hover.gameObject.SetActive(false);
            heading.Text = HudCanvas.Text(go.transform, "Text", size, colour, alignment);
            return heading;
        }

        public void OnPointerEnter(PointerEventData eventData) => _hover.gameObject.SetActive(OnClick != null);
        public void OnPointerExit(PointerEventData eventData) => _hover.gameObject.SetActive(false);
        private void OnDisable() => _hover.gameObject.SetActive(false);

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && !eventData.dragging) OnClick?.Invoke();
        }
    }

    /// A pool of tiles under one parent, handed out in order each frame;
    /// whatever isn't taken is hidden.
    internal sealed class TilePool
    {
        private readonly Transform _parent;
        private readonly string _name;
        private readonly List<PanelTile> _tiles = new List<PanelTile>();
        private int _taken;

        internal TilePool(Transform parent, string name)
        {
            _parent = parent;
            _name = name;
        }

        internal void Begin() => _taken = 0;

        internal PanelTile Take()
        {
            while (_taken < _tiles.Count && _tiles[_taken] == null) _tiles.RemoveAt(_taken);
            PanelTile tile;
            if (_taken < _tiles.Count) tile = _tiles[_taken];
            else
            {
                tile = PanelTile.Create(_parent, _name);
                _tiles.Add(tile);
            }
            _taken++;
            tile.transform.SetSiblingIndex(_taken - 1);
            tile.Show(true);
            return tile;
        }

        internal void End()
        {
            for (var i = _taken; i < _tiles.Count; i++) if (_tiles[i] != null) _tiles[i].Show(false);
        }
    }
}
