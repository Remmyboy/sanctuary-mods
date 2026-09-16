using System.Collections.Generic;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // A row of unit tiles (UnitTile) on the HUD canvas: the game's panel
    // colour behind them with the accent hairline along the top, laid out
    // by layout groups and sized to the contents, with a break — a gap with
    // a line down its middle — where the game's list put a separator. A row
    // given a width limit wraps onto further lines, the first line at the
    // top, and the plate grows to hold them. Anchored by its bottom-left
    // corner; Place puts that where the caller wants it, in canvas units
    // from the screen's bottom-left.
    //
    // Sync takes the entries UnitRow.Collect read off a game panel, and the
    // panel whose button prefab the tiles clone (the build panel's, for
    // every row, so they all match): the children are rebuilt only when the
    // sequence of game buttons, or the way it wraps, changes, and every tile
    // mirrors its button every frame.
    internal sealed class TileRow
    {
        internal const float Gap = 8f;
        internal const float Pad = BottomDock.Pad;
        internal const float SeparatorWidth = 32f;

        private RectTransform _rect;
        private SanctuaryPanelUI _panel;
        private Vector2 _tileSize = new Vector2(80f, 80f);
        private readonly List<UnitTile> _tiles = new List<UnitTile>();
        private readonly List<GameObject> _separators = new List<GameObject>();
        private readonly List<RectTransform> _lines = new List<RectTransform>();
        private readonly List<UnitButtonElement> _shown = new List<UnitButtonElement>();
        private readonly List<int> _shownLines = new List<int>();
        private readonly List<UnitTile> _live = new List<UnitTile>();
        private readonly List<int> _wrap = new List<int>();

        internal RectTransform Rect => _rect;
        internal bool Alive => _rect != null;
        internal bool Showing => _rect != null && _rect.gameObject.activeSelf;

        internal static TileRow Create(RectTransform root, string name)
        {
            var rt = HudCanvas.Plate(root, name);
            var group = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);
            group.spacing = Gap;
            group.childAlignment = TextAnchor.LowerLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            HudCanvas.FitToContents(rt);
            rt.gameObject.SetActive(false);
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

        /// The row's size on the canvas, its scale included.
        internal float Width => _rect != null ? _rect.rect.width * _rect.localScale.x : 0f;
        internal float Height => _rect != null ? _rect.rect.height * _rect.localScale.y : 0f;

        internal void Destroy()
        {
            if (_rect != null) Object.Destroy(_rect.gameObject);
            _rect = null;
            _tiles.Clear();
            _separators.Clear();
            _lines.Clear();
            _shown.Clear();
            _shownLines.Clear();
            _live.Clear();
        }

        /// Makes the row show these entries, at this scale, mirroring the
        /// game's buttons with tiles cloned from prefabPanel's prefab;
        /// maxWidth (canvas units, at the given scale) wraps it, and maxLines
        /// (0 for no limit) cuts it off: what doesn't fit isn't shown.
        internal void Sync(SanctuaryPanelUI prefabPanel, List<UnitRow.Entry> entries, float scale, float maxWidth = float.MaxValue, int maxLines = 0)
        {
            if (_rect == null) return;
            if (prefabPanel != _panel)
            {
                // Tiles are clones of the panel's prefab; a new panel (a new
                // match) means new tiles.
                _panel = prefabPanel;
                foreach (var tile in _tiles) if (tile != null) Object.Destroy(tile.gameObject);
                foreach (var separator in _separators) if (separator != null) Object.Destroy(separator);
                _tiles.Clear();
                _separators.Clear();
                _shown.Clear();
                _shownLines.Clear();
                _live.Clear();
                _tileSize = UnitTile.NativeSize(prefabPanel);
            }
            HudCanvas.PlateStyle(_rect, BottomDock.PanelArt != null && BottomDock.PanelArt.Value, false);
            _rect.localScale = new Vector3(scale, scale, 1f);

            Wrap(entries, maxWidth / Mathf.Max(scale, 0.01f), maxLines);

            var same = _wrapTotal == _shown.Count && _wrap.Count == _shownLines.Count;
            for (var i = 0; same && i < _wrapTotal; i++) same = entries[i].Element == _shown[i];
            for (var i = 0; same && i < _wrap.Count; i++) same = _wrap[i] == _shownLines[i];
            if (!same) Rebuild(prefabPanel, entries);

            for (var i = 0; i < _live.Count && i < entries.Count; i++)
            {
                var tile = _live[i];
                if (tile == null) continue;
                var entry = entries[i];
                if (entry.Element != null) tile.Mirror(entry.Element);
            }

            // So the size is right for whoever lays out beside the row this frame.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
        }

        /// Splits the entries into lines no wider than maxWidth (in unscaled
        /// canvas units, padding included), breaking between tiles; a
        /// separator never starts or ends a line. The result is the number
        /// of entries on each line, in order; a separator dropped at a break
        /// counts on the line it would have ended.
        private int _wrapTotal;

        private void Wrap(List<UnitRow.Entry> entries, float maxWidth, int maxLines)
        {
            _wrap.Clear();
            _wrapTotal = 0;
            var count = 0;
            var width = Pad * 2f;
            foreach (var entry in entries)
            {
                var w = entry.Element != null ? _tileSize.x : SeparatorWidth;
                if (count > 0 && width + w > maxWidth && entry.Element != null)
                {
                    // Past the last line allowed, the rest is left off.
                    if (maxLines > 0 && _wrap.Count + 1 >= maxLines) break;
                    _wrap.Add(count);
                    _wrapTotal += count;
                    count = 0;
                    width = Pad * 2f;
                }
                count++;
                width += w + Gap;
            }
            if (count > 0)
            {
                _wrap.Add(count);
                _wrapTotal += count;
            }
        }

        private void Rebuild(SanctuaryPanelUI panel, List<UnitRow.Entry> entries)
        {
            foreach (var tile in _tiles) if (tile != null && tile.gameObject.activeSelf) tile.gameObject.SetActive(false);
            foreach (var separator in _separators) if (separator != null && separator.activeSelf) separator.SetActive(false);
            foreach (var line in _lines) if (line != null && line.gameObject.activeSelf) line.gameObject.SetActive(false);
            _shown.Clear();
            _shownLines.Clear();
            _live.Clear();
            var tileAt = 0;
            var separatorAt = 0;
            var lineAt = 0;
            var index = 0;
            // The hairline is child 0 of the plate; the layout skips it.
            var lineSibling = 1;
            foreach (var length in _wrap)
            {
                var line = TakeLine(ref lineAt);
                line.SetSiblingIndex(lineSibling++);
                if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
                var sibling = 0;
                var end = index + length;
                // A separator at the start or the end of a line is a gap
                // with nothing to separate: left out of the layout.
                var first = index;
                var last = end - 1;
                for (; index < end; index++)
                {
                    var entry = entries[index];
                    UnitTile tile = null;
                    GameObject child;
                    if (entry.Element != null)
                    {
                        while (tileAt < _tiles.Count && _tiles[tileAt] == null) _tiles.RemoveAt(tileAt);
                        if (tileAt < _tiles.Count) tile = _tiles[tileAt];
                        else
                        {
                            tile = UnitTile.Create(panel, line);
                            if (tile == null) continue;
                            _tiles.Add(tile);
                        }
                        tileAt++;
                        child = tile.gameObject;
                    }
                    else
                    {
                        _shown.Add(null);
                        _live.Add(null);
                        if (index == first || index == last) continue;
                        while (separatorAt < _separators.Count && _separators[separatorAt] == null) _separators.RemoveAt(separatorAt);
                        if (separatorAt < _separators.Count) child = _separators[separatorAt];
                        else
                        {
                            child = MakeSeparator();
                            _separators.Add(child);
                        }
                        separatorAt++;
                    }
                    if (child.transform.parent != line) child.transform.SetParent(line, false);
                    child.transform.SetSiblingIndex(sibling++);
                    if (!child.activeSelf) child.SetActive(true);
                    if (tile != null)
                    {
                        _shown.Add(entry.Element);
                        _live.Add(tile);
                    }
                }
                _shownLines.Add(length);
            }
        }

        private RectTransform TakeLine(ref int lineAt)
        {
            while (lineAt < _lines.Count && _lines[lineAt] == null) _lines.RemoveAt(lineAt);
            if (lineAt < _lines.Count) return _lines[lineAt++];
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(_rect, false);
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.spacing = Gap;
            group.childAlignment = TextAnchor.LowerLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var line = (RectTransform)go.transform;
            _lines.Add(line);
            lineAt++;
            return line;
        }

        private GameObject MakeSeparator()
        {
            var go = new GameObject("Break", typeof(RectTransform));
            go.transform.SetParent(_rect, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = SeparatorWidth;
            layout.minWidth = SeparatorWidth;
            layout.preferredHeight = _tileSize.y;
            layout.minHeight = _tileSize.y;
            var accent = AccentColour;
            accent.a = 0.35f;
            var line = HudCanvas.Fill(go.transform, "Line", accent);
            var lrt = line.rectTransform;
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(4f, _tileSize.y - 16f);
            go.SetActive(false);
            return go;
        }
    }
}
