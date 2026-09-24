using System;
using BepInEx;
using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The post-game screen: every army's economy and units over the match,
    // as a table and a set of charts, opened when the game's own victory or
    // defeat panel comes up.
    //
    // Nothing is shown while the match is being played. The host sends
    // every army's economy and every unit's lifecycle to every client (see
    // the netcode notes in the README) and the client throws away what
    // isn't its own; the Lua side here keeps it instead, in the client VM,
    // and this side only reads it out. The screen opens once the focused
    // army's result is in, which is the point where the game itself stops
    // hiding anything.
    //
    // Client-side only: the hooks are table-field wraps in the client VM
    // (no file touched, so the lobby's Lua hash is unchanged) and nothing
    // is sent anywhere.
    [BepInPlugin("com.sanctuarydb.matchstats", "Match Stats", "0.1.0")]
    public class MatchStatsPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _cfgAutoOpen;
        private ConfigEntry<KeyCode> _cfgToggleKey;

        private MatchData _data;
        private bool _hooked;
        private float _installAccum, _pullAccum, _bridgeAccum = 1f;
        private string _lastErr;
        private bool _resultSeen;
        private int _endTick = -1;
        private StatsScreen _screen;

        private void Awake()
        {
            _log ??= Logger;
            _cfgAutoOpen = Config.Bind("Screen", "AutoOpen", true,
                "Open the stats as soon as the game's victory or defeat panel comes up. Off leaves just the MATCH STATS button under it.");
            _cfgToggleKey = Config.Bind("Screen", "ToggleKey", KeyCode.F3,
                "Key that opens and closes the stats once the match has a result (never before).");
            Logger.LogInfo("Match Stats loaded: the stats open with the game's result screen.");
        }

        private void OnDestroy()
        {
            _screen?.Destroy();
            HudCanvas.Destroy();
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (_lastErr != e.Message)
                {
                    _lastErr = e.Message;
                    Logger.LogWarning($"Match stats: {e}");
                }
            }
        }

        private void Tick()
        {
            // The resolve walks every assembly's types, so a failed one is not
            // retried every frame.
            _bridgeAccum += Time.unscaledDeltaTime;
            if (_bridgeAccum >= 1f)
            {
                _bridgeAccum = 0f;
                EnsureLuaBridge();
            }
            if (!LuaReady)
            {
                // Back in the menu: the scene took the screen with it.
                if (_data != null) Forget();
                return;
            }

            // Hooked as early as the client VM allows, so the whole match is
            // seen; a quarter-second retry costs nothing once it has taken.
            if (!_hooked)
            {
                _installAccum += Time.unscaledDeltaTime;
                if (_installAccum < 0.25f) return;
                _installAccum = 0f;
                if (!RunLua(InstallChunk)) return;
                _hooked = GetLuaGlobal("__SdbStatsHook") == "true";
                ReportLuaError();
                if (!_hooked) return;
                Logger.LogInfo("Match stats: hooks installed.");
            }

            var result = ResultPanel();
            var resultShowing = result != null && result.IsVisible;

            _pullAccum += Time.unscaledDeltaTime;
            // Once the result is in, a pull is also what gives the screen
            // its last figures before it opens.
            if (_pullAccum >= 2f || (resultShowing && !_resultSeen)) Pull();

            if (resultShowing && !_resultSeen && _data != null)
            {
                _resultSeen = true;
                _endTick = _data.Tick;
                Logger.LogInfo($"Match stats: result screen up at tick {_endTick}; {_data.Players().Count} armies.");
                DumpResultPanel(result);
                if (_cfgAutoOpen.Value) Screen(result)?.Open(_data, _endTick);
            }

            if (!_resultSeen) return;
            var screen = Screen(result);
            if (screen == null) return;
            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                if (screen.IsOpen) screen.Close();
                else screen.Open(_data, _endTick);
            }
            screen.Update(_data, resultShowing ? result : null);
        }

        private StatsScreen Screen(SanctuaryPanelUI beside)
        {
            if (_screen != null && _screen.Alive) return _screen;
            var root = beside != null ? HudCanvas.Ensure(beside) : HudCanvas.Ensure();
            if (root == null) return null;
            _screen?.Destroy();
            _screen = new StatsScreen(root);
            return _screen;
        }

        private void Forget()
        {
            _screen?.Destroy();
            _screen = null;
            _data = null;
            _hooked = false;
            _resultSeen = false;
            _endTick = -1;
        }

        private void Pull()
        {
            _pullAccum = 0f;
            var cursor = _data?.Cursor ?? 0;
            if (!RunLua($"__SdbStatsOut = __SdbStatsPull and __SdbStatsPull({cursor}) or ''")) return;
            var raw = GetLuaGlobal("__SdbStatsOut");
            ReportLuaError();
            var session = MatchData.SessionOf(raw);
            if (session == null) return;
            if (_data == null || _data.Session != session)
            {
                // A new VM is a new match. The cursor was for the old one's
                // lines, so read this one's from the start.
                _screen?.Destroy();
                _screen = null;
                _resultSeen = false;
                _endTick = -1;
                _data = new MatchData();
                if (cursor != 0)
                {
                    RunLua("__SdbStatsOut = __SdbStatsPull(0)");
                    raw = GetLuaGlobal("__SdbStatsOut") ?? "";
                }
            }
            _data.Apply(raw);
        }

        private void ReportLuaError()
        {
            var err = GetLuaGlobal("__SdbStatsErr");
            if (!string.IsNullOrEmpty(err) && err != _lastErr)
            {
                _lastErr = err;
                Logger.LogWarning($"Match stats: Lua reports: {err}");
            }
        }

        private static SanctuaryPanelUI ResultPanel()
        {
            try
            {
                var ui = SanctuaryUIManager.Instance;
                return ui != null && ui.TryGetPanel(UIPanelType.GameResult, out var panel) ? panel : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool _dumped;

        // What the result panel is made of, once, for anyone laying anything
        // else out against it.
        private void DumpResultPanel(SanctuaryPanelUI panel)
        {
            if (_dumped || panel == null) return;
            _dumped = true;
            var sb = new System.Text.StringBuilder("Match stats: result panel tree:\n");
            void Walk(Transform t, int depth)
            {
                var rt = t as RectTransform;
                sb.Append(' ', depth * 2).Append(t.name)
                  .Append(t.gameObject.activeSelf ? "" : " (inactive)")
                  .Append(rt != null ? $" {rt.rect.width:0}x{rt.rect.height:0}" : "");
                foreach (var c in t.GetComponents<Component>())
                    if (!(c is Transform)) sb.Append(' ').Append(c.GetType().Name);
                sb.Append('\n');
                if (depth < 6) for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
            }
            Walk(panel.transform, 0);
            Logger.LogInfo(sb.ToString());
        }

        // ---- Lua side --------------------------------------------------------
        //
        // Installed once per client VM, guarded by a global; each match
        // builds a fresh VM, so the hooks and everything they gathered go
        // with the old one. Every wrapper swaps a command's Receive field,
        // which the registry looks up at call time (the ReplayManager's
        // technique), and the game's own handling always runs.
        //
        // - UpdateEconomyTotals arrives every tick for every army. Income
        //   and outcome are per tick (economy.lua adds them straight into the
        //   store), so a second's figures are ten ticks summed. Income
        //   already includes reclaim (`income = generation + harvest`).
        //   Waste is income above spend while the store sits at its cap;
        //   a stall is a tick where the host throttled spending below what
        //   was asked for.
        // - CreateUnit / SetUnitProgress: a unit counts as built when it
        //   finishes, which also counts an upgrade (the new tier is a new
        //   entity). Placement ghosts never finish, so never count. The
        //   commander is not "built".
        // - DestroyUnit is a death (killed, dashed through, self-destructed);
        //   a Delete type or DeleteUnit alone is a removal — an upgrade's
        //   old tier, a cancelled site — and takes the unit off the army
        //   value without counting as a loss.
        // - WinConditionUpdate gives each army's result and when.
        //
        // Army value is the alloy cost of the finished mobile units alive,
        // commander aside; structures count in "built" but not in value.
        // Lost value leaves the commander out too.
        //
        // It waits for the host's armies to be registered, so nothing here
        // is the first to Import a module while the client is still being
        // set up; the sweep below picks up any unit that came before it.
        private const string InstallChunk = @"
if not __SdbStatsHook and type(Armies) == 'table' and next(Armies) ~= nil and __Entities and __Entities.Units then
  __SdbStatsErr = ''
  local ok, err = pcall(function()
    local TR = (Constants and Constants.TickRate) or 10
    local Units = __Entities.Units
    local UD = Import('common/commands/definitions/units.lua')
    local E = Import('common/commands/definitions/economy.lua')
    local W = Import('client/winCondition.lua')
    local Lobby = Import('common/lobby.lua')
    if not (UD.CreateUnit and UD.SetUnitProgress and UD.DestroyUnit and UD.DeleteUnit and E.UpdateEconomyTotals and W.WinConditionUpdate) then
      error('a command to hook is missing')
    end

    local S = { armies = {}, units = {}, lines = {}, cond = {} }
    S.session = tostring(S)
    local function tick()
      local ok, t = pcall(Engine.GetSimulationTick)
      return ok and t or 0
    end

    local function A(id)
      local a = S.armies[id]
      if not a then
        a = { n = 0, ta = 0, te = 0, sa = 0, se = 0, wa = 0, we = 0, xa = 0, xe = 0, pa = 0, pe = 0, ms = 0,
              wn = 0, wia = 0, wie = 0, woa = 0, woe = 0,
              bl = 0, ba = 0, bn = 0, be = 0, bs = 0, bv = 0, lm = 0, ls = 0, lc = 0, lv = 0,
              av = 0, au = 0, pav = 0, pau = 0, le = 0, da = 0, de = 0, ck = 0 }
        S.armies[id] = a
      end
      return a
    end

    local function cat(tpId)
      if Tags.COMMAND[tpId] then return 'c' end
      if Tags.STRUCTURE[tpId] then return 's' end
      if Tags.ENGINEER[tpId] then return 'e' end
      if Tags.AIR[tpId] then return 'a' end
      if Tags.NAVAL[tpId] then return 'n' end
      return 'l'
    end

    local function reg(idx, armyId)
      local u = Units[idx]
      if not u or not u.tp then return nil end
      local cost = (u.tp.economy and u.tp.economy.cost) or {}
      local r = { army = armyId or u.armyId, cat = cat(u.tpId), va = cost.alloys or 0, ve = cost.energy or 0, done = false, dead = false }
      S.units[idx] = r
      return r, u
    end

    local function complete(r, counts)
      r.done = true
      local a = A(r.army)
      if counts and r.cat ~= 'c' then
        local k = 'b' .. r.cat
        a[k] = a[k] + 1
        a.bv = a.bv + r.va
      end
      if r.cat ~= 's' and r.cat ~= 'c' then
        a.av = a.av + r.va
        a.au = a.au + 1
        if a.av > a.pav then a.pav = a.av end
        if a.au > a.pau then a.pau = a.au end
      end
    end

    local function gone(r, killed)
      if not r.done then return end
      r.done = false
      local a = A(r.army)
      if r.cat ~= 's' and r.cat ~= 'c' then
        a.av = a.av - r.va
        a.au = a.au - 1
      end
      if not killed then return end
      -- The commander counts as a loss, but its cost would swamp the
      -- value of everything else lost.
      if r.cat == 's' then a.ls = a.ls + 1 elseif r.cat == 'c' then a.lc = a.lc + 1 else a.lm = a.lm + 1 end
      if r.cat ~= 'c' then
        a.lv = a.lv + r.va
        a.le = a.le + r.ve
      end
      -- Nothing says who made the kill, so it is shared among the loser's
      -- enemies that are playing: exact in a 1v1, even across a team.
      local loser = Armies[r.army]
      local allies = loser and loser.allyIDs or { [r.army] = true }
      local enemies = {}
      for id, army in pairs(Armies) do
        local e = S.armies[id]
        if not allies[id] and not army.civilian and e and e.ms > 0 then enemies[#enemies + 1] = e end
      end
      local share = 1 / math.max(1, #enemies)
      for _, e in ipairs(enemies) do
        if r.cat == 'c' then
          e.ck = e.ck + share
        else
          e.da = e.da + r.va * share
          e.de = e.de + r.ve * share
        end
      end
    end

    -- FAF's score (lua/sim/score.lua CalculateBrainScore), with alloy for
    -- mass: half of what was spent, plus half the battle's net value (never
    -- below nothing), plus 5000 a commander kill. Energy is brought to
    -- alloy at 10:1, the ratio Sanctuary's unit costs are set at, where FA
    -- uses 20:1. FAF takes a commander's own value back out of a kill;
    -- here it is never counted in, lost or destroyed.
    local EC = 10
    local function score(a)
      local spent = (a.sa + a.se / EC) / 2
      local battle = math.max(0, ((a.da - a.lv) + (a.de - a.le) / EC) / 2)
      return math.floor(spent + battle + a.ck * 5000)
    end

    local function eco(data)
      local t = data.totals
      if not t then return end
      local al, en = t.alloys or {}, t.energy or {}
      local a = A(data.armyId)
      local ia, ie = al.income or 0, en.income or 0
      local oa, oe = al.outcome or 0, en.outcome or 0
      local sta, ste = al.storage or 0, en.storage or 0
      a.n = a.n + 1
      a.ta, a.te, a.sa, a.se = a.ta + ia, a.te + ie, a.sa + oa, a.se + oe
      if sta > a.ms then a.ms = sta end
      if sta > 0 and (al.current or 0) >= sta - 0.01 and ia > oa then a.wa = a.wa + ia - oa end
      if ste > 0 and (en.current or 0) >= ste - 0.01 and ie > oe then a.we = a.we + ie - oe end
      local ra, re = al.request or 0, en.request or 0
      if ra > 0 and oa < ra * 0.99 then a.xa = a.xa + 1 end
      if re > 0 and oe < re * 0.99 then a.xe = a.xe + 1 end
      a.wn = a.wn + 1
      a.wia, a.wie, a.woa, a.woe = a.wia + ia, a.wie + ie, a.woa + oa, a.woe + oe
      if a.wn >= TR then
        local k = TR / a.wn
        local ria, rie = a.wia * k, a.wie * k
        if ria > a.pa then a.pa = ria end
        if rie > a.pe then a.pe = rie end
        S.lines[#S.lines + 1] = string.format('S|%d|%d|%.2f|%.2f|%.2f|%.2f|%.0f|%.0f|%.0f|%d|%d',
          data.armyId, tick(), ria, rie, a.woa * k, a.woe * k, al.current or 0, en.current or 0, a.av, a.au, score(a))
        a.wn, a.wia, a.wie, a.woa, a.woe = 0, 0, 0, 0, 0
      end
    end

    -- Whatever already exists when this goes in (a hot reload mid-match,
    -- the commander) is tracked from here, but not counted as built.
    for idx, u in pairs(Units) do
      local r = reg(idx, u.armyId)
      if r and u:IsCompleted() then complete(r, false) end
    end

    local cu = UD.CreateUnit.Receive
    UD.CreateUnit.Receive = function(data, ...)
      local res = cu(data, ...)
      local ok2, e2 = pcall(function()
        local r, u = reg(data.unitID.index, data.armyID)
        if r and u:IsCompleted() then complete(r, true) end
      end)
      if not ok2 then __SdbStatsErr = 'create: ' .. tostring(e2) end
      return res
    end

    local sp = UD.SetUnitProgress.Receive
    UD.SetUnitProgress.Receive = function(data, ...)
      local res = sp(data, ...)
      local idx = data.unitID.index
      local r = S.units[idx]
      if not (r and (r.done or r.dead)) then
        local u = Units[idx]
        if u and u.IsCompleted and u:IsCompleted() then
          local ok2, e2 = pcall(function()
            r = r or reg(idx, u.armyId)
            if r then complete(r, true) end
          end)
          if not ok2 then __SdbStatsErr = 'progress: ' .. tostring(e2) end
        end
      end
      return res
    end

    local du = UD.DestroyUnit.Receive
    UD.DestroyUnit.Receive = function(data, ...)
      local r = S.units[data.unitID.index]
      if r and not r.dead then
        r.dead = true
        pcall(gone, r, data.destroyType ~= ((DestroyType and DestroyType.Delete) or 1))
      end
      return du(data, ...)
    end

    local xu = UD.DeleteUnit.Receive
    UD.DeleteUnit.Receive = function(data, ...)
      local idx = data.unitID.index
      local r = S.units[idx]
      if r then
        if not r.dead then pcall(gone, r, false) end
        S.units[idx] = nil
      end
      return xu(data, ...)
    end

    local et = E.UpdateEconomyTotals.Receive
    E.UpdateEconomyTotals.Receive = function(data, ...)
      local ok2, e2 = pcall(eco, data)
      if not ok2 then __SdbStatsErr = 'eco: ' .. tostring(e2) end
      return et(data, ...)
    end

    local wc = W.WinConditionUpdate
    W.WinConditionUpdate = function(data, ...)
      pcall(function() S.cond[data.armyID] = { data.condition, tick() } end)
      return wc(data, ...)
    end

    local function clean(s)
      return (tostring(s or ''):gsub('[|\r\n]', ' '))
    end

    __SdbStatsPull = function(from)
      local out = {}
      local map = GameInfo and GameInfo.MapInfo and GameInfo.MapInfo.name or ''
      out[#out + 1] = string.format('V|%s|%d|%d|%s|%s', S.session, tick(), TR, tostring(GetFocusArmy()), clean(map))
      for id, army in pairs(Armies or {}) do
        if not army.civilian then
          local a = A(id)
          -- Only humans are put in ArmyToPlayer (script.lua InitLobby).
          local p = Lobby.ArmyToPlayer and Lobby.ArmyToPlayer[id]
          local human = p ~= nil
          local team = id
          for aid, v in pairs(army.allyIDs or {}) do
            if v and aid < team then team = aid end
          end
          local c = army.color or {}
          local cd = S.cond[id]
          out[#out + 1] = string.format('P|%d|%s|%d|%d|%d|%.3f|%.3f|%.3f|%d|%d',
            id, clean(p and p.nickname), army.factionId or 0, human and 1 or 0, team,
            c.x or 0.5, c.y or 0.5, c.z or 0.5, cd and cd[1] or 0, cd and cd[2] or 0)
          out[#out + 1] = string.format('T|%d|%d|%.1f|%.1f|%.1f|%.1f|%.1f|%.1f|%d|%d|%.2f|%.2f|%.0f|%d|%d|%d|%d|%d|%.0f|%d|%d|%d|%.0f|%.0f|%d|%d|%.0f|%.2f',
            id, a.n, a.ta, a.te, a.sa, a.se, a.wa, a.we, a.xa, a.xe, a.pa, a.pe, a.ms,
            a.bl, a.ba, a.bn, a.be, a.bs, a.bv, a.lm, a.ls, a.lc, a.lv, a.pav, a.pau,
            score(a), a.da, a.ck)
        end
      end
      for i = (from or 0) + 1, #S.lines do out[#out + 1] = S.lines[i] end
      out[#out + 1] = 'C|' .. #S.lines
      return table.concat(out, '\n')
    end

    __SdbStatsHook = true
  end)
  if not ok then __SdbStatsErr = 'install: ' .. tostring(err) end
end
__SdbStatsHook = __SdbStatsHook and 'true' or nil
";
    }
}
