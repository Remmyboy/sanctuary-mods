using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The build strip: the game's build options along the bottom, with the
    // tier tabs and the build queue stacked above them, each in its own
    // dashed panel with paging arrows and "coming soon" placeholders.
    //
    // With this on, those three panels are made invisible (PanelConceal) and
    // the HUD lays the bottom out itself, left to right from where the
    // game's strip starts: the selection row (SelectionRow), then the build
    // options the selection actually has; above the options, the tier tabs
    // when more than one is live, then the queue. Every button is the game's
    // own, so clicks (shift and right included) and hovers do what they do
    // on the game's panels.
    internal static class BuildStrip
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Scale;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Construction", "ReplaceStrip", true,
                "Replace the game's build options, tier tabs and build queue with the HUD's own rows: the selection row, then the " +
                "options the selection actually has (no placeholders, no paging), with the tier tabs — only when there is more than one — " +
                "and the queue above. Clicks and hovers go to the game's own buttons. It all comes back whenever the overlay is hidden or the mod is unloaded.");
            Scale = config.Bind("Construction", "Scale", 1f,
                new ConfigDescription("Size of the build rows, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));
        }

        // ---- the game's panels ------------------------------------------------

        private static readonly PanelConceal _concealOptions = new PanelConceal();
        private static readonly PanelConceal _concealQueue = new PanelConceal();
        private static readonly PanelConceal _concealTabs = new PanelConceal();

        internal static bool Active => Enabled != null && Enabled.Value;

        internal static void Tick(bool hudShowing)
        {
            var want = hudShowing && InMatch && Enabled.Value;
            var options = want ? SelectionRow.FindPanel<ConstructionPanelUI>(UIPanelType.Construction) : null;
            var queue = want ? SelectionRow.FindPanel<ConstructionQueuePanelUI>(UIPanelType.ConstructionQueue) : null;
            var tabs = want ? SelectionRow.FindPanel<ConstructionFilterPanelUI>(UIPanelType.ConstructionFilter) : null;
            if (_concealOptions.Apply(options)) Describe(options, queue);
            _concealQueue.Apply(queue);
            _concealTabs.Apply(tabs);
            if (!want) UnitRow.ClearHover();
        }

        internal static void Shutdown()
        {
            _concealOptions.Release();
            _concealQueue.Release();
            _concealTabs.Release();
            UnitRow.ClearHover();
        }

        /// Where the game's strip draws: its "Panel Dashed" child, sized to
        /// its contents and sitting lower than the panel's own rectangle.
        internal static bool StripRect(Component panel, float scale, out Rect rect)
        {
            Component measure = panel;
            try
            {
                var dashed = panel.transform.Find("Panel Dashed");
                if (dashed != null) measure = dashed;
            }
            catch { /* the root will do */ }
            return PanelConceal.GuiRect(measure, scale, out rect);
        }

        // ---- drawing --------------------------------------------------------------

        private static readonly List<UnitRow.Entry> _options = new List<UnitRow.Entry>();
        private static readonly List<UnitRow.Entry> _queue = new List<UnitRow.Entry>();
        private static readonly List<ConstructionFilterToggleElement> _tabs = new List<ConstructionFilterToggleElement>();

        private const float TabHeight = 22f;
        private const float TabPad = 10f;
        private const float RowGap = 4f;

        private static GUIStyle _stTab;

        internal static void ApplyFont(Font font)
        {
            _stTab = new GUIStyle(_stStripChip) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            if (font != null) _stTab.font = font;
        }

        /// From OnGUI, under the 1080-logical matrix.
        internal static void Draw(float logicalWidth, float logicalHeight, float scale, Texture2D panelTexture)
        {
            var options = _concealOptions.Panel as ConstructionPanelUI;
            if (options == null) return;
            if (_stTab == null) ApplyFont(null);
            var queue = _concealQueue.Panel as ConstructionQueuePanelUI;
            var tabs = _concealTabs.Panel as ConstructionFilterPanelUI;

            if (Event.current.type == EventType.Layout)
            {
                UnitRow.Collect(options.IsVisible ? options : null, _options, true);
                UnitRow.Collect(queue != null && queue.IsVisible ? queue : null, _queue, false);
                CollectTabs(tabs != null && tabs.IsVisible ? tabs : null);
            }

            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);
            var x = 14f;
            var bottom = logicalHeight - 14f;
            if (StripRect(options, scale, out var strip))
            {
                x = strip.x;
                bottom = strip.yMax;
            }

            // The selection row first, then the options after it.
            var selectionWidth = SelectionRow.DrawAt(x, bottom, scale, panelTexture);
            var optionsX = selectionWidth > 0f ? x + selectionWidth + 10f : x;
            var optionsWidth = UnitRow.Draw(optionsX, bottom, s, scale, _options, panelTexture);

            // Above: the tier tabs (when there is a choice), then the queue.
            var above = bottom - (optionsWidth > 0f ? UnitRow.Height * s : selectionWidth > 0f ? UnitRow.Height * Mathf.Clamp(SelectionRow.Scale.Value, 0.7f, 1.6f) : 0f) - RowGap;
            var ax = optionsX;
            if (_tabs.Count > 1)
            {
                ax += DrawTabs(ax, above, s, scale, panelTexture) + 8f;
            }
            UnitRow.Draw(ax, above, s, scale, _queue, panelTexture);

            UnitRow.FlushHover();
        }

        private static void CollectTabs(ConstructionFilterPanelUI panel)
        {
            _tabs.Clear();
            var container = panel != null ? panel.itemContainer : null;
            if (container == null) return;
            for (var i = 0; i < container.childCount; i++)
            {
                var child = container.GetChild(i);
                if (!child.gameObject.activeInHierarchy || child.localScale.x < 0.5f) continue;
                var element = child.GetComponent<ConstructionFilterToggleElement>();
                var toggle = child.GetComponent<Toggle>();
                if (element == null || toggle == null || !toggle.interactable) continue;
                _tabs.Add(element);
            }
        }

        /// The tier tabs as a row of chips, the chosen one lit. Returns the width.
        private static float DrawTabs(float x, float bottom, float s, float scale, Texture2D panelTexture)
        {
            var widths = new float[_tabs.Count];
            var total = TabPad;
            for (var i = 0; i < _tabs.Count; i++)
            {
                var text = _tabs[i].displayText != null ? _tabs[i].displayText.text : "T" + (i + 1);
                widths[i] = _stTab.CalcSize(new GUIContent(text)).x + 16f;
                total += widths[i] + 4f;
            }
            total += TabPad - 4f;
            var height = TabHeight + 8f;
            var area = new Rect(x, bottom - height * s, total * s, height * s);

            Shield(area, scale);
            GUI.DrawTexture(area, panelTexture);
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            SanctuaryHudPlugin.Fill(new Rect(area.x, area.y, area.width, 1f), accent);

            var previousMatrix = GUI.matrix;
            GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(area.x, area.y, 0f), Quaternion.identity, new Vector3(s, s, 1f));

            var tx = TabPad;
            for (var i = 0; i < _tabs.Count; i++)
            {
                var element = _tabs[i];
                var rect = new Rect(tx, 4f, widths[i], TabHeight);
                tx += widths[i] + 4f;
                var e = Event.current;
                var hover = rect.Contains(e.mousePosition);
                var on = element.toggle != null && element.toggle.isOn;
                var fill = SanctuaryHudPlugin.GameAccent;
                fill.a = on ? 0.55f : hover ? 0.3f : 0.12f;
                SanctuaryHudPlugin.Fill(rect, fill);
                var edge = SanctuaryHudPlugin.GameAccent;
                edge.a = on ? 1f : hover ? 0.8f : 0.4f;
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), edge);
                _stTab.normal.textColor = on || hover ? Color.white : new Color(0.78f, 0.85f, 0.95f, 0.9f);
                GUI.Label(rect, element.displayText != null ? element.displayText.text : "T" + (i + 1), _stTab);

                if (hover && e.type == EventType.MouseUp && e.button == 0)
                {
                    ClickTab(element);
                    e.Use();
                }
                else if (hover && e.type == EventType.MouseDown && e.button == 0)
                {
                    e.Use();
                }
            }

            GUI.matrix = previousMatrix;
            return area.width;
        }

        private static void ClickTab(ConstructionFilterToggleElement element)
        {
            try
            {
                var data = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left,
                    clickCount = 1,
                    position = Input.mousePosition,
                };
                ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerUpHandler);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Build strip: the game's tier tab threw ({ex.Message}).");
            }
        }

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(ConstructionPanelUI options, ConstructionQueuePanelUI queue)
        {
            try
            {
                _log?.LogInfo("Build strip concealed; the options panel:");
                PanelConceal.DumpSubtree(options.transform, 0, _log, 2);
                if (queue != null)
                {
                    _log?.LogInfo("...and the queue panel:");
                    PanelConceal.DumpSubtree(queue.transform, 0, _log, 2);
                }
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Build strip: could not describe it ({e.Message}).");
            }
        }
    }
}
