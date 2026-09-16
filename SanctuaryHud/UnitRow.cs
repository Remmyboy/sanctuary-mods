using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // Reading the game's unit-button panels. Three of them are lists of
    // UnitButtonElement — the selection list, the build options and the
    // build queue — each with a portrait, an overlay text (a count, or a
    // hotkey) and sometimes a progress bar. Collect reads such a panel's
    // live buttons in order for a TileRow to stand in for them; the element
    // colours (DomainColours) live here too.
    internal static class UnitRow
    {
        internal sealed class Entry
        {
            public UnitButtonElement Element;   // null for a separator
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
                    // A "coming soon" placeholder: the Lua adds it with click
                    // and hover events off (constructionPanel.lua's demo units).
                    if (!element.emitClickEvents) continue;
                    if (element.textOverlayText != null && element.textOverlayText.text == "?") continue;
                    // ...and one with no art of its own wears the prefab's
                    // default portrait, which is the "coming soon" picture.
                    if (element.portraitImage != null && element.defaultPortrait != null &&
                        element.portraitImage.overrideSprite == element.defaultPortrait) continue;
                }
                row.Add(new Entry { Element = element });
            }
            while (row.Count > 0 && row[row.Count - 1].Element == null) row.RemoveAt(row.Count - 1);
        }

        // ---- the element colours ---------------------------------------------
        //
        // The tiles, by the unit's element: green for land, blue for water,
        // a hard split along the bottom-left to top-right diagonal — land
        // above, water below — for one that goes on both, a lighter blue for
        // the air, and a neutral slate where the element isn't known.

        private static readonly Color LandColour = new Color(0.13f, 0.42f, 0.27f, 0.95f);
        private static readonly Color NavalColour = new Color(0.10f, 0.24f, 0.44f, 0.95f);
        private static readonly Color AirColour = new Color(0.24f, 0.50f, 0.72f, 0.95f);
        private static readonly Color UnknownColour = new Color(0.15f, 0.19f, 0.25f, 0.95f);
        private static readonly Dictionary<UnitDomains.Domain, Texture2D> _tiles = new Dictionary<UnitDomains.Domain, Texture2D>();

        internal static Texture2D Tile(UnitDomains.Domain domain)
        {
            if (_tiles.TryGetValue(domain, out var tile) && tile != null) return tile;
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

        internal static Color EdgeFor(UnitDomains.Domain domain)
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
    }
}
