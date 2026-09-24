using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The economy strip across the top and the commander widget under its
    // right end, on the HUD canvas.
    //
    // Full-width strip: alloy on the left, energy on the right. Each half
    // shows storage on top with gross in / gross out / net beside it, and a
    // capacity bar underneath whose length scales gently with storage size
    // and whose colour warns as the store heads for empty. With the game's
    // panel hidden, its buttons sit between the two halves as clones of the
    // game's own, each click passed to the original.
    //
    // Commander widget, top-right under the strip: the game's own strategic
    // icon with a health bar underneath, always visible so a commander under
    // fire is obvious; click to select the commander and fly the camera to it.
    //
    // Both are laid out by hand in canvas units (the IMGUI figures doubled)
    // on one container that carries the size setting, so the setting sizes
    // them without touching anything else.
    internal static class EcoStrip
    {
        /// The strip's height in canvas units.
        internal const float Height = 96f;
        private const float Pad = 32f;
        private const float ControlSize = 52f;

        private static RectTransform _root, _strip;
        private static Half _alloy, _energy;
        private static Middle _middle;
        private static Image _leftLine, _rightLine;
        private static Commander _commander;
        private static bool _syncLogged;

        /// From Update: shows the strip and the widget, filled from the
        /// latest economy update and the commander's health, or hides them.
        internal static void Tick(bool showing, float scale)
        {
            try
            {
                Sync(showing, scale);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Economy strip could not be laid out (logged once): {e}");
                }
                if (_root != null) _root.gameObject.SetActive(false);
            }
        }

        internal static void Shutdown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
        }

        private static void Sync(bool showing, float scale)
        {
            Dictionary<string, float> eco;
            lock (_ecoLock) eco = _eco;
            if (!showing || eco == null)
            {
                if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);
                return;
            }
            var canvasRoot = HudCanvas.Ensure();
            if (canvasRoot == null) return;
            if (_root == null) Build(canvasRoot);
            if (!_root.gameObject.activeSelf) _root.gameObject.SetActive(true);

            // The container spans the screen at 1/scale and is scaled up,
            // so everything on it is laid out in unscaled units.
            var size = HudCanvas.Size;
            var width = size.x / scale;
            _root.localScale = new Vector3(scale, scale, 1f);
            _root.sizeDelta = new Vector2(width, size.y / scale);
            _strip.sizeDelta = new Vector2(width, Height);

            // The game's buttons in the middle, when its panel is hidden.
            var middle = _middle.Sync(GamePanel.Controls, GamePanel.VersionText);
            var half = (width - middle) / 2f;
            _alloy.Sync(eco, "alloy", SanctuaryHudPlugin.AlloyTint, 0f, half);
            _energy.Sync(eco, "energy", SanctuaryHudPlugin.EnergyTint, half + middle, half);
            _middle.Place(half, middle);
            _leftLine.rectTransform.anchoredPosition = new Vector2(half - 1f, -20f);
            var showRight = middle > 0f;
            if (_rightLine.gameObject.activeSelf != showRight) _rightLine.gameObject.SetActive(showRight);
            if (showRight) _rightLine.rectTransform.anchoredPosition = new Vector2(half + middle - 1f, -20f);

            _commander.Sync();
        }

        private static void Build(RectTransform canvasRoot)
        {
            var go = new GameObject("Economy strip", typeof(RectTransform));
            go.transform.SetParent(canvasRoot, false);
            _root = (RectTransform)go.transform;
            _root.anchorMin = new Vector2(0f, 1f);
            _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = Vector2.zero;

            var plate = HudCanvas.Fill(_root, "Strip", PanelColour);
            plate.raycastTarget = true;
            _strip = plate.rectTransform;
            _strip.anchorMin = new Vector2(0f, 1f);
            _strip.anchorMax = new Vector2(0f, 1f);
            _strip.pivot = new Vector2(0f, 1f);
            _strip.anchoredPosition = Vector2.zero;

            _alloy = Half.Create(_strip, "Alloy", "alloy", "ALLOY", SanctuaryHudPlugin.AlloyTint);
            _energy = Half.Create(_strip, "Energy", "energy", "ENERGY", SanctuaryHudPlugin.EnergyTint);
            _middle = Middle.Create(_strip);

            // The game's panels sit on a hairline of accent blue; give the
            // strip the same edge, with a short fade under it so it lifts
            // off the map rather than ending in a hard line. Short lines
            // mark the halves.
            var accent = AccentColour;
            accent.a = 0.35f;
            _leftLine = VerticalLine(_strip, accent);
            _rightLine = VerticalLine(_strip, accent);
            accent.a = 0.6f;
            var bottom = HudCanvas.Fill(_strip, "Edge", accent);
            HudCanvas.StretchAlongBottom(bottom.rectTransform, 2f);
            for (var i = 0; i < 4; i++)
            {
                var fade = HudCanvas.Fill(_strip, "Fade", new Color(0f, 0f, 0f, 0.28f - i * 0.07f));
                var frt = fade.rectTransform;
                frt.anchorMin = new Vector2(0f, 0f);
                frt.anchorMax = new Vector2(1f, 0f);
                frt.pivot = new Vector2(0.5f, 1f);
                frt.offsetMin = new Vector2(0f, -2f * (i + 1));
                frt.offsetMax = new Vector2(0f, -2f * i);
            }

            _commander = Commander.Create(_root);
        }

        private static Image VerticalLine(Transform parent, Color colour)
        {
            var line = HudCanvas.Fill(parent, "Line", colour);
            var rt = line.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(2f, Height - 40f);
            return line;
        }

        /// A text anchored top-left at a position, of a size.
        private static TMP_Text At(Transform parent, string name, float size, Color colour, TextAlignmentOptions alignment, Vector2 at, Vector2 box)
        {
            var text = HudCanvas.Text(parent, name, size, colour, alignment);
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(at.x, -at.y);
            rt.sizeDelta = box;
            return text;
        }

        // ---- one half ---------------------------------------------------------------

        private sealed class Half
        {
            private RectTransform _rect;
            private Color _tint;
            private RectTransform _storageRow;
            private TMP_Text _storage, _max, _net, _income, _spent, _chip;
            private Image _track, _fill, _tip, _chipBox;
            private float _lead;

            internal static Half Create(Transform parent, string name, string key, string label, Color tint)
            {
                var half = new Half { _tint = tint };
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                half._rect = rt;

                // The resource's mark, large, in place of its name: the
                // game's own icon, or the word where it has none.
                var markBox = new GameObject("Mark", typeof(RectTransform));
                markBox.transform.SetParent(rt, false);
                var mrt = (RectTransform)markBox.transform;
                mrt.anchorMin = mrt.anchorMax = new Vector2(0f, 1f);
                mrt.pivot = new Vector2(0f, 1f);
                mrt.anchoredPosition = new Vector2(Pad, -8f);
                mrt.sizeDelta = new Vector2(44f, 44f);
                var icon = HudCanvas.Icon(mrt, key, 44f, tint);
                if (icon != null)
                {
                    var irt = icon.rectTransform;
                    irt.anchorMin = Vector2.zero;
                    irt.anchorMax = Vector2.one;
                    irt.offsetMin = Vector2.zero;
                    irt.offsetMax = Vector2.zero;
                    half._lead = 60f;
                }
                else
                {
                    mrt.sizeDelta = new Vector2(140f, 40f);
                    var word = HudCanvas.Text(mrt, "Label", 28f, tint, TextAlignmentOptions.MidlineLeft);
                    var wrt = word.rectTransform;
                    wrt.anchorMin = Vector2.zero;
                    wrt.anchorMax = Vector2.one;
                    wrt.offsetMin = Vector2.zero;
                    wrt.offsetMax = Vector2.zero;
                    HudCanvas.SetText(word, label);
                    half._lead = 132f;
                }

                // Storage, then "/ limit" after it, sized to the figure.
                var row = new GameObject("Storage", typeof(RectTransform));
                row.transform.SetParent(rt, false);
                half._storageRow = (RectTransform)row.transform;
                half._storageRow.anchorMin = half._storageRow.anchorMax = new Vector2(0f, 1f);
                half._storageRow.pivot = new Vector2(0f, 1f);
                half._storageRow.anchoredPosition = new Vector2(Pad + half._lead, -2f);
                var group = row.AddComponent<HorizontalLayoutGroup>();
                group.spacing = 12f;
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                var fitter = row.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                half._storage = HudCanvas.Text(row.transform, "Value", 44f, Color.white, TextAlignmentOptions.MidlineLeft);
                half._max = HudCanvas.Text(row.transform, "Max", 28f, SanctuaryHudPlugin.MutedText, TextAlignmentOptions.MidlineLeft);

                // Right cluster: net on the right, with gross in stacked over
                // gross out beside it, so the two figures that are compared
                // line up under each other rather than reading across.
                half._net = At(rt, "Net", 38f, GainColour, TextAlignmentOptions.MidlineRight, new Vector2(0f, 22f), new Vector2(216f, 44f));
                half._income = At(rt, "In", 30f, GainColour, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 10f), new Vector2(128f, 32f));
                half._spent = At(rt, "Out", 30f, LossColour, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 46f), new Vector2(128f, 32f));

                // Capacity bar: a thin line in the resource colour on an
                // accent-tinted track, the way the game draws its own gauges.
                var track = AccentColour;
                track.a = 0.14f;
                half._track = HudCanvas.Fill(rt, "Track", track);
                var trt = half._track.rectTransform;
                trt.anchorMin = trt.anchorMax = new Vector2(0f, 1f);
                trt.pivot = new Vector2(0f, 1f);
                trt.anchoredPosition = new Vector2(Pad, -72f);
                half._fill = HudCanvas.Fill(trt, "Fill", tint);
                var frt = half._fill.rectTransform;
                frt.anchorMin = new Vector2(0f, 0f);
                frt.anchorMax = new Vector2(0f, 1f);
                frt.pivot = new Vector2(0f, 0.5f);
                frt.anchoredPosition = Vector2.zero;
                half._tip = HudCanvas.Fill(trt, "Tip", new Color(1f, 1f, 1f, 0.75f));
                var tprt = half._tip.rectTransform;
                tprt.anchorMin = tprt.anchorMax = new Vector2(0f, 0.5f);
                tprt.pivot = new Vector2(1f, 0.5f);
                tprt.sizeDelta = new Vector2(2f, 12f);

                // Warning chip at the end of the bar row, short of the flows.
                half._chipBox = HudCanvas.Fill(rt, "Chip", DangerColour);
                var crt = half._chipBox.rectTransform;
                crt.anchorMin = crt.anchorMax = new Vector2(0f, 1f);
                crt.pivot = new Vector2(1f, 1f);
                var cgroup = half._chipBox.gameObject.AddComponent<HorizontalLayoutGroup>();
                cgroup.padding = new RectOffset(7, 7, 0, 0);
                cgroup.childAlignment = TextAnchor.MiddleCenter;
                cgroup.childControlWidth = true;
                cgroup.childControlHeight = true;
                cgroup.childForceExpandWidth = false;
                cgroup.childForceExpandHeight = false;
                var cfitter = half._chipBox.gameObject.AddComponent<ContentSizeFitter>();
                cfitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                cfitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                half._chip = HudCanvas.Text(crt, "Text", 24f, Color.white, TextAlignmentOptions.Center);
                half._chipBox.gameObject.SetActive(false);
                return half;
            }

            internal void Sync(Dictionary<string, float> eco, string key, Color tint, float x, float w)
            {
                _tint = tint;
                _rect.anchoredPosition = new Vector2(x, 0f);
                _rect.sizeDelta = new Vector2(w, Height);

                float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;
                var current = V("StorageCurrent");
                var limit = Mathf.Max(1f, V("StorageLimit"));
                var incomeRaw = V("GeneratedIncome");
                var wantedRaw = -V("RequestedTotal");
                var spendRaw = -V("RequestedStalled");
                var stalling = SanctuaryHudPlugin.IsStalling(current, incomeRaw, wantedRaw, spendRaw);

                var s = SanctuaryHudPlugin.Smoothed(key) ?? new[] { incomeRaw, wantedRaw, spendRaw };
                var income = s[0];
                // While this resource is the one stalling, the spend figure
                // shows demand, not what the economy managed to pay: actual
                // spend is then capped by income, so it would just mirror the
                // income back at you and hide the shortfall. Otherwise it
                // shows actual spend, as the game's panel does.
                var spent = stalling ? s[1] : s[2];
                // Net stays on actual spend: it describes the store's real
                // movement, which is what the bar and the "empty in" chip need.
                var net = s[0] - s[2];

                HudCanvas.SetText(_storage, SanctuaryHudPlugin.Fmt(current));
                HudCanvas.SetText(_max, "/ " + SanctuaryHudPlugin.Fmt(limit));

                var netText = (net >= 0f ? "+" : "−") + SanctuaryHudPlugin.Fmt(net) + "/s";
                _net.color = stalling ? DangerColour : net >= 0f ? GainColour : LossColour;
                HudCanvas.SetText(_net, netText);
                _net.rectTransform.anchoredPosition = new Vector2(w - Pad - 216f, -22f);

                var flowsX = w - Pad - 216f - 12f - 128f;
                _income.rectTransform.anchoredPosition = new Vector2(flowsX, -10f);
                _spent.rectTransform.anchoredPosition = new Vector2(flowsX, -46f);
                HudCanvas.SetText(_income, "+" + SanctuaryHudPlugin.Fmt(income));
                // Flag the spend figure while stalling, since it is then
                // demand you are not actually meeting rather than resources
                // leaving the store.
                _spent.color = stalling ? DangerColour : LossColour;
                HudCanvas.SetText(_spent, "−" + SanctuaryHudPlugin.Fmt(spent));

                // The bar's length scales gently with the storage size and
                // stops short of the flows column whatever that size.
                var inner = w - Pad * 2f;
                var lengthFactor = Mathf.Clamp(0.45f + 0.15f * Mathf.Log10(limit / 400f), 0.45f, 1f);
                var barMax = flowsX - 24f - Pad;
                var barWidth = Mathf.Max(0f, Mathf.Min(inner * lengthFactor, barMax));
                _track.rectTransform.sizeDelta = new Vector2(barWidth, 8f);
                var fillWidth = barWidth * Mathf.Clamp01(current / limit);
                _fill.rectTransform.sizeDelta = new Vector2(fillWidth, 0f);
                _fill.color = SanctuaryHudPlugin.FillColour(_tint, current, net, stalling);
                var showTip = fillWidth > 4f;
                if (_tip.gameObject.activeSelf != showTip) _tip.gameObject.SetActive(showTip);
                if (showTip) _tip.rectTransform.anchoredPosition = new Vector2(fillWidth, 0f);

                string chip = null;
                if (stalling) chip = "STALL −" + SanctuaryHudPlugin.Fmt(wantedRaw - spendRaw) + "/s";
                else if (net < -0.5f)
                {
                    var tte = current / -net;
                    if (tte < 120f) chip = "EMPTY IN " + tte.ToString("0") + "s";
                }
                var showChip = chip != null;
                if (_chipBox.gameObject.activeSelf != showChip) _chipBox.gameObject.SetActive(showChip);
                if (showChip)
                {
                    HudCanvas.SetText(_chip, chip);
                    _chipBox.color = stalling ? DangerColour : new Color(0.75f, 0.45f, 0.15f, 0.9f);
                    _chipBox.rectTransform.anchoredPosition = new Vector2(flowsX - 20f, -58f);
                }
            }
        }

        // ---- the game's buttons in the middle -------------------------------------

        // The buttons from the game's hidden panel: clones of its own, in a
        // row, with its version line under them, which gives way to the name
        // of the button under the mouse. A click is passed on to the original.
        private sealed class Middle
        {
            private RectTransform _rect, _row;
            private TMP_Text _caption;
            private readonly List<ControlTile> _tiles = new List<ControlTile>();
            private readonly List<GamePanel.PanelControl> _shown = new List<GamePanel.PanelControl>();
            private string _versionText;

            internal static Middle Create(Transform parent)
            {
                var middle = new Middle();
                var go = new GameObject("Controls", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                middle._rect = (RectTransform)go.transform;
                middle._rect.anchorMin = middle._rect.anchorMax = new Vector2(0f, 1f);
                middle._rect.pivot = new Vector2(0f, 1f);
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.padding = new RectOffset(28, 28, 8, 0);
                column.spacing = 4f;
                column.childAlignment = TextAnchor.UpperCenter;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = false;
                column.childForceExpandHeight = false;
                var fitter = go.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

                var row = new GameObject("Buttons", typeof(RectTransform));
                row.transform.SetParent(go.transform, false);
                var group = row.AddComponent<HorizontalLayoutGroup>();
                group.spacing = 8f;
                group.childAlignment = TextAnchor.MiddleCenter;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                middle._row = (RectTransform)row.transform;

                middle._caption = HudCanvas.Text(go.transform, "Caption", 22f, SanctuaryHudPlugin.MutedText, TextAlignmentOptions.Center);
                go.SetActive(false);
                return middle;
            }

            /// Fills the middle from the hidden panel's controls; returns
            /// the width it needs, 0 when there is nothing to show.
            internal float Sync(List<GamePanel.PanelControl> controls, string versionText)
            {
                var show = controls.Count > 0;
                if (_rect.gameObject.activeSelf != show) _rect.gameObject.SetActive(show);
                if (!show) return 0f;

                var same = controls.Count == _shown.Count;
                for (var i = 0; same && i < controls.Count; i++) same = controls[i] == _shown[i];
                if (!same)
                {
                    foreach (var tile in _tiles) if (tile != null) UnityEngine.Object.Destroy(tile.gameObject);
                    _tiles.Clear();
                    _shown.Clear();
                    foreach (var control in controls)
                    {
                        var tile = ControlTile.Create(_row, control);
                        if (tile == null) continue;
                        _tiles.Add(tile);
                        _shown.Add(control);
                    }
                }

                string caption = versionText;
                foreach (var tile in _tiles) if (tile != null && tile.Hovered) caption = tile.Control.Label;
                HudCanvas.SetText(_caption, caption ?? "");
                _versionText = versionText;
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
                return _rect.rect.width;
            }

            internal void Place(float x, float width)
            {
                _rect.anchoredPosition = new Vector2(x, 0f);
                _rect.sizeDelta = new Vector2(width, Height);
            }
        }

        /// One of the hidden panel's buttons, cloned: the game's own plate and
        /// symbol with the Button taken off, an accent wash behind it that
        /// brightens under the mouse, and the click passed to the original.
        private sealed class ControlTile : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            internal GamePanel.PanelControl Control;
            internal bool Hovered;
            private Image _wash, _edge;

            internal static ControlTile Create(Transform parent, GamePanel.PanelControl control)
            {
                if (control == null || control.Target == null || HudCanvas.Holder == null) return null;
                GameObject go = null;
                try
                {
                    go = Instantiate(control.Target, HudCanvas.Holder);
                    go.name = "Control " + control.Label;
                    // Only the look: the game's Button and anything scripted
                    // stay on the original, which gets the click.
                    foreach (var behaviour in go.GetComponentsInChildren<Behaviour>(true))
                    {
                        // (CanvasRenderer is a Component, never listed here.)
                        if (!(behaviour is Graphic)) DestroyImmediate(behaviour);
                    }
                    foreach (var graphic in go.GetComponentsInChildren<Graphic>(true))
                    {
                        graphic.enabled = true;
                        graphic.raycastTarget = false;
                    }
                    var tile = go.AddComponent<ControlTile>();
                    tile.Control = control;
                    var layout = go.AddComponent<LayoutElement>();
                    layout.preferredWidth = ControlSize;
                    layout.preferredHeight = ControlSize;
                    layout.minWidth = ControlSize;
                    layout.minHeight = ControlSize;
                    var rt = (RectTransform)go.transform;
                    rt.localScale = Vector3.one;

                    var wash = AccentColour;
                    wash.a = 0.10f;
                    tile._wash = HudCanvas.Fill(rt, "Wash", wash);
                    tile._wash.raycastTarget = true;
                    var wrt = tile._wash.rectTransform;
                    wrt.anchorMin = Vector2.zero;
                    wrt.anchorMax = Vector2.one;
                    wrt.offsetMin = Vector2.zero;
                    wrt.offsetMax = Vector2.zero;
                    tile._wash.transform.SetSiblingIndex(0);
                    var edge = AccentColour;
                    edge.a = 0.45f;
                    tile._edge = HudCanvas.Fill(rt, "Edge", edge);
                    HudCanvas.StretchAlongBottom(tile._edge.rectTransform, 2f);

                    go.transform.SetParent(parent, false);
                    go.SetActive(true);
                    return tile;
                }
                catch (Exception e)
                {
                    if (go != null) Destroy(go);
                    _log?.LogWarning($"Strip {control.Label} button could not be cloned ({e.Message}).");
                    return null;
                }
            }

            public void OnPointerEnter(PointerEventData eventData) => SetHover(true);
            public void OnPointerExit(PointerEventData eventData) => SetHover(false);
            private void OnDisable() => SetHover(false);

            private void SetHover(bool on)
            {
                Hovered = on;
                var wash = AccentColour;
                wash.a = on ? 0.24f : 0.10f;
                _wash.color = wash;
                var edge = AccentColour;
                edge.a = on ? 0.8f : 0.45f;
                _edge.color = edge;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left) GamePanel.Click(Control, _log);
            }
        }

        // ---- the commander widget ---------------------------------------------------

        private sealed class Commander : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            private const float W = 216f;
            private const float H = 132f;

            private Image _hover, _edge, _fill;
            private TMP_Text _label, _glyph;
            private RawImage _icon;

            internal static Commander Create(Transform parent)
            {
                var plate = HudCanvas.Fill(parent, "Commander", PanelColour);
                plate.raycastTarget = true;
                var rt = plate.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-28f, -(Height + 20f));
                rt.sizeDelta = new Vector2(W, H);
                var widget = plate.gameObject.AddComponent<Commander>();

                widget._hover = HudCanvas.Fill(rt, "Hover", new Color(1f, 1f, 1f, 0.12f));
                var hrt = widget._hover.rectTransform;
                hrt.anchorMin = Vector2.zero;
                hrt.anchorMax = Vector2.one;
                hrt.offsetMin = Vector2.zero;
                hrt.offsetMax = Vector2.zero;
                widget._hover.gameObject.SetActive(false);
                var edge = AccentColour;
                edge.a = 0.45f;
                widget._edge = HudCanvas.Fill(rt, "Edge", edge);
                HudCanvas.StretchAlongBottom(widget._edge.rectTransform, 2f);

                widget._label = At(rt, "Label", 24f, new Color(0.85f, 0.9f, 0.97f), TextAlignmentOptions.Center, new Vector2(0f, 12f), new Vector2(W, 40f));
                HudCanvas.SetText(widget._label, "COMMANDER");

                // The game's own strategic icon, sampled out of its atlas.
                var icon = new GameObject("Icon", typeof(RectTransform));
                icon.transform.SetParent(rt, false);
                widget._icon = icon.AddComponent<RawImage>();
                widget._icon.raycastTarget = false;
                var irt = widget._icon.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 1f);
                irt.pivot = new Vector2(0.5f, 1f);
                irt.anchoredPosition = new Vector2(0f, -40f);
                irt.sizeDelta = new Vector2(52f, 52f);
                icon.SetActive(false);
                widget._glyph = At(rt, "Glyph", 36f, new Color(0.55f, 0.78f, 1f, 0.95f), TextAlignmentOptions.Center, new Vector2(0f, 40f), new Vector2(W, 36f));
                HudCanvas.SetText(widget._glyph, "◆");

                var track = AccentColour;
                track.a = 0.14f;
                var bar = HudCanvas.Fill(rt, "Track", track);
                var brt = bar.rectTransform;
                brt.anchorMin = new Vector2(0f, 0f);
                brt.anchorMax = new Vector2(1f, 0f);
                brt.pivot = new Vector2(0.5f, 0f);
                brt.offsetMin = new Vector2(20f, 16f);
                brt.offsetMax = new Vector2(-20f, 24f);
                widget._fill = HudCanvas.Fill(brt, "Fill", GainColour);
                widget._fill.sprite = HudCanvas.White;
                widget._fill.type = Image.Type.Filled;
                widget._fill.fillMethod = Image.FillMethod.Horizontal;
                widget._fill.fillOrigin = 0;
                var frt = widget._fill.rectTransform;
                frt.anchorMin = Vector2.zero;
                frt.anchorMax = Vector2.one;
                frt.offsetMin = Vector2.zero;
                frt.offsetMax = Vector2.zero;

                plate.gameObject.SetActive(false);
                return widget;
            }

            internal void Sync()
            {
                var show = _commanderLocalIndex >= 0;
                if (gameObject.activeSelf != show) gameObject.SetActive(show);
                if (!show) return;

                var frac = _commanderMaxHealth > 0f ? Mathf.Clamp01(_commanderHealth / _commanderMaxHealth) : 1f;
                var critical = frac < Alerts.CriticalFraction;
                _label.color = critical ? DangerColour : new Color(0.85f, 0.9f, 0.97f);

                var haveIcon = _iconAtlas != null && _iconUvRects != null && _commanderIconIndex >= 0 && _commanderIconIndex < _iconUvRects.Count;
                if (_icon.gameObject.activeSelf != haveIcon) _icon.gameObject.SetActive(haveIcon);
                if (_glyph.gameObject.activeSelf == haveIcon) _glyph.gameObject.SetActive(!haveIcon);
                if (haveIcon)
                {
                    _icon.texture = _iconAtlas;
                    _icon.uvRect = _iconUvRects[_commanderIconIndex];
                    _icon.color = _ownArmyColourUi ?? Color.white;
                }
                else _glyph.color = critical ? DangerColour : new Color(0.55f, 0.78f, 1f, 0.95f);

                _fill.fillAmount = frac;
                _fill.color = frac > 0.6f ? new Color(0.42f, 0.85f, 0.5f) : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour;
            }

            public void OnPointerEnter(PointerEventData eventData) => SetHover(true);
            public void OnPointerExit(PointerEventData eventData) => SetHover(false);
            private void OnDisable() => SetHover(false);

            private void SetHover(bool on)
            {
                _hover.gameObject.SetActive(on);
                var edge = AccentColour;
                edge.a = on ? 0.8f : 0.45f;
                _edge.color = edge;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left) return;
                _pendingCommander = true;
                _applyOnFrame = -1;
            }
        }
    }
}
