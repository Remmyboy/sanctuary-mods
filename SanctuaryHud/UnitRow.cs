using System;
using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // A row of the game's unit buttons, drawn the HUD's way. Three of the
    // game's panels are lists of UnitButtonElement — the selection list, the
    // build options and the build queue — each with a portrait, an overlay
    // text (a count, or a hotkey) and sometimes a progress bar. This reads
    // such a panel's buttons and draws them as one row, passing clicks and
    // hovers to the game's own buttons so the Lua behind them runs
    // unchanged: shift-clicks, right-clicks, and the hover that puts a
    // build option's figures on the unit card.
    internal static class UnitRow
    {
        internal sealed class Entry
        {
            public UnitButtonElement Element;   // null for a separator
        }

        internal const float ButtonSize = 40f;
        internal const float Gap = 4f;
        internal const float SeparatorWidth = 9f;
        internal const float Pad = 5f;
        internal const float Height = ButtonSize + Pad * 2f;

        private static GUIStyle _stText;

        // The tiles, by the unit's element: green for land, blue for water,
        // a hard split along the bottom-left to top-right diagonal — land
        // above, water below — for
        // one that goes on both, a lighter blue for the air, and a neutral
        // slate where the element isn't known.
        private static readonly Color LandColour = new Color(0.13f, 0.42f, 0.27f, 0.95f);
        private static readonly Color NavalColour = new Color(0.10f, 0.24f, 0.44f, 0.95f);
        private static readonly Color AirColour = new Color(0.24f, 0.50f, 0.72f, 0.95f);
        private static readonly Color UnknownColour = new Color(0.15f, 0.19f, 0.25f, 0.95f);
        private static readonly Dictionary<UnitDomains.Domain, Texture2D> _tiles = new Dictionary<UnitDomains.Domain, Texture2D>();

        private static Texture2D Tile(UnitDomains.Domain domain)
        {
            if (_tiles.TryGetValue(domain, out var tile)) return tile;
            const int size = 32;
            tile = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)          // row 0 is the bottom
            {
                for (var x = 0; x < size; x++)
                {
                    Color c;
                    switch (domain)
                    {
                        case UnitDomains.Domain.Land: c = LandColour; break;
                        case UnitDomains.Domain.Naval: c = NavalColour; break;
                        case UnitDomains.Domain.Air: c = AirColour; break;
                        // Split along the bottom-left to top-right diagonal:
                        // land above it, water below.
                        case UnitDomains.Domain.Amphibious: c = y > x ? LandColour : NavalColour; break;
                        default: c = UnknownColour; break;
                    }
                    pixels[y * size + x] = c;
                }
            }
            tile.SetPixels(pixels);
            tile.Apply(false, true);
            _tiles[domain] = tile;
            return tile;
        }

        private static Color EdgeFor(UnitDomains.Domain domain)
        {
            switch (domain)
            {
                case UnitDomains.Domain.Land: return new Color(0.42f, 0.88f, 0.5f);
                case UnitDomains.Domain.Naval: return new Color(0.35f, 0.62f, 1f);
                case UnitDomains.Domain.Amphibious: return new Color(0.4f, 0.8f, 0.8f);
                case UnitDomains.Domain.Air: return new Color(0.6f, 0.85f, 1f);
                default: return SanctuaryHudPlugin.GameAccent;
            }
        }

        internal static void ApplyFont(Font font)
        {
            _stText = new GUIStyle(_stStripChip) { fontSize = 13, alignment = TextAnchor.LowerLeft, normal = { textColor = Color.white } };
            if (font != null) _stText.font = font;
        }

        /// The panel's buttons that are on, in order, with a separator entry
        /// where the game put one; the ones the game has greyed out (a
        /// "coming soon" option) are left out when asked.
        internal static void Collect(SanctuaryPanelUI panel, List<Entry> row, bool liveOnly)
        {
            row.Clear();
            var container = panel != null ? panel.itemContainer : null;
            if (container == null) return;
            for (var i = 0; i < container.childCount; i++)
            {
                var child = container.GetChild(i);
                // The panel pools what it isn't using by scaling it to nothing.
                if (!child.gameObject.activeInHierarchy || child.localScale.x < 0.5f) continue;
                var element = child.GetComponent<UnitButtonElement>();
                if (element == null)
                {
                    if (row.Count > 0 && row[row.Count - 1].Element != null) row.Add(new Entry());
                    continue;
                }
                if (liveOnly)
                {
                    var button = child.GetComponent<Button>();
                    if (button != null && !button.interactable) continue;
                    // A "coming soon" placeholder: the game gives it "?" for
                    // its hotkey and leaves it clickable.
                    if (element.textOverlayText != null && element.textOverlayText.text == "?") continue;
                }
                row.Add(new Entry { Element = element });
            }
            while (row.Count > 0 && row[row.Count - 1].Element == null) row.RemoveAt(row.Count - 1);
        }

        /// The row's width at scale 1, padding included.
        internal static float Width(List<Entry> row)
        {
            if (row.Count == 0) return 0f;
            var w = Pad * 2f - Gap;
            foreach (var entry in row) w += (entry.Element != null ? ButtonSize : SeparatorWidth) + Gap;
            return w;
        }

        /// Draws the row with its bottom-left corner at (x, bottom), in
        /// 1080-logical coordinates, at scale s. Returns the outer width.
        internal static float Draw(float x, float bottom, float s, float scale, List<Entry> row, Texture2D panelTexture)
        {
            if (row.Count == 0) return 0f;
            if (_stText == null) ApplyFont(null);
            var area = new Rect(x, bottom - Height * s, Width(row) * s, Height * s);

            Shield(area, scale);
            GUI.DrawTexture(area, panelTexture);
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            SanctuaryHudPlugin.Fill(new Rect(area.x, area.y, area.width, 1f), accent);

            var previousMatrix = GUI.matrix;
            GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(area.x, area.y, 0f), Quaternion.identity, new Vector3(s, s, 1f));

            var bx = Pad;
            foreach (var entry in row)
            {
                if (entry.Element == null)
                {
                    accent.a = 0.35f;
                    SanctuaryHudPlugin.Fill(new Rect(bx + SeparatorWidth / 2f, Pad + 6f, 1f, ButtonSize - 12f), accent);
                    bx += SeparatorWidth + Gap;
                    continue;
                }
                DrawButton(new Rect(bx, Pad, ButtonSize, ButtonSize), entry.Element);
                bx += ButtonSize + Gap;
            }

            GUI.matrix = previousMatrix;
            return area.width;
        }

        private static void DrawButton(Rect rect, UnitButtonElement element)
        {
            var e = Event.current;
            var hover = rect.Contains(e.mousePosition);
            if (hover && e.type == EventType.Repaint) _hoverThisFrame = element;
            var previous = GUI.color;

            // The HUD's own tile under the game's portrait of the type: the
            // game's brown backdrop sprite is left out.
            GUI.color = Color.white;
            var domain = UnitDomains.Enabled != null && UnitDomains.Enabled.Value
                ? UnitDomains.Of(element.portraitImage != null ? element.portraitImage.overrideSprite : null)
                : UnitDomains.Domain.Unknown;
            GUI.DrawTexture(rect, Tile(domain));
            GamePanel.DrawIcon(rect, element.portraitImage);
            if (hover) SanctuaryHudPlugin.Fill(rect, new Color(1f, 1f, 1f, 0.12f));
            GUI.color = previous;

            var edge = EdgeFor(domain);
            edge.a = hover ? 1f : 0.7f;
            SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), edge);

            // Progress, where the game shows it (the item being built).
            var bar = element.progressBar;
            if (bar != null && bar.gameObject.activeSelf && bar.fillAmount > 0f)
            {
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.yMax - 4f, rect.width, 3f), new Color(0f, 0f, 0f, 0.5f));
                SanctuaryHudPlugin.Fill(new Rect(rect.x, rect.yMax - 4f, rect.width * Mathf.Clamp01(bar.fillAmount), 3f), GainColour);
            }

            // The overlay text: a count, or a hotkey.
            var text = element.textOverlayText != null ? element.textOverlayText.text : null;
            if (!string.IsNullOrEmpty(text) && text != "?")
            {
                var size = _stText.CalcSize(new GUIContent(text));
                SanctuaryHudPlugin.Fill(new Rect(rect.x + 1f, rect.yMax - size.y - 1f, size.x + 6f, size.y), new Color(0f, 0f, 0f, 0.6f));
                GUI.Label(new Rect(rect.x + 4f, rect.y, rect.width - 4f, rect.height - 1f), text, _stText);
            }

            // Left and right both go to the game's button, whose Lua handler
            // fires on the release and reads shift itself.
            if (hover && e.type == EventType.MouseUp && (e.button == 0 || e.button == 1))
            {
                Click(element, e.button == 1 ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left);
                e.Use();
            }
            else if (hover && e.type == EventType.MouseDown && (e.button == 0 || e.button == 1))
            {
                e.Use();
            }
        }

        private static void Click(UnitButtonElement element, PointerEventData.InputButton button)
        {
            if (element == null) return;
            try
            {
                var data = new PointerEventData(EventSystem.current)
                {
                    button = button,
                    clickCount = 1,
                    position = Input.mousePosition,
                };
                ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerUpHandler);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Unit row: the game's button threw ({ex.Message}).");
            }
        }

        // ---- hover ------------------------------------------------------------------
        //
        // The game's buttons emit hover events (a build option under the
        // mouse puts its figures on the unit card; a queue item shows what
        // it is), so the row tells the game's button when the mouse arrives
        // and leaves. Settled once per repaint, after every row has drawn.

        private static UnitButtonElement _hoverThisFrame;
        private static UnitButtonElement _hovered;

        /// Call once per OnGUI after the last row has drawn.
        internal static void FlushHover()
        {
            if (Event.current.type != EventType.Repaint) return;
            var now = _hoverThisFrame;
            _hoverThisFrame = null;
            if (now == _hovered) return;
            Hover(_hovered, false);
            _hovered = now;
            Hover(now, true);
        }

        /// Sends a leave to whatever is hovered, for when the rows stop drawing.
        internal static void ClearHover()
        {
            if (_hovered == null) return;
            Hover(_hovered, false);
            _hovered = null;
            _hoverThisFrame = null;
        }

        private static void Hover(UnitButtonElement element, bool enter)
        {
            if (element == null) return;
            try
            {
                var data = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
                if (enter) ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerEnterHandler);
                else ExecuteEvents.Execute(element.gameObject, data, ExecuteEvents.pointerExitHandler);
            }
            catch { /* a button on its way out */ }
        }
    }
}
