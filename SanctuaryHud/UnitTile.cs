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
    // One of the game's unit buttons, cloned: a copy of the panel's own
    // button prefab — background plate, portrait, strategic icon, count text,
    // progress bar, hover overlay and the Button with its glow — with the
    // game's element script taken off and this one put on. Each frame it
    // mirrors a live button on the concealed game panel (Mirror), and every
    // pointer event it takes is passed to that button, so the Lua behind it
    // runs unchanged: left, right and shift clicks, and the hover that puts
    // a build option's figures on the unit card.
    internal sealed class UnitTile : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// The game's tile is 80 canvas units square.
        internal const float Size = 80f;

        private UnitButtonElement _source;
        private Image _background, _portrait, _icon, _progress;
        private TMP_Text _text;
        private GameObject _textContainer, _hoverOverlay;
        private Button _button;
        private LayoutElement _layout;
        private Image _edge;
        private RawImage _badge;
        private bool _hover;

        /// The game button this tile stands for, or null.
        internal UnitButtonElement Source => _source;

        /// The tile under the mouse, or null.
        internal static UnitTile Hovered { get; private set; }

        /// A clone of the panel's button prefab, stripped and ready, parented
        /// under parent and active. Null when the prefab can't be cloned.
        internal static UnitTile Create(SanctuaryPanelUI panel, Transform parent)
        {
            var prefab = panel != null ? panel.buttonPrefab : null;
            var holder = HudCanvas.Holder;
            if (prefab == null || holder == null) return null;
            GameObject go = null;
            try
            {
                // Under the inactive holder nothing on the clone runs yet, so
                // the game's script comes off before its Awake can.
                go = Instantiate(prefab, holder);
                go.name = "Tile";
                var tile = go.AddComponent<UnitTile>();

                var element = go.GetComponent<UnitButtonElement>();
                if (element != null)
                {
                    tile._background = element.background;
                    tile._portrait = element.portraitImage;
                    tile._icon = element.strategicIconImage;
                    tile._progress = element.progressBar;
                    tile._text = element.textOverlayText;
                    tile._textContainer = element.textOverlayContainer;
                    tile._hoverOverlay = element.hoverOverlay;
                    DestroyImmediate(element);
                }
                foreach (var trigger in go.GetComponentsInChildren<TooltipTrigger>(true)) DestroyImmediate(trigger);

                tile._button = go.GetComponent<Button>();
                if (tile._button != null) tile._button.onClick.RemoveAllListeners();
                tile._layout = go.GetComponent<LayoutElement>();
                if (tile._layout == null) tile._layout = go.AddComponent<LayoutElement>();
                tile._layout.ignoreLayout = false;
                // The click must land on the tile, not on the row's plate
                // behind it, whatever the prefab set its images to.
                if (tile._background != null) tile._background.raycastTarget = true;
                var rt = (RectTransform)go.transform;
                rt.localScale = Vector3.one;

                if (tile._hoverOverlay != null) tile._hoverOverlay.SetActive(false);
                if (tile._progress != null) tile._progress.gameObject.SetActive(false);
                if (tile._textContainer != null) tile._textContainer.SetActive(false);

                // The HUD's own additions: a line along the bottom edge in the
                // unit's element colour, and the strategic icon badge in the
                // top-right corner, raised a little above the tile's edge.
                tile._edge = HudCanvas.Fill(rt, "Edge", Color.white);
                HudCanvas.StretchAlongBottom(tile._edge.rectTransform, 2f);
                var badge = new GameObject("Badge", typeof(RectTransform));
                badge.transform.SetParent(rt, false);
                tile._badge = badge.AddComponent<RawImage>();
                tile._badge.raycastTarget = false;
                var brt = tile._badge.rectTransform;
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 1f);
                brt.sizeDelta = new Vector2(44f, 44f);
                brt.anchoredPosition = new Vector2(0f, 6f);
                badge.SetActive(false);

                go.transform.SetParent(parent, false);
                go.SetActive(true);
                return tile;
            }
            catch (Exception e)
            {
                if (go != null) Destroy(go);
                if (!_createLogged)
                {
                    _createLogged = true;
                    _log?.LogWarning($"Unit tile: the game's button prefab could not be cloned (logged once): {e}");
                }
                return null;
            }
        }

        // ---- the element tiles --------------------------------------------------

        private static readonly Dictionary<UnitDomains.Domain, Sprite> _domainSprites = new Dictionary<UnitDomains.Domain, Sprite>();

        private static Sprite DomainSprite(UnitDomains.Domain domain)
        {
            if (_domainSprites.TryGetValue(domain, out var sprite) && sprite != null) return sprite;
            var texture = UnitRow.Tile(domain);
            sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _domainSprites[domain] = sprite;
            return sprite;
        }

        // ---- mirroring ------------------------------------------------------------

        /// Takes the given game button's look for this frame: its art (or
        /// the HUD's element tile behind the portrait), count, progress and
        /// whether it can be clicked; the badge when asked for.
        internal void Mirror(UnitButtonElement source, float width, bool badge)
        {
            _source = source;
            _layout.preferredWidth = width;
            _layout.preferredHeight = Size;
            _layout.minWidth = width;
            _layout.minHeight = Size;

            var portrait = source.portraitImage != null ? source.portraitImage.overrideSprite : null;
            var domain = UnitDomains.Enabled != null && UnitDomains.Enabled.Value ? UnitDomains.Of(portrait) : UnitDomains.Domain.Unknown;

            if (_background != null)
            {
                if (domain != UnitDomains.Domain.Unknown)
                {
                    _background.sprite = DomainSprite(domain);
                    _background.color = Color.white;
                }
                else
                {
                    var src = source.background;
                    var plate = src != null ? src.overrideSprite : null;
                    _background.sprite = plate != null ? plate : DomainSprite(UnitDomains.Domain.Unknown);
                    _background.color = src != null && plate != null ? src.color : Color.white;
                }
            }
            if (_portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.color = source.portraitImage != null ? source.portraitImage.color : Color.white;
                _portrait.enabled = portrait != null;
            }
            if (_icon != null)
            {
                var src = source.strategicIconImage;
                var on = src != null && src.gameObject.activeSelf && src.enabled;
                if (_icon.gameObject.activeSelf != on) _icon.gameObject.SetActive(on);
                if (on)
                {
                    _icon.sprite = src.overrideSprite;
                    _icon.color = src.color;
                }
            }

            // The count, or a hotkey ("?" is the game's none).
            var text = source.textOverlayText != null ? source.textOverlayText.text : null;
            var showText = !string.IsNullOrEmpty(text) && text != "?" &&
                           (source.textOverlayContainer == null || source.textOverlayContainer.activeSelf);
            if (_textContainer != null && _textContainer.activeSelf != showText) _textContainer.SetActive(showText);
            if (_text != null && showText) _text.text = text;

            // Progress, where the game shows it (the item being built).
            var bar = source.progressBar;
            var showBar = bar != null && bar.gameObject.activeSelf && bar.fillAmount > 0f;
            if (_progress != null)
            {
                if (_progress.gameObject.activeSelf != showBar) _progress.gameObject.SetActive(showBar);
                if (showBar)
                {
                    _progress.fillAmount = bar.fillAmount;
                    _progress.fillCenter = false;
                }
            }

            if (_button != null)
            {
                var sourceButton = source.GetComponent<Button>();
                _button.interactable = sourceButton == null || sourceButton.interactable;
            }

            var edge = UnitRow.EdgeFor(domain);
            edge.a = _hover ? 1f : 0.7f;
            _edge.color = edge;

            // The type's strategic icon — the same image the map draws over
            // the unit, out of the game's icon atlas — for telling one type
            // from the next in a mixed selection.
            var showBadge = false;
            if (badge)
            {
                EnsureIconRegistry();
                var index = IconIndexByName(UnitDomains.IconOf(portrait));
                if (index >= 0 && IconAtlasUv(index, out var atlas, out var uv))
                {
                    _badge.texture = atlas;
                    _badge.uvRect = uv;
                    _badge.color = Color.white;
                    showBadge = true;
                }
            }
            if (_badge.gameObject.activeSelf != showBadge) _badge.gameObject.SetActive(showBadge);
        }

        // ---- pointer events, passed to the game's button -----------------------

        public void OnPointerDown(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerDownHandler);

        public void OnPointerUp(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerUpHandler);

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hover = true;
            Hovered = this;
            if (_hoverOverlay != null) _hoverOverlay.SetActive(true);
            Forward(eventData, ExecuteEvents.pointerEnterHandler);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Leave(eventData);
        }

        private void OnDisable()
        {
            // A row taken down under the mouse still owes the game's button
            // its leave, or the unit card keeps the build figures up.
            if (_hover) Leave(new PointerEventData(EventSystem.current) { position = Input.mousePosition });
        }

        private void Leave(PointerEventData eventData)
        {
            if (!_hover) return;
            _hover = false;
            if (Hovered == this) Hovered = null;
            if (_hoverOverlay != null) _hoverOverlay.SetActive(false);
            Forward(eventData, ExecuteEvents.pointerExitHandler);
        }

        private static bool _createLogged;
        private static bool _forwardLogged;

        private void Forward<T>(PointerEventData eventData, ExecuteEvents.EventFunction<T> handler) where T : IEventSystemHandler
        {
            if (_source == null) return;
            try
            {
                ExecuteEvents.Execute(_source.gameObject, eventData, handler);
            }
            catch (Exception e)
            {
                if (!_forwardLogged)
                {
                    _forwardLogged = true;
                    _log?.LogWarning($"Unit tile: the game's button threw (logged once): {e}");
                }
            }
        }
    }
}
