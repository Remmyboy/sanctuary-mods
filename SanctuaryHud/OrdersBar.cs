using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The orders panel, bottom left. The game draws all twenty-one order
    // buttons for any selection and dims the ones that don't apply; of the
    // bright ones, only Stop and the toggles (pause, repeat build, shield,
    // intel, production) do anything when clicked in the current build — the
    // Lua registers no click function for move, attack, patrol and the rest,
    // which are hotkeys and right-clicks anyway.
    //
    // With this on, the game's panel is made invisible (PanelConceal) and a
    // compact row stands in its place on the HUD canvas: only the orders the
    // selection can take, each a clone of the game's own button (OrderTile)
    // wearing its icon, colour and glow, mirroring the concealed one and
    // passing it every click so whatever Lua hung on it runs unchanged.
    internal static class OrdersBar
    {
        internal static ConfigEntry<bool> Enabled, HideInert;
        internal static ConfigEntry<float> Scale;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("SanctuaryUI", "OrdersRow", true,
                "Replace the game's orders panel (bottom left) with a compact row showing only the orders the selected units can take, " +
                "using the game's own icons. The game's panel comes back whenever the overlay is hidden or the mod is unloaded.");
            HideInert = config.Bind("SanctuaryUI", "OrdersHideInert", true,
                "Leave out the buttons the game has not wired up yet: move, attack, patrol, assist and the rest do nothing when clicked " +
                "in the current build (they are hotkeys and right-clicks). Stop and the toggles — pause, repeat build, shield, intel, production — stay.");
            Scale = config.Bind("SanctuaryUI", "OrdersScale", 1f,
                new ConfigDescription("Size of the compact row, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));
        }

        // ---- the game's panel -------------------------------------------------

        private static readonly PanelConceal _conceal = new PanelConceal();

        private static OrdersPanelUI FindPanel()
        {
            try
            {
                var ui = SanctuaryUIManager.Instance;
                if (ui == null) return null;
                return ui.TryGetPanel(UIPanelType.Orders, out var panel) ? panel as OrdersPanelUI : null;
            }
            catch
            {
                return null;
            }
        }

        /// From Update. Conceals the game's panel while the row is standing
        /// in for it, and gives it back otherwise.
        private static OrdersPanelUI _described;

        internal static void Tick(bool hudShowing)
        {
            var want = hudShowing && InMatch && Enabled.Value;
            var panel = InMatch ? FindPanel() : null;
            // Once per panel (a new match brings a new one), whether or not
            // the row is standing in for it: what the game's panel holds.
            if (panel != null && panel != _described)
            {
                _described = panel;
                Describe(panel);
            }
            _conceal.Apply(want ? panel : null);
            try
            {
                SyncRow(want ? panel : null);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Orders row could not be laid out (logged once): {e}");
                }
                _bar?.Show(false);
            }
        }

        internal static void Shutdown()
        {
            _conceal.Release();
            _bar?.Destroy();
            _bar = null;
        }

        // ---- the buttons -------------------------------------------------------

        // The Lua's element table, in order (client/ui/ordersPanel.lua): the
        // fallback for naming a button whose icon sprite carries no name.
        private static readonly string[] ByIndex =
        {
            "shield", "alloys", "spr", "stealth", "surface_dive", "dock", "transport",
            "manual_fire_01", "manual_fire_02", "harvest", "capture", "repair", "pause", "repeat_build",
            "target_priority", "move", "attack_move", "attack", "patrol", "assist", "stop",
        };

        /// The buttons with a click function in the Lua. Everything else is
        /// drawn by the game and does nothing.
        private static readonly HashSet<string> Wired = new HashSet<string> { "stop", "repeat_build", "pause", "shield", "spr", "alloys" };

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            ["spr"] = "INTEL",
            ["alloys"] = "PRODUCTION",
            ["manual_fire_01"] = "MANUAL FIRE 1",
            ["manual_fire_02"] = "MANUAL FIRE 2",
        };

        private sealed class OrderButton
        {
            public OrderButtonElement Element;
            public string Key;
            public string Label;
            public Color Tint;
            public bool Active;
        }

        private static readonly List<OrderButton> _row = new List<OrderButton>();

        private static string KeyOf(OrderButtonElement element, int index)
        {
            var sprite = element.icon != null ? element.icon.sprite : null;
            var name = sprite != null ? sprite.name ?? "" : "";
            const string prefix = "icon_order_";
            var at = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var key = name.Substring(at + prefix.Length).ToLowerInvariant();
                var dot = key.IndexOf('.');
                if (dot >= 0) key = key.Substring(0, dot);
                if (key.EndsWith("(clone)")) key = key.Substring(0, key.Length - 7).Trim();
                if (key.Length > 0) return key;
            }
            return index < ByIndex.Length ? ByIndex[index] : "order" + index;
        }

        private static string LabelOf(string key) =>
            Labels.TryGetValue(key, out var label) ? label : key.Replace('_', ' ').ToUpperInvariant();

        /// Reads the game's panel: every button that is on and enabled for
        /// the current selection, in the panel's order.
        private static void Collect(OrdersPanelUI panel, bool hideInert)
        {
            _row.Clear();
            var container = panel.itemContainer;
            if (container == null) return;
            var index = 0;
            for (var i = 0; i < container.childCount; i++)
            {
                var child = container.GetChild(i);
                var element = child.GetComponent<OrderButtonElement>();
                if (element == null) continue;
                var at = index++;
                // The panel pools buttons it isn't using by scaling them to
                // nothing; the game dims the rest by making them non-interactable.
                if (!child.gameObject.activeInHierarchy || child.localScale.x < 0.5f) continue;
                var button = child.GetComponent<Button>();
                if (button == null || !button.interactable) continue;
                var key = KeyOf(element, at);
                if (hideInert && !Wired.Contains(key)) continue;
                _row.Add(new OrderButton
                {
                    Element = element,
                    Key = key,
                    Label = LabelOf(key),
                    Tint = element.background != null ? element.background.color : SanctuaryHudPlugin.GameAccent,
                    // SetColorTint gives the frame an alpha of 1/255 at rest
                    // and 1 while the toggle is on; SetActive swaps between them.
                    Active = button.colors.normalColor.a > 0.5f,
                });
            }
        }

        // ---- the row ----------------------------------------------------------------

        private const float Gap = 8f;

        private static OrderRow _bar;
        private static bool _syncLogged;

        /// From Update, after the conceal. Builds the row on the HUD canvas
        /// the first time, fills it from the game's panel, and puts it where
        /// the game's panel sits.
        private static void SyncRow(OrdersPanelUI panel)
        {
            if (panel == null || !panel.IsVisible)
            {
                _bar?.Show(false);
                return;
            }
            var root = HudCanvas.Ensure(panel);
            if (root == null) return;
            if (_bar == null || !_bar.Alive) _bar = OrderRow.Create(root, "Orders row");

            Collect(panel, HideInert.Value);
            if (_row.Count == 0)
            {
                _bar.Show(false);
                return;
            }

            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);
            _bar.Show(true);
            _bar.Sync(panel, _row, s);

            // Where the game's own panel sits: the row takes its bottom-left corner.
            var at = new Vector2(28f, 28f);
            if (HudCanvas.LocalRect(panel, out var anchor)) at = new Vector2(anchor.x, anchor.y);
            _bar.Place(at);
        }

        /// The order buttons (OrderTile) on a plate, one line.
        private sealed class OrderRow
        {
            private RectTransform _rect;
            private SanctuaryPanelUI _panel;
            private readonly List<OrderTile> _tiles = new List<OrderTile>();
            private readonly List<OrderButtonElement> _shown = new List<OrderButtonElement>();

            internal bool Alive => _rect != null;

            internal static OrderRow Create(RectTransform root, string name)
            {
                var rt = HudCanvas.Plate(root, name);
                var group = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
                group.padding = new RectOffset((int)TileRow.Pad, (int)TileRow.Pad, (int)TileRow.Pad, (int)TileRow.Pad);
                group.spacing = Gap;
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                HudCanvas.FitToContents(rt);
                rt.gameObject.SetActive(false);
                return new OrderRow { _rect = rt };
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

            internal void Destroy()
            {
                if (_rect != null) UnityEngine.Object.Destroy(_rect.gameObject);
                _rect = null;
                _tiles.Clear();
                _shown.Clear();
            }

            internal void Sync(SanctuaryPanelUI panel, List<OrderButton> buttons, float scale)
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

                var same = buttons.Count == _shown.Count;
                for (var i = 0; same && i < buttons.Count; i++) same = buttons[i].Element == _shown[i];
                if (!same)
                {
                    _shown.Clear();
                    foreach (var tile in _tiles) if (tile != null && tile.gameObject.activeSelf) tile.gameObject.SetActive(false);
                    var at = 0;
                    var sibling = 1;   // after the hairline
                    foreach (var button in buttons)
                    {
                        while (at < _tiles.Count && _tiles[at] == null) _tiles.RemoveAt(at);
                        OrderTile tile;
                        if (at < _tiles.Count) tile = _tiles[at];
                        else
                        {
                            tile = OrderTile.Create(panel, _rect);
                            if (tile == null) continue;
                            _tiles.Add(tile);
                        }
                        at++;
                        tile.transform.SetSiblingIndex(sibling++);
                        if (!tile.gameObject.activeSelf) tile.gameObject.SetActive(true);
                        _shown.Add(button.Element);
                    }
                }
                for (var i = 0; i < _shown.Count && i < _tiles.Count && i < buttons.Count; i++)
                {
                    var button = buttons[i];
                    if (_tiles[i] != null) _tiles[i].Mirror(button.Element, button.Label + (button.Active ? "  ·  ON" : ""));
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
            }
        }

        // ---- diagnostics ------------------------------------------------------

        /// Once per panel, into the log: what the game's panel holds, so the
        /// next tweak has something to go on.
        /// What an icon Image carries, for the log: the sprite, its texture
        /// and whether its rectangle can be read (a packed sprite's cannot).
        private static string SpriteInfo(Image icon)
        {
            if (icon == null) return "no image";
            var sprite = icon.overrideSprite;
            if (sprite == null) return "no sprite" + (icon.sprite != null ? " (override null, sprite set)" : "");
            var tex = sprite.texture;
            string rect;
            try { var r = sprite.textureRect; rect = $"{r.x:0},{r.y:0} {r.width:0}x{r.height:0}"; }
            catch (Exception e) { rect = "unreadable: " + e.GetType().Name; }
            return $"sprite '{sprite.name}' packed={sprite.packed} tex={(tex != null ? $"'{tex.name}' {tex.width}x{tex.height}" : "none")} rect={rect} colour={icon.color}";
        }

        private static void Describe(OrdersPanelUI panel)
        {
            try
            {
                var found = new List<string>();
                var container = panel.itemContainer;
                if (container != null)
                {
                    var index = 0;
                    for (var i = 0; i < container.childCount; i++)
                    {
                        var element = container.GetChild(i).GetComponent<OrderButtonElement>();
                        if (element == null) continue;
                        found.Add($"{index}:{KeyOf(element, index)} icon {SpriteInfo(element.icon)}; background {SpriteInfo(element.background)}; frame {SpriteInfo(element.frame)}");
                        index++;
                    }
                }
                _log?.LogInfo($"Orders panel found; {found.Count} button(s): {string.Join(" | ", found)}.");
                PanelConceal.DumpSubtree(panel.transform, 0, _log, 1);
                if (panel.buttonPrefab != null)
                {
                    _log?.LogInfo("...and its button prefab:");
                    PanelConceal.DumpSubtree(panel.buttonPrefab.transform, 0, _log, 3);
                }
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Orders panel: could not describe it ({e.Message}).");
            }
        }
    }
}
