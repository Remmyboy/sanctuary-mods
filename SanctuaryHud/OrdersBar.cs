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
    // compact row draws in its place: only the orders the selection can take,
    // wearing the game's own icons and colours, each click passed on to the
    // game's own button so whatever Lua hung on it runs unchanged.
    internal static class OrdersBar
    {
        internal static ConfigEntry<bool> Enabled, HideInert;
        internal static ConfigEntry<float> Scale;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Orders", "CompactPanel", true,
                "Replace the game's orders panel (bottom left) with a compact row showing only the orders the selected units can take, " +
                "using the game's own icons. The game's panel comes back whenever the overlay is hidden or the mod is unloaded.");
            HideInert = config.Bind("Orders", "HideInert", true,
                "Leave out the buttons the game has not wired up yet: move, attack, patrol, assist and the rest do nothing when clicked " +
                "in the current build (they are hotkeys and right-clicks). Stop and the toggles — pause, repeat build, shield, intel, production — stay.");
            Scale = config.Bind("Orders", "Scale", 1f,
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
        }

        internal static void Shutdown() => _conceal.Release();

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

        // ---- drawing --------------------------------------------------------------

        // The game's buttons are 80 canvas units, 40 at 1080; the row keeps
        // that size so the icons read as they do on the game's own panel.
        private const float ButtonSize = 40f;
        private const float Gap = 4f;
        private const float Pad = 5f;

        private static GUIStyle _stCaption;

        internal static void ApplyFont(Font font)
        {
            _stCaption = new GUIStyle(_stStripChip) { fontSize = 12, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.85f, 0.9f, 0.97f) } };
            if (font != null) _stCaption.font = font;
        }

        /// From OnGUI, under the 1080-logical matrix.
        internal static void Draw(float logicalWidth, float logicalHeight, float scale, Texture2D panelTexture)
        {
            var panel = _conceal.Panel as OrdersPanelUI;
            if (panel == null || !panel.IsVisible) return;
            if (Event.current.type == EventType.Layout) Collect(panel, HideInert.Value);
            if (_row.Count == 0) return;
            if (_stCaption == null) ApplyFont(null);

            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);
            var innerW = _row.Count * ButtonSize + (_row.Count - 1) * Gap + Pad * 2f;
            var innerH = ButtonSize + Pad * 2f;
            var area = new Rect(14f, logicalHeight - 14f - innerH * s, innerW * s, innerH * s);
            // Where the game's own panel sits: the row takes its bottom-left corner.
            if (PanelConceal.GuiRect(panel, scale, out var anchor))
            {
                area.x = anchor.x;
                area.y = anchor.yMax - area.height;
            }

            Shield(area, scale);
            GUI.DrawTexture(area, panelTexture);
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            SanctuaryHudPlugin.Fill(new Rect(area.x, area.y, area.width, 1f), accent);

            var previousMatrix = GUI.matrix;
            GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(area.x, area.y, 0f), Quaternion.identity, new Vector3(s, s, 1f));

            string caption = null;
            var x = Pad;
            foreach (var button in _row)
            {
                var rect = new Rect(x, Pad, ButtonSize, ButtonSize);
                x += ButtonSize + Gap;
                var hover = rect.Contains(Event.current.mousePosition);
                if (hover) caption = button.Label + (button.Active ? "  ·  ON" : "");

                // The game's own three layers, as its panel stacks them: the
                // dashed background in the order's colour, the icon in white,
                // and the frame — which the game keeps all but invisible until
                // the toggle is on or the mouse is over it.
                // The HUD's own glyph on a flat tile in the order's colour
                // (the Lua's dark tint brightened, as the game's glow shader
                // does): filled solid while a toggle is on, edged otherwise.
                var tint = Bright(button.Tint);
                var fill = tint;
                fill.a = button.Active ? 0.75f : hover ? 0.4f : 0.22f;
                SanctuaryHudPlugin.Fill(rect, fill);
                var edge = button.Active ? Color.white : tint;
                edge.a = button.Active ? 0.95f : hover ? 0.9f : 0.55f;
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.y, rect.width, 1f), edge);
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), edge);
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.y, 1f, rect.height), edge);
                SanctuaryHudPlugin.Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), edge);

                var previous = GUI.color;
                GUI.color = Color.white;
                var glyph = Glyphs.Get(button.Key);
                var glyphRect = new Rect(rect.x + rect.width * 0.2f, rect.y + rect.height * 0.2f, rect.width * 0.6f, rect.height * 0.6f);
                if (glyph != null) GUI.DrawTexture(glyphRect, glyph);
                else
                {
                    _stStripGlyph.normal.textColor = Color.white;
                    GUI.Label(rect, button.Label.Substring(0, 1), _stStripGlyph);
                }
                GUI.color = previous;

                if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) Click(button.Element);
            }

            // The name of the button under the mouse, in a chip above the row:
            // the icons are the game's, and not all of them explain themselves.
            if (caption != null)
            {
                var width = _stCaption.CalcSize(new GUIContent(caption)).x + 16f;
                var chip = new Rect(0f, -22f, width, 18f);
                GUI.DrawTexture(chip, panelTexture);
                SanctuaryHudPlugin.Fill(new Rect(chip.x, chip.y, chip.width, 1f), accent);
                GUI.Label(new Rect(chip.x + 8f, chip.y, chip.width - 8f, chip.height), caption, _stCaption);
            }

            GUI.matrix = previousMatrix;
        }

        private static readonly GUIStyle _stStripGlyph = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        /// The Lua's tints are dark (stop is 0.55, 0.02, 0.02); the game's
        /// button shader glows them up. Same hue, full brightness.
        private static Color Bright(Color tint)
        {
            Color.RGBToHSV(tint, out var h, out var s, out var v);
            var bright = Color.HSVToRGB(h, Mathf.Min(1f, s * 1.1f), Mathf.Max(v, 0.9f));
            bright.a = tint.a;
            return bright;
        }

        /// Clicks the game's own button the way the mouse would: down then
        /// up, which is what its Lua handler fires on.
        private static void Click(OrderButtonElement element)
        {
            if (element == null) return;
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
            catch (Exception e)
            {
                _log?.LogWarning($"Orders row: the game's button threw ({e.Message}).");
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
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Orders panel: could not describe it ({e.Message}).");
            }
        }
    }
}
