using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // A row of unit tiles (UnitTile) on the HUD canvas: the game's panel
    // colour behind them with the accent hairline along the top, laid out
    // by a HorizontalLayoutGroup and sized to its contents, with a break —
    // a gap with a line down its middle — where the game's list put a
    // separator. Anchored by its bottom-left corner; Place puts that where
    // the caller wants it, in canvas units from the screen's bottom-left.
    //
    // Sync takes the entries UnitRow.Collect read off a game panel: the
    // children are rebuilt only when the sequence of game buttons changes,
    // and every tile mirrors its button every frame.
    internal sealed class TileRow
    {
        internal const float Gap = 8f;
        internal const float Pad = 10f;
        internal const float SeparatorWidth = 32f;

        private RectTransform _rect;
        private SanctuaryPanelUI _panel;
        private readonly List<UnitTile> _tiles = new List<UnitTile>();
        private readonly List<GameObject> _separators = new List<GameObject>();
        private readonly List<UnitButtonElement> _shown = new List<UnitButtonElement>();
        private readonly List<UnitTile> _live = new List<UnitTile>();

        internal RectTransform Rect => _rect;
        internal bool Alive => _rect != null;
        internal bool Showing => _rect != null && _rect.gameObject.activeSelf;

        internal static TileRow Create(RectTransform root, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;

            // The plate is a raycast target: the gaps between tiles must not
            // let a click through to the map either.
            var back = go.AddComponent<Image>();
            back.color = SanctuaryHudPlugin.GamePanelColour;
            back.raycastTarget = true;

            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);
            group.spacing = Gap;
            group.childAlignment = TextAnchor.LowerLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            var line = HudCanvas.Fill(rt, "Accent", accent);
            HudCanvas.StretchAlongTop(line.rectTransform, 2f);
            line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            go.SetActive(false);
            return new TileRow { _rect = rt };
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

        /// The row's width on the canvas, its scale included.
        internal float Width => _rect != null ? _rect.rect.width * _rect.localScale.x : 0f;

        internal void Destroy()
        {
            if (_rect != null) Object.Destroy(_rect.gameObject);
            _rect = null;
            _tiles.Clear();
            _separators.Clear();
            _shown.Clear();
            _live.Clear();
        }

        /// Makes the row show these entries, at this scale, mirroring the
        /// game's buttons; badges puts the strategic icon on each tile.
        internal void Sync(SanctuaryPanelUI panel, List<UnitRow.Entry> entries, bool badges, float scale)
        {
            if (_rect == null) return;
            if (panel != _panel)
            {
                // Tiles are clones of the panel's own prefab; a new panel
                // (a new match) means new tiles.
                _panel = panel;
                foreach (var tile in _tiles) if (tile != null) Object.Destroy(tile.gameObject);
                foreach (var separator in _separators) if (separator != null) Object.Destroy(separator);
                _tiles.Clear();
                _separators.Clear();
                _shown.Clear();
                _live.Clear();
            }
            _rect.localScale = new Vector3(scale, scale, 1f);

            var same = entries.Count == _shown.Count;
            for (var i = 0; same && i < entries.Count; i++) same = entries[i].Element == _shown[i];
            if (!same) Rebuild(panel, entries);

            for (var i = 0; i < _live.Count && i < entries.Count; i++)
            {
                var tile = _live[i];
                if (tile == null) continue;
                var entry = entries[i];
                if (entry.Element != null) tile.Mirror(entry.Element, entry.Width * 2f, badges);
            }

            // So the width is right for whoever lays out beside the row this frame.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
        }

        private void Rebuild(SanctuaryPanelUI panel, List<UnitRow.Entry> entries)
        {
            foreach (var tile in _tiles) if (tile != null && tile.gameObject.activeSelf) tile.gameObject.SetActive(false);
            foreach (var separator in _separators) if (separator != null && separator.activeSelf) separator.SetActive(false);
            _shown.Clear();
            _live.Clear();
            var tileAt = 0;
            var separatorAt = 0;
            // The hairline is child 0; the layout skips it.
            var sibling = 1;
            foreach (var entry in entries)
            {
                GameObject child;
                UnitTile tile = null;
                if (entry.Element != null)
                {
                    while (tileAt < _tiles.Count && _tiles[tileAt] == null) _tiles.RemoveAt(tileAt);
                    if (tileAt < _tiles.Count) tile = _tiles[tileAt];
                    else
                    {
                        tile = UnitTile.Create(panel, _rect);
                        if (tile == null) continue;
                        _tiles.Add(tile);
                    }
                    tileAt++;
                    child = tile.gameObject;
                }
                else
                {
                    while (separatorAt < _separators.Count && _separators[separatorAt] == null) _separators.RemoveAt(separatorAt);
                    if (separatorAt < _separators.Count) child = _separators[separatorAt];
                    else
                    {
                        child = MakeSeparator();
                        _separators.Add(child);
                    }
                    separatorAt++;
                }
                child.transform.SetSiblingIndex(sibling++);
                if (!child.activeSelf) child.SetActive(true);
                _shown.Add(entry.Element);
                _live.Add(tile);
            }
        }

        private GameObject MakeSeparator()
        {
            var go = new GameObject("Break", typeof(RectTransform));
            go.transform.SetParent(_rect, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = SeparatorWidth;
            layout.minWidth = SeparatorWidth;
            layout.preferredHeight = UnitTile.Size;
            layout.minHeight = UnitTile.Size;
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.35f;
            var line = HudCanvas.Fill(go.transform, "Line", accent);
            var lrt = line.rectTransform;
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(4f, UnitTile.Size - 16f);
            go.SetActive(false);
            return go;
        }
    }
}
