using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Drag a factory's queue tile to move that item in the queue.
    //
    // The host has no way to reorder a queue. It takes one queue command,
    // RequestQueueAmount (add or take away N of an item), and an item it has
    // not seen before goes on the end. So a move rebuilds the queue from the
    // first position that changes: everything from there to the back is taken
    // off, last first, and put back on in the new order. Each step goes the way
    // a queue button click does (constructionBuildQueuePanel.lua): predicted
    // locally with ModifyBuildQueue, recorded as a pending operation, sent.
    //
    // Moving an item to the very front takes off the item in hand, and the host
    // cancels what the factory was building (ModifyBuildQueue calls
    // OnCancelConstruction whenever the head of the queue changes), so the new
    // front item starts at once. Anywhere further back leaves the current
    // build alone.
    internal static class QueueReorder
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "ReorderQueueByDragging", false,
                "Drag a tile in the build strip's queue to move that item. Dropping it at the very front cancels " +
                "what the factory is building and starts the moved item instead; anywhere else waits for the current " +
                "build. Applies to every selected factory sharing the queue shown.");
        }

        /// Moves queue item `from` (0-based, as shown) to sit before the tile
        /// that was at `slot` (0-based; the count means the end).
        internal static void Apply(int from, int slot)
        {
            // Where the item ends up, 1-based, once it is out of the list.
            var to = slot > from ? slot : slot + 1;
            if (to == from + 1) return;
            var chunk = MoveChunk
                .Replace("__FROM__", (from + 1).ToString(CultureInfo.InvariantCulture))
                .Replace("__TO__", to.ToString(CultureInfo.InvariantCulture));
            RunLua(chunk);
        }

        private const string MoveChunk = @"
local ok, err = pcall(function()
  if IsObserver() then return end
  local from, to = __FROM__, __TO__
  local SS = Import('client/input/selectionSystem.lua')
  local BQ = Import('common/commands/definitions/buildQueue.lua')

  local function sig(q)
    local t = {}
    for i, it in ipairs(q) do t[i] = it.tpId .. 'x' .. it.count end
    return table.concat(t, ',')
  end

  -- The panel shows a queue only when every selected unit has that same
  -- queue (uiManager.lua), and its clicks go to all of them; so does this.
  local ref, units = nil, {}
  for _, u in pairs(SS.GetSelectedUnits()) do
    local q = u.id and u.predictedBuildQueue
    if q and #q > 0 and u.tp.construction and u.tp.construction.canBuild then
      ref = ref or sig(q)
      if sig(q) == ref then table.insert(units, u) end
    end
  end

  for _, u in ipairs(units) do
    local q = u.predictedBuildQueue
    local n = #q
    if from <= n and to <= n then
      local old, order = {}, {}
      for i = 1, n do
        old[i] = { tpId = q[i].tpId, count = q[i].count, id = next(q[i].queueItemIds) }
        order[i] = old[i]
      end
      table.insert(order, to, table.remove(order, from))

      -- Refuse an order the queue would not keep: a unit moved ahead of the
      -- factory upgrade that unlocks it is dropped by CleanUpAndMergeQueueItems.
      local sim = {}
      for i, it in ipairs(order) do sim[i] = { tpId = it.tpId, count = it.count, queueItemIds = {} } end
      u.predictedBuildQueue = sim
      local buildable = true
      for i = 1, #sim do
        if not buildQueueUtils.CanBuild(u, sim[i].tpId, i, true) then buildable = false end
      end
      u.predictedBuildQueue = q

      if buildable then
        local k = math.min(from, to)
        local function step(id, tpId, delta)
          local newId = buildQueueUtils.ModifyBuildQueue(u, id, tpId, delta, true)
          if delta > 0 then
            if not newId then return end
            id = newId
          end
          table.insert(u.buildQueuePendingOperations, { deltaAmount = delta, queueItemId = id, tpID = tpId })
          BQ.RequestQueueAmount.Send({ u.id }, { id }, tpId, delta)
        end
        for i = n, k, -1 do step(old[i].id, old[i].tpId, -old[i].count) end
        for i = k, n do step(-1, order[i].tpId, order[i].count) end
      end
    end
  end
  Import('client/ui/uiManager.lua').SetUIDirty()
end)
if not ok then Warn('SanctuaryHud queue reorder: ' .. tostring(err)) end";

        /// On each tile of the queue row. A drag lifts the tile — dimmed, its
        /// portrait under the cursor — and a bar shows where it will land.
        /// The press it started from is taken back from the game's button,
        /// so letting go never also counts as a click on it.
        internal sealed class Drag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            internal TileRow Row;
            internal int Index;

            private bool _dragging;
            private Sprite _sprite;
            private CanvasGroup _group;
            private Image _bar, _ghost;

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || Row == null || Enabled == null || !Enabled.Value) return;
                var tile = GetComponent<UnitTile>();
                if (tile == null) return;
                _dragging = true;
                _sprite = tile.Portrait;
                tile.CancelPress(eventData);

                _group = GetComponent<CanvasGroup>();
                if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
                _group.alpha = 0.35f;

                var row = Row.Rect;
                _bar = HudCanvas.Fill(row, "Drop here", AccentColour);
                _bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                _ghost = HudCanvas.Fill(row, "Dragged", Color.white);
                _ghost.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                _ghost.sprite = _sprite;
                _ghost.preserveAspect = true;
                _ghost.enabled = _sprite != null;
                var size = ((RectTransform)transform).rect.size * 0.8f;
                _ghost.rectTransform.sizeDelta = size;
                OnDrag(eventData);
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (!_dragging || Row == null || Row.Rect == null) return;
                var row = Row.Rect;
                var cam = eventData.pressEventCamera;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(row, eventData.position, cam, out var local))
                {
                    _ghost.rectTransform.localPosition = local;
                    _ghost.transform.SetAsLastSibling();
                }

                var slot = Slot(eventData.position, cam, out var edge, out var height);
                _bar.enabled = slot >= 0;
                if (slot >= 0)
                {
                    _bar.rectTransform.sizeDelta = new Vector2(4f, height);
                    _bar.rectTransform.localPosition = edge;
                }
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (!_dragging) return;
                var slot = Row != null && Row.Rect != null ? Slot(eventData.position, eventData.pressEventCamera, out _, out _) : -1;
                // The queue can move under a drag (an item finishes); only a
                // tile still showing what was picked up is moved.
                var tile = GetComponent<UnitTile>();
                var same = tile != null && tile.Portrait == _sprite;
                Finish();
                if (slot >= 0 && same && InMatch)
                {
                    try { Apply(Index, slot); }
                    catch (Exception e) { _log?.LogWarning($"Queue reorder failed: {e.Message}"); }
                }
            }

            private void OnDisable() => Finish();

            private void Finish()
            {
                _dragging = false;
                if (_group != null) _group.alpha = 1f;
                if (_bar != null) Destroy(_bar.gameObject);
                if (_ghost != null) Destroy(_ghost.gameObject);
                _bar = null;
                _ghost = null;
            }

            /// The gap the pointer is nearest, as the index of the tile it
            /// comes before (the count for the end), with where to draw it in
            /// the row's own space. -1 when the pointer is off the row.
            private int Slot(Vector2 screen, Camera cam, out Vector3 edge, out float height)
            {
                edge = Vector3.zero;
                height = 0f;
                var row = Row.Rect;
                var count = Row.LiveCount;
                if (count == 0) return -1;

                // Off the row's plate by more than a tile's height: no drop.
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(row, screen, cam, out var inRow)) return -1;
                var plate = row.rect;
                var first = Row.LiveAt(0);
                var tileHeight = first != null ? ((RectTransform)first.transform).rect.height : plate.height;
                if (inRow.y < plate.yMin - tileHeight || inRow.y > plate.yMax + tileHeight) return -1;

                var corners = new Vector3[4];
                var slot = count;
                for (var i = 0; i < count; i++)
                {
                    var t = Row.LiveAt(i);
                    if (t == null) continue;
                    var rt = (RectTransform)t.transform;
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screen, cam, out var inTile)) continue;
                    if (inTile.x < rt.rect.center.x) { slot = i; break; }
                }

                var at = Row.LiveAt(Mathf.Min(slot, count - 1));
                if (at == null) return -1;
                var atRect = (RectTransform)at.transform;
                atRect.GetWorldCorners(corners);
                // Halfway into the gap before the tile, or after the last one.
                var x = slot < count ? (corners[0] + corners[1]) * 0.5f : (corners[2] + corners[3]) * 0.5f;
                var local = row.InverseTransformPoint(x);
                local.x += (slot < count ? -1f : 1f) * TileRow.Gap * 0.5f;
                edge = local;
                height = atRect.rect.height;
                return slot;
            }
        }
    }
}
