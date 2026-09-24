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
    // same buttons stand as a row along the bottom on the HUD's own canvas
    // (TileRow of UnitTile: clones of the game's button, each mirroring one
    // of the panel's and passing it every click and hover), at the left end
    // of the build strip. With the build strip replaced too (BuildStrip),
    // that lays its options out after the row; otherwise the row goes where
    // the game's own strip starts, or off its right end when it is showing.
    internal static class SelectionRow
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("SanctuaryUI", "SelectionRow", true,
                "Draw the selected unit types as a row along the bottom, at the left end of the build options, instead of the game's " +
                "column up the left edge. Left click keeps just that type, right click drops it. " +
                "The game's list comes back whenever the overlay is hidden or the mod is unloaded.");
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
            var want = hudShowing && InMatch && Enabled.Value && !PanelConceal.Unavailable;
            var panel = want ? FindPanel<SelectionPanelUI>(UIPanelType.Selection) : null;
            if (_conceal.Apply(panel)) Describe(panel);
            try
            {
                SyncRow(panel);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Selection row could not be laid out (logged once): {e}");
                }
                _row?.Show(false);
            }
        }

        internal static void Shutdown()
        {
            _conceal.Release();
            _row?.Destroy();
            _row = null;
        }

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

        // ---- the row ----------------------------------------------------------------

        private static TileRow _row;
        private static bool _syncLogged;
        private static readonly List<UnitRow.Entry> _entries = new List<UnitRow.Entry>();

        /// From Update, after the conceal. Builds the row on the HUD canvas
        /// the first time, fills it from the game's list, and puts it where
        /// it goes this frame.
        private static void SyncRow(SelectionPanelUI panel)
        {
            if (panel == null || !panel.IsVisible)
            {
                _row?.Show(false);
                return;
            }
            var root = HudCanvas.Ensure(panel);
            if (root == null) return;
            if (_row == null || !_row.Alive) _row = TileRow.Create(root, "Selection row");

            UnitRow.Collect(panel, _entries, false);
            if (_entries.Count == 0)
            {
                _row.Show(false);
                return;
            }

            // The tiles are clones of the build panel's prefab, so the row
            // matches the build area; its own panel's if there is no build panel.
            var construction = FindPanel<ConstructionPanelUI>(UIPanelType.Construction);
            var s = SanctuaryHudPlugin.HudScale;
            _row.Show(true);
            _row.Sync(construction != null ? (SanctuaryPanelUI)construction : panel, _entries, s);

            // Against the left column's right edge, on the baseline; with
            // the strip left to the game, where the game's strip starts, or
            // off its right end when it is showing.
            var at = new Vector2(BottomDock.ColumnRight, BottomDock.Origin.y);
            if (!BuildStrip.Active && construction != null && BuildStrip.StripLocal(construction, out var strip))
            {
                at = new Vector2(strip.x, strip.y);
                if (construction.IsVisible)
                {
                    // Off the right end of the game's own strip, pulled back
                    // in if that would run off the screen.
                    at.x = strip.xMax + 20f;
                    var limit = HudCanvas.Size.x - 20f;
                    if (at.x + _row.Width > limit) at.x = Mathf.Max(0f, limit - _row.Width);
                }
            }
            _row.Place(at);
            if (BuildStrip.Active) BottomDock.Add(new Rect(at.x, at.y, _row.Width, _row.Height));
        }

        /// The row's size on the canvas, 0 while it is not showing: what the
        /// build strip lays its rows out around.
        internal static float RowWidth => _row != null && _row.Showing ? _row.Width : 0f;
        internal static float RowHeight => _row != null && _row.Showing ? _row.Height : 0f;

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(SelectionPanelUI panel)
        {
            try
            {
                _log?.LogInfo("Selection panel concealed; its tree:");
                PanelConceal.DumpSubtree(panel.transform, 0, _log, 2);
                if (panel.buttonPrefab != null)
                {
                    _log?.LogInfo("...and its button prefab:");
                    PanelConceal.DumpSubtree(panel.buttonPrefab.transform, 0, _log, 3);
                }
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Selection panel: could not describe it ({e.Message}).");
            }
        }
    }
}
