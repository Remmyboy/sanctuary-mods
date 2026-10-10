using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // An engineer's queue of buildings, shown where a factory's queue is.
    //
    // The game shows a factory's queue and nothing for an engineer: its
    // buildings are orders, not a build queue (predictedBuildQueue stays
    // empty). With this on, selected builders that share one queue get a row
    // of its buildings above the build options, in order, runs of the same
    // building as one tile with a count, as a factory's queue shows them.
    // Right-clicking a tile takes one of that building out and keeps the rest
    // of the queue, before and after it: the queue is re-issued without it,
    // through Waypoints' plan (as Ctrl-clicking its waypoint does), since the
    // host has no way to edit an order once given.
    internal static class EngineerQueue
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("BottomPanels", "EngineerQueue", false,
                "Show the buildings queued for the selected engineers (when they share one queue) as a row of tiles " +
                "where a factory's queue goes, and right-click a tile to take that building out while keeping the " +
                "rest of the queue. Needs the build strip (BuildStrip) on.");
        }

        private sealed class Item
        {
            public int Index, Count;
            public uint Icon, Plate;
        }

        private static readonly List<Item> _items = new List<Item>();
        private static string _raw = "";
        private static float _nextPoll;
        private static RectTransform _rect;
        private static readonly List<Tile> _tiles = new List<Tile>();
        private static Vector2 _tileSize = new Vector2(80f, 80f);
        private static float _scale = -1f;
        private static bool _dirty = true;

        internal static float Width => _rect != null ? _rect.rect.width * _rect.localScale.x : 0f;
        internal static float Height => _rect != null ? _rect.rect.height * _rect.localScale.y : 0f;
        internal static RectTransform Rect => _rect;

        /// From BuildStrip, each frame the build options show and the game
        /// has no queue of its own up: reads the queue (a few times a
        /// second, through Waypoints, which goes in while this is on) and
        /// fills the row. False when there is nothing to show.
        internal static bool Sync(RectTransform root, Vector2 tileSize, float scale, float maxWidth)
        {
            if (!Enabled.Value || root == null)
            {
                Hide();
                return false;
            }
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 0.25f;
                var raw = Waypoints.Queue();
                if (raw != _raw)
                {
                    _raw = raw;
                    Parse(raw);
                    _dirty = true;
                }
            }
            if (_items.Count == 0)
            {
                Hide();
                return false;
            }
            if (_rect == null) Create(root);
            if (!_rect.gameObject.activeSelf)
            {
                _rect.gameObject.SetActive(true);
                _dirty = true;
            }
            if (tileSize != _tileSize)
            {
                _tileSize = tileSize;
                _dirty = true;
            }
            if (scale != _scale)
            {
                _scale = scale;
                _rect.localScale = new Vector3(scale, scale, 1f);
                _dirty = true;
            }
            if (_dirty)
            {
                _dirty = false;
                HudCanvas.PlateStyle(_rect, false);
                // One line of it, as the factory queue keeps: what doesn't
                // fit isn't shown.
                var room = Mathf.Max(1, Mathf.FloorToInt((maxWidth / Mathf.Max(scale, 0.01f) - TileRow.Pad * 2f + TileRow.Gap) / (_tileSize.x + TileRow.Gap)));
                var shown = Mathf.Min(_items.Count, room);
                while (_tiles.Count < shown) _tiles.Add(Tile.Create(_rect));
                for (var i = 0; i < _tiles.Count; i++)
                {
                    var on = i < shown;
                    if (on) _tiles[i].Set(_items[i], _tileSize);
                    if (_tiles[i].gameObject.activeSelf != on) _tiles[i].gameObject.SetActive(on);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
            }
            return true;
        }

        internal static void Place(Vector2 bottomLeft)
        {
            if (_rect != null) _rect.anchoredPosition = bottomLeft;
        }

        internal static void Hide()
        {
            if (_rect != null && _rect.gameObject.activeSelf) _rect.gameObject.SetActive(false);
        }

        internal static void Shutdown()
        {
            if (_rect != null) UnityEngine.Object.Destroy(_rect.gameObject);
            _rect = null;
            _tiles.Clear();
            _items.Clear();
            _raw = "";
        }

        /// 'index:icon:plate' per building; consecutive ones of a kind make
        /// one item, which keeps the index of its first.
        private static void Parse(string raw)
        {
            _items.Clear();
            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                if (f.Length < 3 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;
                uint.TryParse(f[1], out var icon);
                uint.TryParse(f[2], out var plate);
                var last = _items.Count > 0 ? _items[_items.Count - 1] : null;
                if (last != null && last.Icon == icon && last.Plate == plate) last.Count++;
                else _items.Add(new Item { Index = index, Count = 1, Icon = icon, Plate = plate });
            }
        }

        private static void Create(RectTransform root)
        {
            _rect = HudCanvas.Plate(root, "Engineer queue");
            var group = _rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            var pad = (int)TileRow.Pad;
            group.padding = new RectOffset(pad, pad, pad, pad);
            group.spacing = TileRow.Gap;
            group.childAlignment = TextAnchor.LowerLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            HudCanvas.FitToContents(_rect);
            _dirty = true;
        }

        /// One building: its plate and art, the count in the corner where it
        /// is more than one. A right-click takes one out.
        private sealed class Tile : MonoBehaviour, IPointerClickHandler
        {
            private Image _plate, _art;
            private GameObject _countBox;
            private TMP_Text _count;
            private LayoutElement _layout;
            private int _index;

            internal static Tile Create(Transform parent)
            {
                var back = HudCanvas.Fill(parent, "Tile", new Color(0.1f, 0.12f, 0.15f, 0.9f));
                back.raycastTarget = true;
                var tile = back.gameObject.AddComponent<Tile>();
                tile._layout = back.gameObject.AddComponent<LayoutElement>();
                tile._plate = Stretched(back.transform, "Plate");
                tile._art = Stretched(back.transform, "Art");
                var box = HudCanvas.Fill(back.transform, "Count", new Color(0f, 0f, 0f, 0.65f));
                tile._countBox = box.gameObject;
                var brt = box.rectTransform;
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 0f);
                brt.sizeDelta = new Vector2(28f, 24f);
                tile._count = HudCanvas.Text(box.transform, "Text", 20f, Color.white, TextAlignmentOptions.Center);
                var crt = tile._count.rectTransform;
                crt.anchorMin = Vector2.zero;
                crt.anchorMax = Vector2.one;
                crt.offsetMin = Vector2.zero;
                crt.offsetMax = Vector2.zero;
                HoverGlow.Add(back.gameObject);
                return tile;
            }

            private static Image Stretched(Transform parent, string name)
            {
                var image = HudCanvas.Fill(parent, name, Color.white);
                var rt = image.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                image.preserveAspect = true;
                image.enabled = false;
                return image;
            }

            internal void Set(Item item, Vector2 size)
            {
                _index = item.Index;
                _layout.preferredWidth = _layout.minWidth = size.x;
                _layout.preferredHeight = _layout.minHeight = size.y;
                var art = SpriteFor(item.Icon);
                var plate = art != null ? SpriteFor(item.Plate) : null;
                _plate.sprite = plate;
                _plate.enabled = plate != null;
                _art.sprite = art;
                _art.enabled = art != null;
                var many = item.Count > 1;
                if (many) HudCanvas.SetText(_count, item.Count.ToString(CultureInfo.InvariantCulture));
                if (_countBox.activeSelf != many) _countBox.SetActive(many);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Right) Waypoints.RemoveAt(_index);
            }
        }
    }
}
