using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Factory rally points you can see, and waypoints you can drag, delete
    // and select by.
    //
    // A factory's rally point lives only on the host (HostFactory.rallyPoint):
    // the client sends RequestRallyPoint and never hears about it again, so
    // nothing ever draws it. This remembers what this client sent and draws it
    // in the game's own order-line style, from the factory to the point, for
    // selected factories and, while Shift shows every order, for all of yours.
    //
    // Orders cannot be edited once issued. The host takes exactly two order
    // messages, ADD and CLEAR (commonOrders.lua), and the client keeps no
    // payload for an order (which building, which target). So moving a
    // waypoint re-issues the queue of every unit that has it, with that one
    // position changed: the same ClientIssueOrder path a right-click takes,
    // prediction and all, validated by the host like any other order. The
    // payloads come from watching this client's own IssueOrder sends. The
    // game's own clientOrderManager.lua has "todo !! draggable waypoints".
    //
    // Nothing here edits a Lua file, so ComputeLuaHash is untouched and a
    // modded client still joins unmodded lobbies; everything it sends is a
    // command an unmodded client can send. It works with the overlay hidden
    // too: these are controls, not display.
    internal static class Waypoints
    {
        private static ConfigEntry<bool> _cfgRally;
        private static ConfigEntry<bool> _cfgDrag;
        private static ConfigEntry<float> _cfgGrabPixels;

        private static int _moves;

        // The chunk carries the settings, so a change of setting is a
        // different chunk: LuaHook takes the old one out and puts this in.
        private static readonly LuaHook Hook = new LuaHook("__SdbWaypoints", "waypoints", Chunk)
        {
            LogInstalls = false,
            Installed = OnInstalled,
        };

        private static string _chunk;
        private static string _chunkSignature;

        internal static void Bind(ConfigFile config)
        {
            _cfgDrag = config.Bind("QoL", "DraggableWaypoints", false,
                "Left-drag a move, attack-move or build waypoint, or a rally point, to move it; Ctrl-click one to " +
                "delete it; double-click one to select the units sharing it (Shift adds them). Works on the " +
                "waypoints on screen: your selected units', or all of yours while Shift is held. Moving one " +
                "re-issues the queue of every unit sharing it; engineers with a queued building pause for a " +
                "moment while the old placement clears.");
            _cfgGrabPixels = config.Bind("QoL", "WaypointGrabPixels", 16f,
                new ConfigDescription("How close to a waypoint, in screen pixels, a left press has to land to pick it up.",
                    new AcceptableValueRange<float>(4f, 60f)));
            _cfgRally = config.Bind("QoL", "ShowRallyPoints", false,
                "Draw a line from each factory to the rally point you gave it, the way move orders are drawn: for " +
                "the selected factories, and for all of your factories while Shift is held. The game keeps rally " +
                "points on the host only, so this shows the ones set since the match (or the mod) started.");
        }

        internal static void Shutdown() => Hook.Remove();

        // Alt-Tab mid-drag loses the button's release, and the next click
        // anywhere would then drop the waypoint there. Put it back instead.
        internal static void FocusLost()
        {
            if (!Hook.Live || !LuaReady) return;
            Hook.Call("if __SdbWaypoints.drag then __SdbWaypoints.Cancel() end");
        }

        /// The install chunk with the settings in, made again only when one
        /// of them changes (LuaHook asks for it every check).
        private static string Chunk()
        {
            var grab = Mathf.Clamp(_cfgGrabPixels.Value, 4f, 60f).ToString(CultureInfo.InvariantCulture);
            var signature = (_cfgRally.Value ? "r" : "-") + (_cfgDrag.Value ? "d" : "-") + grab;
            if (_chunk == null || signature != _chunkSignature)
            {
                _chunkSignature = signature;
                _chunk = InstallChunk
                    .Replace("__RALLY__", _cfgRally.Value ? "true" : "false")
                    .Replace("__DRAG__", _cfgDrag.Value ? "true" : "false")
                    .Replace("__GRAB__", grab);
            }
            return _chunk;
        }

        private static void OnInstalled()
        {
            _moves = 0;
            _log?.LogInfo("Waypoints installed for this match" +
                          (_cfgRally.Value ? ": rally points shown" : "") +
                          (_cfgDrag.Value ? (_cfgRally.Value ? ", " : ": ") + "waypoints draggable" : "") + ".");
        }

        private static float _accum;

        internal static void Tick()
        {
            // The client VM exists exactly while a match or replay runs, so it
            // is the whole gate, as in BuildHotkeys. LuaHook checks once a
            // second, puts it back in a new VM, and swaps it for a settings
            // change; a failed install waits before it is tried again.
            Hook.Tick(_cfgRally.Value || _cfgDrag.Value);
            if (!Hook.Live) return;

            _accum += Time.unscaledDeltaTime;
            if (_accum < 1f) return;
            _accum = 0f;
            // Another mod unhooking itself (CameraUtilities puts its saved
            // originals back) can take ours out with it.
            Hook.Call("__SdbWaypoints.Ensure()");
            CountMoves();
        }

        /// The counter of waypoints moved, for the log. A number, since the
        /// read-back bridge is lua_tostring and reads no booleans.
        private static void CountMoves()
        {
            var raw = GetLuaGlobal("__SdbWaypointsCount");
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > _moves)
            {
                _moves = count;
                _log?.LogInfo($"Waypoints: {count} waypoint(s) moved this match.");
            }
        }

        // Installed once per match, guarded by a global so a retry is
        // harmless. Every hook replaces a field of a module's environment
        // table, which is where the game's callers look the function up at call
        // time: inputActions.lua calls Import(...).OnMouseLeftDown() per press,
        // clientMain.lua calls Import(...).UpdateDragFormationPreview() per
        // frame, and a module's own bare calls (Update -> DebugDraw,
        // DrawOrderNode -> GetPosForOrder) resolve against that same table.
        private const string InstallChunk = @"
if not __SdbWaypoints then
  local W = { hooks = {}, drawn = {}, showAll = false }
  W.rallyOn = __RALLY__
  W.dragOn = __DRAG__
  W.grab = __GRAB__
  -- Outlive a reinstall (a settings change), not the match: the VM goes with it.
  __SdbWaypointsRec = __SdbWaypointsRec or {}
  __SdbWaypointsRally = __SdbWaypointsRally or {}
  W.rec = __SdbWaypointsRec
  W.rally = __SdbWaypointsRally
  __SdbWaypointsCount = 0

  local bit = require('bit')
  local COM = Import('client/managers/orders/clientOrderManager.lua')
  local IEF = Import('client/inputEventsFunctions.lua')
  local SS = Import('client/input/selectionSystem.lua')
  local IS = Import('client/input/inputSystem.lua')
  local MC = Import('client/input/mouseCursor.lua')
  local BM = Import('client/input/buildmodeTemp.lua')
  local DFP = Import('client/input/dragFormationPreviewSystem.lua')
  local OC = Import('common/commands/definitions/orders.lua')
  local BQ = Import('common/commands/definitions/buildQueue.lua')
  local PU = Import('common/systems/placementUtils.lua')
  local PL = Import('common/layers.lua').PlacementLayer
  local RS = Import('common/resourceSpot.lua')
  local MU = Import('common/mapUtils.lua')
  local OT = Import('common/commonOrders.lua').OrderTasks
  local PredictedUnit = Import('client/entities/predictedUnit.lua').PredictedUnit
  local ONE = EngineClasses.float3(1, 1, 1)

  -- Waypoints that can be picked up: the ones that are a place. The rest sit
  -- on a target unit, where moving the marker would mean nothing.
  local DRAGGABLE = { [OT.MOVE] = true, [OT.ATTACKMOVE] = true, [OT.DASH] = true, [OT.BUILD] = true }
  local POSITIONAL = { [OT.MOVE] = true, [OT.ATTACKMOVE] = true, [OT.DASH] = true }
  local TARGETED = { [OT.ATTACKUNIT] = true, [OT.REPAIR] = true, [OT.ASSISTUNIT] = true,
                     [OT.RECLAIM] = true, [OT.CAPTURE] = true }

  local function live(u) return u and not u.dead and not u.deleted end
  local function copy3(p) return { x = p.x, y = p.y, z = p.z } end

  -- Hooks. Each wrapper steps aside once this install is gone, so one left in
  -- place under another mod's wrapper does nothing.
  local function hook(tbl, key, make)
    local orig = rawget(tbl, key)
    if type(orig) ~= 'function' then error('Waypoints: nothing to hook at ' .. key) end
    local mine = make(orig)
    rawset(tbl, key, mine)
    table.insert(W.hooks, { t = tbl, k = key, orig = orig, mine = mine })
    return orig
  end
  -- Put ours back where someone restored the original over it; anything else
  -- there is a wrapper around ours and is left alone.
  W.Ensure = function()
    for _, h in ipairs(W.hooks) do
      if rawget(h.t, h.k) == h.orig then rawset(h.t, h.k, h.mine) end
    end
  end
  local function unhook()
    for _, h in ipairs(W.hooks) do
      if rawget(h.t, h.k) == h.mine then rawset(h.t, h.k, h.orig) end
    end
  end

  -- A game update that renames something fails the install part way; what
  -- was hooked by then comes back out, so the retry starts clean.
  local installed, installError = pcall(function()

  ------------------------------------------------------------------ records
  -- What each order was issued with, by order id. The client's order objects
  -- carry position and task only; re-issuing one needs the payload too
  -- (tpId for a building, the target for an assist), and a building's
  -- placement ghost, which only the acknowledgement names.
  hook(OC.IssueOrder, 'Send', function(orig) return function(orderId, append, task, unitIDs, position, payload, ...)
    if __SdbWaypoints == W and orderId then W.rec[orderId] = { task = task, payload = payload } end
    return orig(orderId, append, task, unitIDs, position, payload, ...)
  end end)

  hook(COM, 'OnOrderAcknowledged', function(orig) return function(orderId, accepted, extra, ...)
    if __SdbWaypoints == W then
      local r = W.rec[orderId]
      if r then
        if not accepted then W.rec[orderId] = nil
        elseif r.task == OT.BUILD and extra then r.ghost = extra end
      end
    end
    return orig(orderId, accepted, extra, ...)
  end end)

  hook(COM, 'OnOrderDeleted', function(orig) return function(orderId, ...)
    local res = orig(orderId, ...)
    if __SdbWaypoints == W then W.rec[orderId] = nil end
    return res
  end end)

  -- The rally point, as this client sends it. It never comes back.
  hook(BQ.RequestRallyPoint, 'Send', function(orig) return function(units, pos, ...)
    if __SdbWaypoints == W and pos and not IsObserver() then
      for _, u in pairs(units or {}) do
        if u and u.id then W.rally[u.id.index] = { v = u.id.version, pos = copy3(pos) } end
      end
    end
    return orig(units, pos, ...)
  end end)

  -- Shift shows every order; the flag is a local of the order manager.
  hook(COM, 'SetOrderDraw', function(orig) return function(v, ...)
    if __SdbWaypoints == W then W.showAll = v and true or false end
    return orig(v, ...)
  end end)

  ----------------------------------------------------------------- drawing
  -- The order manager redraws every frame, but what this adds seldom
  -- changes: each frame's lines and nodes are listed first (W.want, flat
  -- numbers), and the prefabs are only remade when the list differs from
  -- what is up (W.shown).
  W.want, W.shown = {}, {}
  local function clearDrawn()
    for _, id in ipairs(W.drawn) do Engine.DeletePrefabInstance(id) end
    W.drawn = {}
    W.shown = {}
  end

  -- The same prefabs and calls CreateOrderLine and DrawOrderNode use, with
  -- the colours DebugDraw gives each task.
  local LINE_STYLE = { [OT.BUILD] = 1, [OT.REPAIR] = 1, [OT.ASSISTUNIT] = 1, [OT.RECLAIM] = 1, [OT.CAPTURE] = 1,
                       [OT.ATTACKUNIT] = 2, [OT.ATTACKMOVE] = 2, [OT.DASH] = 3 }
  local function nodePrefab(task)
    if LINE_STYLE[task] == 1 then return _G.OrderNodePrefabIDOrange end
    if LINE_STYLE[task] == 2 then return _G.OrderNodePrefabIDRed end
    if LINE_STYLE[task] == 3 then return _G.OrderNodePrefabIDPurple end
    return _G.OrderNodePrefabIDBlue
  end
  -- Eight numbers an item: kind (1 line, 2 node), a, b (0s for a node), task.
  local function wantLine(a, b, task)
    local w, n = W.want, #W.want
    w[n + 1], w[n + 2], w[n + 3], w[n + 4] = 1, a.x, a.y, a.z
    w[n + 5], w[n + 6], w[n + 7], w[n + 8] = b.x, b.y, b.z, task or 0
  end
  local function wantNode(p, task)
    local w, n = W.want, #W.want
    w[n + 1], w[n + 2], w[n + 3], w[n + 4] = 2, p.x, p.y, p.z
    w[n + 5], w[n + 6], w[n + 7], w[n + 8] = 0, 0, 0, task or 0
  end
  local function drawLine(a, b, task)
    a = MU.SnapToWaterVisualMarkerHeight(copy3(a))
    b = MU.SnapToWaterVisualMarkerHeight(copy3(b))
    local id = EngineClasses.LocalID()
    Engine.InstantiatePrefab(_G.OrderLinePrefabID, b, ONE, GetIdentityQuaternion(), id)
    Engine.SetLineRendererPointTypes(id, LinePointType.Position, LinePointType.Position, LinePointType.Position, LinePointType.Position)
    local c = math3D.scale(math3D.add(a, b), 0.5)
    Engine.SetLineRendererPoints(id, a, c, c, b)
    Engine.SetLineRendererStyle(id, LINE_STYLE[task] or 0)
    table.insert(W.drawn, id)
  end
  local function drawNode(p, task)
    local id = {}
    Engine.InstantiatePrefab(nodePrefab(task), MU.SnapToWaterVisualMarkerHeight(copy3(p)), ONE, GetIdentityQuaternion(), id)
    table.insert(W.drawn, id)
  end

  -- While a re-issue waits for old placements to clear, the units hold no
  -- orders; draw the queues they are about to get so nothing blinks out.
  local function drawPendingJob()
    local J = W.job
    if not (J and J.phase == 'wait') then return end
    for _, grp in ipairs(J.groups) do
      local prev, n = { x = 0, y = 0, z = 0 }, 0
      for _, u in ipairs(grp.units) do
        if live(u) then
          local p = u:GetPosition()
          prev.x, prev.y, prev.z, n = prev.x + p.x, prev.y + p.y, prev.z + p.z, n + 1
        end
      end
      if n > 0 then
        prev.x, prev.y, prev.z = prev.x / n, prev.y / n, prev.z / n
        for _, s in ipairs(grp.specs) do
          local task = s.build and OT.BUILD or s.task
          wantLine(prev, s.pos, task)
          wantNode(s.pos, task)
          prev = s.pos
        end
      end
    end
  end

  local function isFactory(u)
    return live(u) and u.tp and u.tp.general and Tags.FACTORY[u.tp.general.tpId] and u:IsCompleted()
  end

  -- A factory's rally point, if this client set one. An upgrade takes its
  -- factory's rally point on the host, so the new one inherits it here too:
  -- the upgrade site's upgrader is the factory it replaces.
  local function rallyOf(f)
    local r = W.rally[f.id.index]
    if r and r.v == f.id.version then return r.pos end
    local up = f.upgrader
    if up and up.id then
      local r2 = W.rally[up.id.index]
      if r2 and r2.v == up.id.version then
        W.rally[f.id.index] = { v = f.id.version, pos = r2.pos }
        return r2.pos
      end
    end
  end

  local function ownUnits()
    local a = Armies[GetFocusArmy()]
    return a and a.units or {}
  end

  local function shownFactories()
    local out = {}
    if W.showAll then
      for _, u in pairs(ownUnits()) do if isFactory(u) then table.insert(out, u) end end
    else
      local focus = GetFocusArmy()
      for _, u in pairs(SS.GetSelectedUnits()) do
        if u.armyId == focus and isFactory(u) then table.insert(out, u) end
      end
    end
    return out
  end

  -- What is wanted this frame, into W.want.
  W.inheritTick = 0
  local function listExtras()
    -- CameraUtilities hiding order lines hides these as well.
    local cu = rawget(_G, '__CameraUtils')
    if cu and cu.orders then return end
    drawPendingJob()
    if not W.rallyOn or IsObserver() then return end
    -- Upgrade sites have to be seen while they still point at the old
    -- factory, selected or not.
    W.inheritTick = W.inheritTick + 1
    if W.inheritTick >= 5 then
      W.inheritTick = 0
      for _, u in pairs(ownUnits()) do
        if live(u) and u.upgrader and u.tp and Tags.FACTORY[u.tp.general.tpId] then rallyOf(u) end
      end
    end
    local D = W.drag
    for _, f in ipairs(shownFactories()) do
      local p = rallyOf(f)
      if p then
        if D and D.active and D.rally == f and D.pos then p = D.pos end
        wantLine(f:GetPosition(), p, OT.MOVE)
        wantNode(p, OT.MOVE)
      end
    end
  end

  W.DrawExtras = function()
    W.want = {}
    listExtras()
    local want, shown = W.want, W.shown
    local same = #want == #shown
    for i = 1, #want do
      if not same then break end
      same = want[i] == shown[i]
    end
    if same then return end
    clearDrawn()
    for i = 1, #want, 8 do
      local a = { x = want[i + 1], y = want[i + 2], z = want[i + 3] }
      if want[i] == 1 then
        drawLine(a, { x = want[i + 4], y = want[i + 5], z = want[i + 6] }, want[i + 7])
      else
        drawNode(a, want[i + 7])
      end
    end
    W.shown = want
  end

  hook(COM, 'DebugDraw', function(orig) return function(...)
    local res = orig(...)
    if __SdbWaypoints == W then
      local ok, err = pcall(W.DrawExtras)
      if not ok and not W.drawWarned then W.drawWarned = true Warn('Waypoints draw: ' .. tostring(err)) end
    end
    return res
  end end)

  -- While a waypoint is held its marker and lines follow the cursor: the
  -- order manager draws every node and line through this.
  W.origGetPos = hook(COM, 'GetPosForOrder', function(orig) return function(o, ...)
    local D = W.drag
    if D and D.active and D.order == o and D.pos and __SdbWaypoints == W then return D.pos end
    return orig(o, ...)
  end end)

  --------------------------------------------------------------- hit test
  -- Where the cursor's ray meets the plane y = h.
  local function ground(sx, sy, h)
    local o, d = Engine.ScreenPointToRay(EngineClasses.float2(sx, sy))
    if not (o and d) or math.abs(d.y) < 1e-6 then return nil end
    local t = (h - o.y) / d.y
    if t <= 0 then return nil end
    return o.x + d.x * t, o.z + d.z * t
  end

  -- Screen distance in pixels from the cursor to world point p: the world
  -- offset mapped back through the screen-to-ground Jacobian at p's height,
  -- so it is right on a tilted camera and on the water surface alike.
  local function pixelDist(p, ms)
    local cx, cz = ground(ms.x, ms.y, p.y)
    local ax, az = ground(ms.x + 8, ms.y, p.y)
    local bx, bz = ground(ms.x, ms.y + 8, p.y)
    if not (cx and ax and bx) then return math.huge end
    local j11, j21 = (ax - cx) / 8, (az - cz) / 8
    local j12, j22 = (bx - cx) / 8, (bz - cz) / 8
    local det = j11 * j22 - j12 * j21
    if math.abs(det) < 1e-9 then return math.huge end
    local dx, dz = p.x - cx, p.z - cz
    local u = (j22 * dx - j12 * dz) / det
    local v = (-j21 * dx + j11 * dz) / det
    return math.sqrt(u * u + v * v)
  end

  local function unitsOf(o)
    local out = {}
    for _, u in ipairs(o.orderState.activeUnitsArray) do table.insert(out, u) end
    for _, u in ipairs(o.orderState.queuedUnitsArray) do table.insert(out, u) end
    return out
  end

  local function seqOf(u)
    local st, s = u.orderState, {}
    if st.activeOrder then table.insert(s, st.activeOrder) end
    for _, q in ipairs(st.queuedOrdersArray) do table.insert(s, q) end
    return s
  end

  -- Orders whose markers are on screen: the selected units' (drawn only while
  -- they have an active order, as DrawQueuedOrdersForUnit does), or all of
  -- yours while Shift shows everything.
  local function shownOrders()
    local list, seen = {}, {}
    local function add(o)
      if o and not seen[o] and not o.isDeletePredicted and DRAGGABLE[o.orderTask] then
        seen[o] = true
        table.insert(list, o)
      end
    end
    local function addUnit(u)
      local st = u.orderState
      if st and st.activeOrder then
        add(st.activeOrder)
        for _, q in ipairs(st.queuedOrdersArray) do add(q) end
      end
    end
    local focus = GetFocusArmy()
    if W.showAll then
      for _, u in pairs(ownUnits()) do addUnit(u) end
    else
      for _, u in pairs(SS.GetSelectedUnits()) do if u.armyId == focus then addUnit(u) end end
    end
    return list
  end

  ------------------------------------------------------------------- plan
  local function footprint(tp, p) return PU.GetPlacementBounds(p, tp.skirtSize) end
  local function overlaps(a, b, c, d, e, f, g, h) return a <= g and e <= c and b <= h and f <= d end

  local function ghostOf(r)
    local g = r and r.ghost and GetUnitById(r.ghost)
    if live(g) then return g end
  end

  -- How to re-issue every unit's queue with `order` moved to newPos (nil:
  -- just check it can be done). Units with the same queue go as one group,
  -- so a shared order stays one order; a building shared across groups is
  -- placed once and the other groups assist it. nil and a reason if any
  -- order in the way cannot be reproduced.
  W.Plan = function(order, newPos, remove)
    local focus = GetFocusArmy()
    local U = unitsOf(order)
    if #U == 0 then return nil, 'no units' end
    local inU = {}
    for _, u in ipairs(U) do
      if u.armyId ~= focus or not live(u) then return nil, 'not all ours' end
      inU[u] = true
    end

    local plan = { groups = {}, stage = false, wait = {}, units = U }
    local specs = {}

    local function specFor(o)
      if specs[o] ~= nil then return specs[o] end
      -- Deleting a waypoint is re-issuing the queue without it.
      if remove and o == order then return 'skip' end
      local task, r, s = o.orderTask, W.rec[o.id], nil
      if task == OT.BUILD then
        local pl = r and r.payload
        -- No acknowledgement yet, or issued before the hook: unknown ghost.
        if not (pl and pl.tpId and r.ghost) then return false end
        local tp = __Templates.Units[pl.tpId]
        if not tp then return false end
        local g = ghostOf(r)
        if o == order then
          -- A started building cannot move.
          if g and (g.progress or 0) > 0 then return false end
          s = { build = o.id, pos = newPos or o.positionFloat3, payload = pl, old = g }
          -- The old ghost stays until the host drops the emptied order, so a
          -- new footprint over it has to wait for that.
          if g and newPos then
            local a, b, c, d = footprint(tp, o.positionFloat3)
            local e, f, gg, h = footprint(tp, newPos)
            if overlaps(a, b, c, d, e, f, gg, h) then
              plan.stage = true
              table.insert(plan.wait, g.id)
            end
          end
        else
          if not g then
            s = 'skip'   -- built, destroyed or gone: nothing left to do there
          elseif g:IsCompleted() then
            s = 'skip'
          else
            local outside = false
            for _, u in ipairs(unitsOf(o)) do if not inU[u] then outside = true end end
            if (g.progress or 0) > 0 or outside then
              -- Survives the re-issue (started, or someone else still has
              -- it): carry on building it, which is what repair does to an
              -- unfinished structure.
              s = { task = OT.REPAIR, pos = g:GetPosition(), payload = { targetGlobalId = g.id } }
            else
              -- Deleted with its order, then placed again on the same spot.
              s = { build = o.id, pos = o.positionFloat3, payload = pl, old = g }
              plan.stage = true
              table.insert(plan.wait, g.id)
            end
          end
        end
      elseif POSITIONAL[task] then
        s = { task = task, pos = (o == order and newPos) or o.positionFloat3, payload = r and r.payload }
      elseif TARGETED[task] then
        -- The host follows a target through an upgrade, so its current target
        -- beats the one it was issued at. Reclaim can target a wreck, which
        -- that lookup cannot see.
        local pl
        if task ~= OT.RECLAIM and live(o.targetUnit) then pl = { targetGlobalId = o.targetUnit.id } end
        pl = pl or (r and r.payload)
        if not pl then return false end
        s = { task = task, pos = copy3(W.origGetPos(o)), payload = pl }
      else
        return false
      end
      specs[o] = s
      return s
    end

    local byKey = {}
    for _, u in ipairs(U) do
      local seq = seqOf(u)
      local ids = {}
      for i, o in ipairs(seq) do ids[i] = o.id end
      local key = table.concat(ids, '|')
      local grp = byKey[key]
      if not grp then
        local list = {}
        for _, o in ipairs(seq) do
          local s = specFor(o)
          if s == false then return nil, 'queue holds an order that cannot be re-issued' end
          if s ~= 'skip' then table.insert(list, s) end
        end
        grp = { units = {}, specs = list, i = 1, issued = 0 }
        byKey[key] = grp
        table.insert(plan.groups, grp)
      end
      table.insert(grp.units, u)
    end
    return plan
  end

  -- A building order as IssueBuildOrderTemp places one: a predicted ghost at
  -- once, swapped for the host's on acknowledgement. A factory ghost's queued
  -- units ride along, as they do from a predicted ghost.
  local function issueBuild(units, append, s, onAck)
    local pl = s.payload
    local tp = __Templates.Units[pl.tpId]
    local pos = EngineClasses.float3(s.pos.x, PU.GetPlacementHeight(tp, s.pos.x, s.pos.z), s.pos.z)
    local lid
    local ok = pcall(function()
      local colour = Armies[pl.armyId]:GetColor()
      colour.w = PlacementGhostIconAlpha
      local _, l = Engine.InstantiatePrefab(tp.general.placementGhostPrefabID, pos, ONE, GetIdentityQuaternion())
      lid = l
      Engine.SetIconColor(lid, 0, colour)
      local pu = PredictedUnit(lid, GetFocusArmy(), tp)
      _G.PredictedUnits[lid.index] = pu
      local old = s.old
      if old and old.predictedBuildQueue and next(old.predictedBuildQueue) then
        pu.predictedBuildQueue = table.deepCopy(old.predictedBuildQueue)
        pu.predictedBuildQueueIdCounter = old.predictedBuildQueueIdCounter or 0
      end
    end)
    if not ok then
      if lid then pcall(Engine.DeletePrefabInstance, lid) end -- lua-check: ok
      lid = nil
    end
    COM.ClientIssueOrder(OT.BUILD, units, append, pos, pl, function(accepted, gid)
      if lid then
        local ok2 = pcall(IEF.OnBuildOrderAcknowledged, accepted, lid, gid)
        local pu = not ok2 and _G.PredictedUnits[lid.index]
        if pu then pcall(pu.Delete, pu) end -- lua-check: ok
      end
      onAck(accepted, gid)
    end)
  end

  -- The order manager only re-predicts on its next sim tick, so until then
  -- it would draw the old queue: the waypoint flicks back to where it was
  -- before jumping to where it was dropped. Predict and draw straight away.
  local function showNow()
    pcall(COM.RecalculatePendingMessages) -- lua-check: ok
    pcall(COM.DebugDraw) -- lua-check: ok
  end

  W.Start = function(plan)
    plan.t0 = _G.Tick or 0
    plan.builds = {}
    if plan.stage then
      COM.ClientClearOrder(plan.units)
      plan.phase = 'wait'
    else
      plan.phase = 'issue'
    end
    W.job = plan
    W.Step()
    showNow()
  end

  -- Runs every frame while a re-issue is under way. In sim ticks, so a pause
  -- holds it rather than timing it out.
  W.Step = function()
    local J = W.job
    if not J then return end
    local age = (_G.Tick or 0) - J.t0
    if age > 150 then W.job = nil return end
    if J.phase == 'wait' then
      -- Until the host has dropped the old placement ghosts, a building put
      -- back on the same spot would be refused as blocked.
      for _, id in ipairs(J.wait) do
        if live(GetUnitById(id)) and age < 30 then return end
      end
      J.phase = 'issue'
    end
    local waiting, before = false, 0
    for _, grp in ipairs(J.groups) do before = before + grp.issued + (grp.cleared and 1 or 0) end
    for _, grp in ipairs(J.groups) do
      while grp.i <= #grp.specs do
        local s = grp.specs[grp.i]
        local append = grp.issued > 0
        if s.build then
          local b = J.builds[s.build]
          if not b then
            b = { state = 'pending' }
            J.builds[s.build] = b
            issueBuild(grp.units, append, s, function(accepted, gid)
              b.state = (accepted and gid) and 'ok' or 'fail'
              b.ghost = gid
            end)
            grp.issued = grp.issued + 1
          elseif b.state == 'pending' then
            waiting = true
            break
          elseif b.state == 'ok' then
            COM.ClientIssueOrder(OT.REPAIR, grp.units, append, s.pos, { targetGlobalId = b.ghost })
            grp.issued = grp.issued + 1
          end
        else
          COM.ClientIssueOrder(s.task, grp.units, append, s.pos, s.payload)
          grp.issued = grp.issued + 1
        end
        grp.i = grp.i + 1
      end
      -- Nothing survived: the move still replaces what they had.
      if grp.i > #grp.specs and grp.issued == 0 and not J.stage and not grp.cleared then
        grp.cleared = true
        COM.ClientClearOrder(grp.units)
      end
    end
    if not waiting then W.job = nil end
    local after = 0
    for _, grp in ipairs(J.groups) do after = after + grp.issued + (grp.cleared and 1 or 0) end
    if after > before then showNow() end
  end

  -------------------------------------------------------------------- drag
  local function clearPreview()
    if W.preview then
      pcall(Engine.SetRangeRingsEnabled, W.preview, false) -- lua-check: ok
      pcall(Engine.DeletePrefabInstance, W.preview) -- lua-check: ok
      W.preview = nil
    end
  end

  -- Nearest free deposit, as extractor placement snaps to one.
  local function nearestSpot(p, tp, radius)
    local best, bestD = nil, radius
    local domain = PU.GetPlacementDomain(tp)
    for _, spot in pairs(RS.resourceSpots) do
      local q = spot:GetPosition()
      q.x = math.truncateToInt(q.x)
      q.z = math.truncateToInt(q.z)
      local d = math.sqrt((p.x - q.x) ^ 2 + (p.z - q.z) ^ 2)
      if d < bestD then best, bestD = q, d end
    end
    return best
  end

  -- UpdateConstructionPreview's cell test, except that the building's own
  -- old footprint counts as free: it goes when the order is re-issued.
  local function buildCells(tp, p, oldPos)
    local skirt = tp.skirtSize
    local a, b, c, d = PU.GetPlacementBounds(p, skirt)
    local size = GameInfo.MapInfo.size
    if a < 0 or b < 0 or c > size[1] or d > size[2] then return false, nil end
    local oa, ob, oc, od = PU.GetPlacementBounds(oldPos, skirt)
    local domain = PU.GetPlacementDomain(tp)
    local alloy = Tags.ALLOYS_EXTRACTION[tp.general.tpId]
    local cells, all = {}, true
    for x = 1, skirt.x do
      cells[x] = {}
      for y = 1, skirt.y do
        local cx, cy = a + x - 1, b + y - 1
        local res = {}
        local at = EngineClasses.int2(cx, cy)
        Engine.GetLocalGridCellFlags(_G.localBuildingPlacementGridID, at, at, res)
        local flags = res[1] and res[1].flags or 0
        local ok = PU.IsCellValidForPlacement(flags, domain, alloy) and true or false
        if ok and bit.band(flags, PL.Default) ~= 0 then
          ok = cx >= oa and cx <= oc and cy >= ob and cy <= od
        end
        cells[x][y] = ok
        if not ok then all = false end
      end
    end
    return all, cells
  end

  local function drawPreview(tp, p, cells, all)
    local _, id = Engine.InstantiatePrefab(tp.general.placementGhostPrefabID, p, ONE, GetIdentityQuaternion())
    W.preview = id
    Engine.SetIconsEnabled(id, false)
    Engine.SetRangeRingsEnabled(id, true)
    for x = 1, tp.skirtSize.x do
      for y = 1, tp.skirtSize.y do
        local ok, bone = cells[x][y], {}
        Engine.GetLocalBone(id, 'Grid' .. x .. ' ' .. y .. 'On', bone)
        Engine.SetRendererEnabled(bone, ok and all)
        Engine.GetLocalBone(id, 'Grid' .. x .. ' ' .. y .. 'Off', bone)
        Engine.SetRendererEnabled(bone, not ok)
        Engine.GetLocalBone(id, 'Grid' .. x .. ' ' .. y .. 'Neutral', bone)
        Engine.SetRendererEnabled(bone, ok and not all)
      end
    end
  end

  W.Cancel = function()
    W.drag = nil
    clearPreview()
    pcall(COM.DebugDraw) -- lua-check: ok
  end

  -- A left press on a waypoint marker picks it up instead of starting a
  -- selection box. It only becomes a drag once the mouse moves; a press let
  -- go where it landed is replayed as the click it would have been.
  W.TryBegin = function()
    if not W.dragOn or W.job or W.drag then return false end
    if IsObserver() or BM.GetBuildMode() then return false end
    if not MC.IsCursorInGameView() or Engine.IsMouseOverUI() then return false end
    local ms = MC.GetMouseScreenPosition()
    local best, bestD = nil, W.grab
    for _, o in ipairs(shownOrders()) do
      local d = pixelDist(MU.SnapToWaterVisualMarkerHeight(copy3(W.origGetPos(o))), ms)
      if d < bestD then best, bestD = { order = o }, d end
    end
    if W.rallyOn then
      for _, f in ipairs(shownFactories()) do
        local p = rallyOf(f)
        if p then
          local d = pixelDist(MU.SnapToWaterVisualMarkerHeight(copy3(p)), ms)
          if d < bestD then best, bestD = { rally = f }, d end
        end
      end
    end
    if not best then return false end
    local D = { sx = ms.x, sy = ms.y, active = false }
    if best.order then
      local o = best.order
      if not W.Plan(o, nil) then return false end
      D.order = o
      if o.orderTask == OT.BUILD then
        D.tp = __Templates.Units[W.rec[o.id].payload.tpId]
        D.alloy = Tags.ALLOYS_EXTRACTION[D.tp.general.tpId]
      end
    else
      D.rally = best.rally
    end
    W.drag = D
    return true
  end

  -- Follows the cursor with the held waypoint, every frame.
  W.Track = function()
    local D = W.drag
    if not D then return end
    local ms = MC.GetMouseScreenPosition()
    if not D.active then
      local dx, dy = ms.x - D.sx, ms.y - D.sy
      if dx * dx + dy * dy < 36 then return end
      D.active = true
    end
    if D.order and (D.order.isDeletePredicted or #unitsOf(D.order) == 0) then return W.Cancel() end
    if D.rally and not live(D.rally) then return W.Cancel() end
    local m = MC.GetMousePosition()
    if D.tp then
      local p = { x = math.truncateToInt(m.x), z = math.truncateToInt(m.z) }
      if D.alloy then
        local spot = nearestSpot(p, D.tp, math.max(8, W.grab))
        if spot then p = { x = spot.x, z = spot.z } end
      end
      p.y = PU.GetPlacementHeight(D.tp, p.x, p.z)
      clearPreview()
      local all, cells = buildCells(D.tp, p, D.order.positionFloat3)
      D.pos, D.valid = p, all
      if cells then drawPreview(D.tp, p, cells, all) end
    else
      D.pos = copy3(m)
      D.valid = MU.IsPositionOnMap(D.pos)
    end
    COM.DebugDraw()
  end

  local function sameId(a, b)
    return a and b and a.index == b.index and a.version == b.version
  end

  -- Selects the units sharing a waypoint; Shift adds them, as it does to a
  -- click selection.
  local function selectAll(units)
    local out = {}
    if IS.IsRawKeyPressed('Shift') then
      for k, e in pairs(SS.GetSelectedEntities() or {}) do out[k] = e end
    end
    for _, u in ipairs(units) do
      if live(u) and u.localId then out[u.localId.index] = u end
    end
    SS.SetSelectedEntities(out)
  end

  -- A press on a marker let go where it landed. Ctrl deletes the waypoint;
  -- two in quick succession select its units. Over ground the click is
  -- otherwise swallowed: an ordinary click there empties the selection, and
  -- with it the markers, so a double click could never land. Over a unit
  -- (other than the marker's own building) it is the ordinary click.
  -- False hands the release back to the game.
  local function markerClick(D)
    if IS.IsRawKeyPressed('Ctrl') then
      if not D.order then return false end
      local plan, why = W.Plan(D.order, nil, true)
      if plan then
        W.Start(plan)
        __SdbWaypointsCount = __SdbWaypointsCount + 1
      else
        Warn('Waypoints: could not delete that waypoint (' .. tostring(why) .. ')')
      end
      return true
    end
    local hover = IEF.GetHoverUnit()
    if hover then
      local own = D.order and D.order.orderTask == OT.BUILD and W.rec[D.order.id]
        and sameId(hover.id, W.rec[D.order.id].ghost)
      if not own then return false end
    end
    local key = D.order or D.rally
    local now = Engine.GetCurrentRealtimeInSeconds()
    local last = W.lastClick
    if last and last.key == key and now - last.t < 0.35 then
      W.lastClick = nil
      selectAll(D.order and unitsOf(D.order) or { D.rally })
    else
      W.lastClick = { key = key, t = now }
    end
    return true
  end

  W.Drop = function()
    local D = W.drag
    W.drag = nil
    clearPreview()
    if not D.active then return markerClick(D) end
    if D.valid and D.pos then
      if D.rally then
        BQ.RequestRallyPoint.Send({ D.rally }, EngineClasses.float3(D.pos.x, D.pos.y, D.pos.z))
        __SdbWaypointsCount = __SdbWaypointsCount + 1
      elseif not (D.tp and D.pos.x == D.order.positionFloat3.x and D.pos.z == D.order.positionFloat3.z) then
        local plan, why = W.Plan(D.order, D.pos)
        if plan then
          W.Start(plan)
          __SdbWaypointsCount = __SdbWaypointsCount + 1
        else
          Warn('Waypoints: could not move that waypoint (' .. tostring(why) .. ')')
        end
      end
    end
    pcall(COM.DebugDraw) -- lua-check: ok
    return true
  end

  local function guarded(name, f, ...)
    local ok, res = pcall(f, ...)
    if not ok then
      W.drag = nil
      clearPreview()
      Warn('Waypoints ' .. name .. ': ' .. tostring(res))
      return false
    end
    return res
  end

  W.origDown = hook(IEF, 'OnMouseLeftDown', function(orig) return function(...)
    if __SdbWaypoints == W and guarded('press', W.TryBegin) then return end
    return orig(...)
  end end)

  W.origUp = hook(IEF, 'OnMouseLeftUp', function(orig) return function(...)
    if __SdbWaypoints == W and W.drag then
      if guarded('drop', W.Drop) then return end
      -- Handed back: the click the press would have been.
      W.origDown()
    end
    return orig(...)
  end end)

  -- A right click while holding a waypoint drops it back where it was, and
  -- is not also taken as an order.
  hook(IEF, 'OnMouseRightDown', function(orig) return function(...)
    if __SdbWaypoints == W and W.drag then
      W.eatRightUp = true
      W.Cancel()
      return
    end
    return orig(...)
  end end)
  hook(IEF, 'OnMouseRightUp', function(orig) return function(...)
    if __SdbWaypoints == W and W.eatRightUp then
      W.eatRightUp = false
      return
    end
    return orig(...)
  end end)

  -- Per frame, after the game's own previews.
  hook(DFP, 'UpdateDragFormationPreview', function(orig) return function(...)
    local res = orig(...)
    if __SdbWaypoints == W then
      guarded('step', W.Step)
      guarded('drag', W.Track)
    end
    return res
  end end)

  W.Remove = function()
    W.drag = nil
    W.job = nil
    clearPreview()
    clearDrawn()
    unhook()
    __SdbWaypoints = nil
    __SdbWaypointsCount = nil
  end

  end)
  if not installed then
    unhook()
    __SdbWaypointsCount = nil
    error('Waypoints install: ' .. tostring(installError))
  end
  __SdbWaypoints = W
end";
    }
}
