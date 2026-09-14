using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The selection list: one button per unit type in the selection, with a
    // count, a left click keeping only that type and a right click dropping
    // it. The game stacks these upwards in a narrow column pinned to the
    // left edge, above the unit card, where a big mixed army scrolls out of
    // sight.
    //
    // With this on, the game's panel is made invisible (PanelConceal) and the
    // same buttons draw as a row along the bottom (UnitRow), at the left end
    // of the build strip. With the build strip replaced too (BuildStrip),
    // that draws the row as part of its layout; otherwise the row goes where
    // the game's own strip starts, or off its right end when it is showing.
    internal static class SelectionRow
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Scale;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Selection", "HorizontalRow", true,
                "Draw the selected unit types as a row along the bottom, at the left end of the build options, instead of the game's " +
                "column up the left edge. Left click keeps just that type, right click drops it. " +
                "The game's list comes back whenever the overlay is hidden or the mod is unloaded.");
            Scale = config.Bind("Selection", "Scale", 1f,
                new ConfigDescription("Size of the row, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));
        }

        // ---- the game's panels ------------------------------------------------

        private static readonly PanelConceal _conceal = new PanelConceal();

        internal static T FindPanel<T>(UIPanelType type) where T : SanctuaryPanelUI
        {
            try
            {
                var ui = SanctuaryUIManager.Instance;
                if (ui == null) return null;
                return ui.TryGetPanel(type, out var panel) ? panel as T : null;
            }
            catch
            {
                return null;
            }
        }

        internal static void Tick(bool hudShowing)
        {
            var want = hudShowing && InMatch && Enabled.Value;
            var panel = want ? FindPanel<SelectionPanelUI>(UIPanelType.Selection) : null;
            if (_conceal.Apply(panel)) Describe(panel);
        }

        internal static void Shutdown() => _conceal.Release();

        private static int _countFrame = -1;
        private static int _count;

        /// How many units are selected, summed off the game's selection
        /// list's counts (concealed or not); once per frame.
        internal static int CountSelected()
        {
            if (_countFrame == Time.frameCount) return _count;
            _countFrame = Time.frameCount;
            _count = 0;
            try
            {
                var panel = FindPanel<SelectionPanelUI>(UIPanelType.Selection);
                if (panel == null || !panel.IsVisible || panel.itemContainer == null) return _count;
                var container = panel.itemContainer;
                for (var i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i);
                    if (!child.gameObject.activeInHierarchy || child.localScale.x < 0.5f) continue;
                    var element = child.GetComponent<UnitButtonElement>();
                    if (element == null || element.textOverlayText == null) continue;
                    if (int.TryParse(element.textOverlayText.text, out var n)) _count += n;
                }
            }
            catch { /* unknown counts as none */ }
            return _count;
        }

        // ---- drawing --------------------------------------------------------------

        private static readonly List<UnitRow.Entry> _row = new List<UnitRow.Entry>();

        /// Draws the row with its bottom-left corner at (x, bottom), for a
        /// layout that owns the bottom of the screen. Returns the width
        /// drawn, 0 when there is nothing to draw.
        internal static float DrawAt(float x, float bottom, float scale, Texture2D panelTexture)
        {
            var panel = _conceal.Panel as SelectionPanelUI;
            if (panel == null || !panel.IsVisible) return 0f;
            if (Event.current.type == EventType.Layout) UnitRow.Collect(panel, _row, false);
            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);
            return UnitRow.Draw(x, bottom, s, scale, _row, panelTexture);
        }

        /// From OnGUI, under the 1080-logical matrix: the row on its own,
        /// beside the game's build strip.
        internal static void Draw(float logicalWidth, float logicalHeight, float scale, Texture2D panelTexture)
        {
            var panel = _conceal.Panel as SelectionPanelUI;
            if (panel == null || !panel.IsVisible) return;
            if (Event.current.type == EventType.Layout) UnitRow.Collect(panel, _row, false);
            if (_row.Count == 0) return;

            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);
            var width = UnitRow.Width(_row) * s;
            var x = 14f;
            var bottom = logicalHeight - 14f;
            var construction = FindPanel<ConstructionPanelUI>(UIPanelType.Construction);
            if (construction != null && BuildStrip.StripRect(construction, scale, out var strip))
            {
                bottom = strip.yMax;
                x = construction.IsVisible ? strip.xMax + 10f : strip.x;
                if (x + width > logicalWidth - 10f) x = Mathf.Max(0f, logicalWidth - 10f - width);
            }
            UnitRow.Draw(x, bottom, s, scale, _row, panelTexture);
            UnitRow.FlushHover();
        }

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(SelectionPanelUI panel)
        {
            try
            {
                _log?.LogInfo("Selection panel concealed; its tree:");
                PanelConceal.DumpSubtree(panel.transform, 0, _log, 2);
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Selection panel: could not describe it ({e.Message}).");
            }
        }
    }
}
