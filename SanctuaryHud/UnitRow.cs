using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Reading the game's unit-button panels. Three of them are lists of
    // UnitButtonElement — the selection list, the build options and the
    // build queue — each with a portrait, an overlay text (a count, or a
    // hotkey) and sometimes a progress bar. Collect reads such a panel's
    // live buttons in order for a TileRow to stand in for them.
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

    }
}
