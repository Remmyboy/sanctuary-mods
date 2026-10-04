using System;
using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // One of the game's order buttons, cloned: a copy of the orders panel's
    // own button prefab — the dashed background in the order's colour, the
    // icon, and the frame the game's glow shader lights when the toggle is
    // on or the mouse is over it — with the game's element script taken off
    // and this one put on. It mirrors a live button on the concealed panel
    // every frame (art, tint, the frame's colour block that carries the
    // toggle state, whether it can be clicked) and passes it every pointer
    // event, so whatever Lua hung on the button runs unchanged.
    //
    // The button's name shows as the game's own tooltip: the game's trigger
    // on the source button where it has one (reached by the forwarded
    // hover), else the HUD's own trigger on the clone with the row's label.
    internal sealed class OrderTile : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private OrderButtonElement _source;
        private Image _background, _frame, _icon;
        private Button _button;
        private LayoutElement _layout;
        private TooltipTrigger _tooltip;
        /// The game lays its buttons in 80-unit grid cells (the prefab's root
        /// is 100, its art overhanging), so the clone takes the cell, not the
        /// root, and the art draws at the size it does on the game's panel.
        private const float Cell = 80f;

        internal static OrderTile Create(SanctuaryPanelUI panel, Transform parent)
        {
            var prefab = panel != null ? panel.buttonPrefab : null;
            var holder = HudCanvas.Holder;
            if (prefab == null || holder == null) return null;
            GameObject go = null;
            try
            {
                go = Instantiate(prefab, holder);
                go.name = "Order";
                var tile = go.AddComponent<OrderTile>();
                HoverGlow.Add(go);

                var element = go.GetComponent<OrderButtonElement>();
                if (element != null)
                {
                    tile._background = element.background;
                    tile._frame = element.frame;
                    tile._icon = element.icon;
                    DestroyImmediate(element);
                }
                foreach (var trigger in go.GetComponentsInChildren<TooltipTrigger>(true)) DestroyImmediate(trigger);

                tile._button = go.GetComponent<Button>();
                if (tile._button != null) tile._button.onClick.RemoveAllListeners();
                if (tile._background != null) tile._background.raycastTarget = true;
                tile._layout = go.GetComponent<LayoutElement>();
                if (tile._layout == null) tile._layout = go.AddComponent<LayoutElement>();
                tile._layout.ignoreLayout = false;
                tile._layout.preferredWidth = Cell;
                tile._layout.preferredHeight = Cell;
                tile._layout.minWidth = Cell;
                tile._layout.minHeight = Cell;
                go.transform.localScale = Vector3.one;

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
                    _log?.LogWarning($"Order tile: the game's button prefab could not be cloned (logged once): {e}");
                }
                return null;
            }
        }

        private static bool _createLogged;

        // The source's own Button and whether it has a tooltip, looked up
        // when the source changes rather than every frame.
        private Button _sourceButton;
        private bool _sourceHasTooltip;

        internal void Mirror(OrderButtonElement source, string caption)
        {
            if (source != _source)
            {
                _source = source;
                _sourceButton = source.GetComponent<Button>();
                _sourceHasTooltip = source.GetComponent<TooltipTrigger>() != null;
            }
            Copy(source.background, _background);
            Copy(source.frame, _frame);
            // The glyph on its own, without the game's glow: drawn plain,
            // white, in the tint the game gave it. Handed to Copy rather than
            // set after it, as a sprite set twice a frame dirties the canvas.
            var glyph = source.icon != null ? Glyph(source.icon.overrideSprite) : null;
            Copy(source.icon, _icon, glyph);
            if (glyph != null && _icon != null) _icon.material = null;
            if (_button != null && _sourceButton != null)
            {
                _button.colors = _sourceButton.colors;
                _button.interactable = _sourceButton.interactable;
            }

            if (!_sourceHasTooltip)
            {
                if (_tooltip == null) _tooltip = gameObject.AddComponent<TooltipTrigger>();
                _tooltip.usesDictionaryTitle = false;
                _tooltip.usesDictionaryDescription = false;
                _tooltip.title = caption;
                _tooltip.description = "";
            }
            else if (_tooltip != null)
            {
                Destroy(_tooltip);
                _tooltip = null;
            }
        }

        // ---- the glyph without the glow ------------------------------------------
        //
        // The game's order icons are not pictures: the sprite holds the glyph
        // in its red channel and a halo region in its green, and the
        // ButtonIconGlow shader (which exposes no strength to turn down)
        // blooms the halo over the glyph until, at this size, the glyph is a
        // white blob. So the glyph channel is lifted out once per icon into a
        // sprite of its own — white where the glyph is, clear elsewhere — and
        // drawn with the plain UI material. The atlas isn't readable, so it
        // goes through a render texture, and only the icon's own rectangle
        // comes back: no copy of the whole atlas is kept on the CPU.

        private static readonly Dictionary<Sprite, Sprite> _glyphs = new Dictionary<Sprite, Sprite>();
        private static bool _glyphFailed;

        private static Sprite Glyph(Sprite source)
        {
            if (source == null || _glyphFailed) return null;
            if (_glyphs.TryGetValue(source, out var made)) return made;
            try
            {
                var atlas = source.texture;
                if (atlas == null) return null;
                var rect = source.textureRect;
                var x = Mathf.RoundToInt(rect.x);
                var y = Mathf.RoundToInt(rect.height < 0f ? rect.y + rect.height : rect.y);
                var w = Mathf.RoundToInt(Mathf.Abs(rect.width));
                var h = Mathf.RoundToInt(Mathf.Abs(rect.height));
                if (w < 2 || h < 2) return null;

                var glyph = ReadBack(atlas, x, y, w, h);
                var pixels = glyph.GetPixels();
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    pixels[i] = new Color(1f, 1f, 1f, p.r * p.a);
                }
                glyph.SetPixels(pixels);
                glyph.Apply(false, true);
                made = Generated.Keep(Sprite.Create(glyph, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect));
                _glyphs[source] = made;
                if (_glyphs.Count == 1) _log?.LogInfo(FormattableString.Invariant($"Order glyphs: lifted out of '{atlas.name}' ({w}x{h}, rect y {rect.y:0} h {rect.height:0})."));
                return made;
            }
            catch (Exception e)
            {
                _glyphFailed = true;
                _log?.LogWarning($"Order glyphs could not be lifted out of the atlas; the game's glow stays ({e.Message}).");
                return null;
            }
        }

        /// A CPU copy of one rectangle (texture pixels, from the bottom-left,
        /// as GetPixels counts them) of a texture the CPU can't read, by way
        /// of the GPU: kept (Generated), readable, for the caller to fill.
        private static Texture2D ReadBack(Texture texture, int x, int y, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var copy = Generated.Keep(new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                });
                copy.ReadPixels(new Rect(x, y, w, h), 0, 0);
                copy.Apply(false, false);
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// For unload: the copies go with the assembly's objects.
        internal static void ReleaseGlyphs()
        {
            foreach (var glyph in _glyphs.Values)
            {
                if (glyph == null) continue;
                if (glyph.texture != null) UnityEngine.Object.Destroy(glyph.texture);
                UnityEngine.Object.Destroy(glyph);
            }
            _glyphs.Clear();
            _glyphFailed = false;
        }

        private static void Copy(Image from, Image to, Sprite instead = null)
        {
            if (to == null) return;
            if (from == null)
            {
                if (to.enabled) to.enabled = false;
                return;
            }
            var sprite = from.overrideSprite;
            to.sprite = instead != null ? instead : sprite;
            to.color = from.color;
            var on = sprite != null && from.enabled && from.gameObject.activeSelf;
            if (to.enabled != on) to.enabled = on;
        }

        public void OnPointerDown(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerDownHandler);
        public void OnPointerUp(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerUpHandler);
        private bool _hover;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hover = true;
            Forward(eventData, ExecuteEvents.pointerEnterHandler);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hover = false;
            Forward(eventData, ExecuteEvents.pointerExitHandler);
        }

        private void OnDisable()
        {
            // A row taken down under the mouse still owes the game's button
            // its leave, or its tooltip and glow stay up.
            if (!_hover) return;
            _hover = false;
            Forward(new PointerEventData(EventSystem.current) { position = Input.mousePosition }, ExecuteEvents.pointerExitHandler);
        }

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
                    _log?.LogWarning($"Order tile: the game's button threw (logged once): {e}");
                }
            }
        }
    }
}
