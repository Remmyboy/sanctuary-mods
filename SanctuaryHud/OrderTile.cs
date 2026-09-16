using System;
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

        internal OrderButtonElement Source => _source;

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

        internal void Mirror(OrderButtonElement source, string caption)
        {
            _source = source;
            Copy(source.background, _background);
            Copy(source.frame, _frame);
            Copy(source.icon, _icon);
            if (_button != null)
            {
                var sourceButton = source.GetComponent<Button>();
                if (sourceButton != null)
                {
                    _button.colors = sourceButton.colors;
                    _button.interactable = sourceButton.interactable;
                }
            }

            if (source.GetComponent<TooltipTrigger>() == null)
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

        private static void Copy(Image from, Image to)
        {
            if (to == null) return;
            if (from == null)
            {
                if (to.enabled) to.enabled = false;
                return;
            }
            var sprite = from.overrideSprite;
            to.sprite = sprite;
            to.color = from.color;
            var on = sprite != null && from.enabled && from.gameObject.activeSelf;
            if (to.enabled != on) to.enabled = on;
        }

        public void OnPointerDown(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerDownHandler);
        public void OnPointerUp(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerUpHandler);
        public void OnPointerEnter(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerEnterHandler);
        public void OnPointerExit(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerExitHandler);

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
