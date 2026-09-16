using System;
using SanctuaryUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // One of the game's tier tabs (T1, T2, …), cloned: a copy of the filter
    // panel's own toggle prefab with the game's element script taken off
    // and this one put on. It mirrors a live tab on the concealed panel —
    // its text, whether it is on, whether it can be clicked, and the colours
    // the game's script would have given it — and passes it every pointer
    // event. The Lua handler on the release turns the other tabs off and
    // leaves turning this one on to the uGUI Toggle's own click, so the
    // click is passed on too, after the release, as a real press would.
    internal sealed class TabTile : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private ConstructionFilterToggleElement _source;
        private Toggle _toggle;
        private TMP_Text _text;
        private LayoutElement _layout;
        private ColorBlock _on, _off;
        private bool _haveColours;
        private Vector2 _native = new Vector2(80f, 44f);

        internal ConstructionFilterToggleElement Source => _source;

        /// A clone of the panel's toggle prefab, stripped and ready, under
        /// parent and active. Null when the prefab can't be cloned.
        internal static TabTile Create(SanctuaryPanelUI panel, Transform parent)
        {
            var prefab = panel != null ? panel.buttonPrefab : null;
            var holder = HudCanvas.Holder;
            if (prefab == null || holder == null) return null;
            GameObject go = null;
            try
            {
                go = Instantiate(prefab, holder);
                go.name = "Tab";
                var tab = go.AddComponent<TabTile>();
                if (prefab.transform is RectTransform prt && prt.rect.width > 1f && prt.rect.height > 1f) tab._native = prt.rect.size;

                var element = go.GetComponent<ConstructionFilterToggleElement>();
                if (element != null)
                {
                    tab._text = element.displayText;
                    tab._toggle = element.toggle;
                    tab._on = element.toggledOnColors;
                    tab._off = element.toggledOffColors;
                    tab._haveColours = true;
                    DestroyImmediate(element);
                }
                foreach (var trigger in go.GetComponentsInChildren<TooltipTrigger>(true)) DestroyImmediate(trigger);

                if (tab._toggle == null) tab._toggle = go.GetComponent<Toggle>();
                if (tab._toggle != null)
                {
                    tab._toggle.group = null;
                    tab._toggle.onValueChanged.RemoveAllListeners();
                    if (tab._toggle.targetGraphic != null) tab._toggle.targetGraphic.raycastTarget = true;
                }
                tab._layout = go.GetComponent<LayoutElement>();
                if (tab._layout == null) tab._layout = go.AddComponent<LayoutElement>();
                tab._layout.ignoreLayout = false;
                tab._layout.preferredWidth = tab._native.x;
                tab._layout.preferredHeight = tab._native.y;
                tab._layout.minWidth = tab._native.x;
                tab._layout.minHeight = tab._native.y;
                go.transform.localScale = Vector3.one;

                go.transform.SetParent(parent, false);
                go.SetActive(true);
                return tab;
            }
            catch (Exception e)
            {
                if (go != null) Destroy(go);
                if (!_createLogged)
                {
                    _createLogged = true;
                    _log?.LogWarning($"Tier tab: the game's tab prefab could not be cloned (logged once): {e}");
                }
                return null;
            }
        }

        private static bool _createLogged;

        internal void Mirror(ConstructionFilterToggleElement source)
        {
            _source = source;
            if (_text != null && source.displayText != null) _text.text = source.displayText.text;
            var sourceToggle = source.toggle != null ? source.toggle : source.GetComponent<Toggle>();
            if (_toggle != null && sourceToggle != null)
            {
                var on = sourceToggle.isOn;
                if (_toggle.isOn != on) _toggle.SetIsOnWithoutNotify(on);
                _toggle.interactable = sourceToggle.interactable;
                if (_haveColours) _toggle.colors = on ? _on : _off;
            }
        }

        public void OnPointerDown(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerDownHandler);
        public void OnPointerUp(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerUpHandler);
        public void OnPointerClick(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerClickHandler);
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
                    _log?.LogWarning($"Tier tab: the game's tab threw (logged once): {e}");
                }
            }
        }
    }
}
