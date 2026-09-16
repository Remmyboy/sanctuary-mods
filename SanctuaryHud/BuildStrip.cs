using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The build strip: the game's build options along the bottom, with the
    // tier tabs and the build queue stacked above them, each in its own
    // dashed panel with paging arrows and "coming soon" placeholders.
    //
    // With this on, those three panels are made invisible (PanelConceal) and
    // the HUD lays the bottom out itself on its canvas (HudCanvas), left to
    // right from where the game's strip starts: the selection row
    // (SelectionRow), then the build options the selection actually has;
    // above the options, the tier tabs when more than one is live, then the
    // queue. Every tile is a clone of the game's own button standing in for
    // one on the concealed panel (UnitTile, TabTile), so clicks (shift and
    // right included) and hovers do what they do on the game's panels.
    internal static class BuildStrip
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("SanctuaryUI", "BuildStrip", true,
                "Replace the game's build options, tier tabs and build queue with the HUD's own rows: the selection row, then the " +
                "options the selection actually has (no placeholders, no paging), with the tier tabs — only when there is more than one — " +
                "and the queue above. Clicks and hovers go to the game's own buttons. It all comes back whenever the overlay is hidden or the mod is unloaded.");
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
            if (_concealOptions.Apply(options)) Describe(options, queue, tabs);
            _concealQueue.Apply(queue);
            _concealTabs.Apply(tabs);
            // The game's dashed panel art, off its build panel, for the plates.
            if (options != null)
            {
                try { HudCanvas.CaptureGamePanelArt(options.transform.Find("Panel Dashed")); }
                catch { /* the plain plate will do */ }
            }
            try
            {
                SyncRows(options, queue, tabs);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Build strip could not be laid out (logged once): {e}");
                }
                HideRows();
            }
        }

        internal static void Shutdown()
        {
            _concealOptions.Release();
            _concealQueue.Release();
            _concealTabs.Release();
            _optionsRow?.Destroy();
            _queueRow?.Destroy();
            _tabRow?.Destroy();
            _optionsRow = null;
            _queueRow = null;
            _tabRow = null;
            InfoCard.SetHover(null);
        }

        /// Where the game's strip draws — its "Panel Dashed" child, sized to
        /// its contents and sitting lower than the panel's own rectangle —
        /// in the HUD canvas's units from the screen's bottom-left.
        internal static bool StripLocal(Component panel, out Rect rect) =>
            HudCanvas.LocalRect(StripMeasure(panel), out rect);

        private static Component StripMeasure(Component panel)
        {
            Component measure = panel;
            try
            {
                var dashed = panel.transform.Find("Panel Dashed");
                if (dashed != null) measure = dashed;
            }
            catch { /* the root will do */ }
            return measure;
        }

        // ---- the rows -----------------------------------------------------------

        private const float Margin = 20f;

        private static TileRow _optionsRow, _queueRow;
        private static TabRow _tabRow;
        private static bool _syncLogged;
        private static readonly List<UnitRow.Entry> _options = new List<UnitRow.Entry>();
        private static readonly List<UnitRow.Entry> _queue = new List<UnitRow.Entry>();
        private static readonly List<ConstructionFilterToggleElement> _tabs = new List<ConstructionFilterToggleElement>();

        private static void HideRows()
        {
            _optionsRow?.Show(false);
            _queueRow?.Show(false);
            _tabRow?.Show(false);
            InfoCard.SetHover(null);
        }

        /// From Update, after the conceals and after the selection row has
        /// placed itself. Fills the three rows from the game's panels and
        /// lays them out around the selection row.
        private static void SyncRows(ConstructionPanelUI options, ConstructionQueuePanelUI queue, ConstructionFilterPanelUI tabs)
        {
            if (options == null || !options.IsVisible)
            {
                HideRows();
                return;
            }
            var root = HudCanvas.Ensure(options);
            if (root == null) return;
            if (_optionsRow == null || !_optionsRow.Alive) _optionsRow = TileRow.Create(root, "Build options");
            if (_queueRow == null || !_queueRow.Alive) _queueRow = TileRow.Create(root, "Build queue");
            if (_tabRow == null || !_tabRow.Alive) _tabRow = TabRow.Create(root, "Tier tabs");

            UnitRow.Collect(options, _options, true);
            UnitRow.Collect(queue != null && queue.IsVisible ? queue : null, _queue, false);
            CollectTabs(tabs != null && tabs.IsVisible ? tabs : null);

            var s = SanctuaryHudPlugin.HudScale;
            var size = HudCanvas.Size;

            // Against the left column's right edge, on the baseline: the
            // selection row (it placed itself there this frame), then the
            // options after it, with no gap.
            var origin = BottomDock.Origin;
            var left = BottomDock.ColumnRight;
            var optionsX = left + SelectionRow.RowWidth;

            // The options wrap onto more lines, upward, rather than run off
            // the screen's right edge.
            if (_options.Count > 0)
            {
                _optionsRow.Show(true);
                _optionsRow.Sync(options, _options, s, size.x - optionsX - Margin);
                _optionsRow.Place(new Vector2(optionsX, origin.y));
                BottomDock.Add(new Rect(optionsX, origin.y, _optionsRow.Width, _optionsRow.Height));
            }
            else _optionsRow.Show(false);

            // Above the baseline row, from the column's edge: the tier tabs
            // (when there is a choice), then the queue against them.
            var above = origin.y + Mathf.Max(BottomDock.RowHeight * s, _options.Count > 0 ? _optionsRow.Height : 0f);
            var ax = left;
            if (_tabs.Count > 1)
            {
                _tabRow.Show(true);
                _tabRow.Sync(tabs, _tabs, s);
                _tabRow.Place(new Vector2(ax, above));
                BottomDock.Add(new Rect(ax, above, _tabRow.Width, _tabRow.Height));
                ax += _tabRow.Width;
            }
            else _tabRow.Show(false);

            if (_queue.Count > 0)
            {
                _queueRow.Show(true);
                _queueRow.Sync(options, _queue, s, size.x - ax - Margin);
                _queueRow.Place(new Vector2(ax, above));
                BottomDock.Add(new Rect(ax, above, _queueRow.Width, _queueRow.Height));
            }
            else _queueRow.Show(false);

            // A build option under the mouse turns the unit card into a
            // build card: cost and time for that template.
            var hovered = UnitTile.Hovered;
            string template = null;
            if (hovered != null && hovered.Source != null && _options.Exists(e => e.Element == hovered.Source))
            {
                var portrait = hovered.Source.portraitImage != null ? hovered.Source.portraitImage.overrideSprite : null;
                template = UnitDomains.TemplateOf(portrait);
            }
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

        // ---- the tab row ---------------------------------------------------------

        /// The tier tabs as a row of the game's own toggles (TabTile) on a
        /// plate, laid out like a TileRow's single line.
        private sealed class TabRow
        {
            private RectTransform _rect;
            private SanctuaryPanelUI _panel;
            private readonly List<TabTile> _tiles = new List<TabTile>();
            private readonly List<ConstructionFilterToggleElement> _shown = new List<ConstructionFilterToggleElement>();

            internal bool Alive => _rect != null;

            internal static TabRow Create(RectTransform root, string name)
            {
                var rt = HudCanvas.Plate(root, name);
                var group = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
                group.padding = new RectOffset((int)TileRow.Pad, (int)TileRow.Pad, 8, 8);
                group.spacing = 4f;
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                HudCanvas.FitToContents(rt);
                rt.gameObject.SetActive(false);
                return new TabRow { _rect = rt };
            }

            internal void Show(bool showing)
            {
                if (_rect == null) return;
                if (_rect.gameObject.activeSelf != showing) _rect.gameObject.SetActive(showing);
            }

            internal void Place(Vector2 bottomLeft)
            {
                if (_rect != null) _rect.anchoredPosition = bottomLeft;
            }

            internal float Height => _rect != null ? _rect.rect.height * _rect.localScale.y : 0f;
            internal float Width => _rect != null ? _rect.rect.width * _rect.localScale.x : 0f;

            internal void Destroy()
            {
                if (_rect != null) UnityEngine.Object.Destroy(_rect.gameObject);
                _rect = null;
                _tiles.Clear();
                _shown.Clear();
            }

            internal void Sync(SanctuaryPanelUI panel, List<ConstructionFilterToggleElement> tabs, float scale)
            {
                if (_rect == null) return;
                if (panel != _panel)
                {
                    _panel = panel;
                    foreach (var tile in _tiles) if (tile != null) UnityEngine.Object.Destroy(tile.gameObject);
                    _tiles.Clear();
                    _shown.Clear();
                }
                _rect.localScale = new Vector3(scale, scale, 1f);
                HudCanvas.PlateStyle(_rect, BottomDock.PanelArt != null && BottomDock.PanelArt.Value, false);

                var same = tabs.Count == _shown.Count;
                for (var i = 0; same && i < tabs.Count; i++) same = tabs[i] == _shown[i];
                if (!same)
                {
                    _shown.Clear();
                    foreach (var tile in _tiles) if (tile != null && tile.gameObject.activeSelf) tile.gameObject.SetActive(false);
                    var at = 0;
                    var sibling = 1;   // after the hairline
                    foreach (var tab in tabs)
                    {
                        while (at < _tiles.Count && _tiles[at] == null) _tiles.RemoveAt(at);
                        TabTile tile;
                        if (at < _tiles.Count) tile = _tiles[at];
                        else
                        {
                            tile = TabTile.Create(panel, _rect);
                            if (tile == null) continue;
                            _tiles.Add(tile);
                        }
                        at++;
                        tile.transform.SetSiblingIndex(sibling++);
                        if (!tile.gameObject.activeSelf) tile.gameObject.SetActive(true);
                        _shown.Add(tab);
                    }
                }
                for (var i = 0; i < _shown.Count && i < _tiles.Count; i++)
                {
                    if (_tiles[i] != null) _tiles[i].Mirror(_shown[i]);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
            }
        }

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(ConstructionPanelUI options, ConstructionQueuePanelUI queue, ConstructionFilterPanelUI tabs)
        {
            try
            {
                _log?.LogInfo("Build strip concealed; the options panel:");
                PanelConceal.DumpSubtree(options.transform, 0, _log, 2);
                if (options.buttonPrefab != null)
                {
                    _log?.LogInfo("...its button prefab:");
                    PanelConceal.DumpSubtree(options.buttonPrefab.transform, 0, _log, 3);
                }
                if (queue != null)
                {
                    _log?.LogInfo("...and the queue panel:");
                    PanelConceal.DumpSubtree(queue.transform, 0, _log, 2);
                }
                if (tabs != null)
                {
                    _log?.LogInfo("...and the tier tabs, with their prefab:");
                    PanelConceal.DumpSubtree(tabs.transform, 0, _log, 2);
                    if (tabs.buttonPrefab != null) PanelConceal.DumpSubtree(tabs.buttonPrefab.transform, 0, _log, 3);
                }
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Build strip: could not describe it ({e.Message}).");
            }
        }
    }
}
