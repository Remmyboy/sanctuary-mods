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
        /// A struct, so a row read every frame allocates nothing.
        internal struct Entry
        {
            public UnitButtonElement Element;   // null for a separator
        }

        /// What each panel's buttons carry, looked up once per button: the
        /// panels pool their buttons, so they are the same objects frame
        /// after frame.
        private static readonly PanelChildren<UnitButtonElement, Button> _children = new PanelChildren<UnitButtonElement, Button>();

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
                var (element, button) = _children.Get(container, child);
                if (element == null)
                {
                    if (row.Count > 0 && row[row.Count - 1].Element != null) row.Add(new Entry());
                    continue;
                }
                if (liveOnly)
                {
                    if (button != null && !button.interactable) continue;
                    // A "coming soon" placeholder: the Lua adds it with click
                    // and hover events off (constructionPanel.lua's demo units).
                    // Not the '?' label, which marks a real option with no
                    // hotkey: SetText never writes '?' (it hides the label
                    // instead), so the text is whatever the pooled button last
                    // held, and the fresh ones' prefab text would drop them.
                    if (!element.emitClickEvents) continue;
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

    /// Two GetComponent answers per child of a game panel's container, kept
    /// rather than asked again every frame. Several containers share one (the
    /// selection list, the build options and the queue are each read every
    /// frame); a container's children are pooled, and the whole lot is
    /// dropped when it grows well past what the containers hold, which
    /// clears out buttons destroyed with an old match.
    internal sealed class PanelChildren<T1, T2> where T1 : Component where T2 : Component
    {
        private readonly Dictionary<Transform, (T1, T2)> _found = new Dictionary<Transform, (T1, T2)>();
        private int _limit = 256;

        internal (T1, T2) Get(Transform container, Transform child)
        {
            if (_found.TryGetValue(child, out var found)) return found;
            if (_found.Count >= _limit)
            {
                _found.Clear();
                _limit = Mathf.Max(256, container.childCount * 8);
            }
            found = (child.GetComponent<T1>(), child.GetComponent<T2>());
            _found[child] = found;
            return found;
        }
    }
}
