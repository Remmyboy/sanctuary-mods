using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using EM.DOTS.Engine.Loader;
using EM.Network;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.Replays
{
    // Makes the game's own replays watchable properly: any player's point of
    // view or every army at once, the fog lifted, every army's economy with
    // whole-game totals, and a transport with pause, speed, forward seek and
    // restart.
    //
    // The game records every match to a `.sanreplay` and plays it back from
    // the main menu's replay list; this panel appears whenever one is
    // playing. Driving the playback lives in ReplayPlayer; this class is the
    // config, the hotkey, the runtime Lua hooks (economy for every army, the
    // lobby roster for names, observer mode) and the panel.
    [BepInPlugin("com.sanctuarydb.replaymanager", "Replay Manager", "0.5.0")]
    public class ReplaysPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<KeyCode> _cfgKey;
        private ConfigEntry<bool> _cfgTimeline;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;
        private ConfigEntry<float> _cfgScale;
        private ConfigEntry<bool> _cfgLocked;

        private bool _controlsOpen = true;

        // Panel size. The table is laid out at fixed widths, so resizing is a
        // zoom: the corner grip scales the whole panel, and the size is saved
        // when the drag ends (BepInEx writes the file on every set).
        private const float MinScale = 0.6f, MaxScale = 2.5f;
        private bool _fogOverlay;
        private int _lastFocus = int.MinValue;

        // Lua-side state, polled.
        private float _luaAccum;
        // The last texts parsed, so an unchanged poll is not parsed again.
        private string _lastArmiesRaw, _lastEcoRaw;
        private List<ArmyRow> _armies = new List<ArmyRow>();
        private Dictionary<int, EcoRow> _eco = new Dictionary<int, EcoRow>();
        private Dictionary<int, string> _seatNames = new Dictionary<int, string>();   // armyId -> nickname
        // Armies that have shown an economy at all. A 2v2 map played 1v1 still
        // reports its empty slots (and the neutral army), as rows of nothing;
        // they would be dead lines in the table. Sticky, so a player who is
        // wiped out late keeps their row.
        private HashSet<int> _played = new HashSet<int>();
        private int _focus = int.MinValue;
        private string _lastHookErr;

        // Seek bar: the knob sets a target while dragged; it is applied when
        // the mouse comes up, so a drag doesn't fire a restart per pixel.
        private float _dragValue;
        // View to put back after a rewind rebuilds the client.
        private int _pendingFocus = int.MinValue;

        private sealed class ArmyRow
        {
            public int Id;
            public string Name;
            public Color Colour;
        }

        private sealed class EcoRow
        {
            // In already includes Harvest: the host sets income to generation
            // plus harvest (host/systems/economy.lua), so never add the two.
            public float ACur, AStore, AIn, AHarvest, AOut, AReq;
            public float ECur, EStore, EIn, EHarvest, EOut, EReq;
            public float ATotalIn, ATotalOut, ETotalIn, ETotalOut;   // whole game so far

            // Anything at all: an unoccupied slot reports zeroes forever.
            public bool Playing =>
                AStore > 0 || EStore > 0 || ACur > 0 || ECur > 0 ||
                AIn > 0 || EIn > 0 || AHarvest > 0 || EHarvest > 0 ||
                ATotalIn > 0 || ETotalIn > 0 || ATotalOut > 0 || ETotalOut > 0;
        }

        private void Awake()
        {
            _log ??= Logger;

            _cfgKey = Config.Bind("UI", "ToggleKey", KeyCode.F7,
                "Shows/hides the replay control panel during playback.");
            _cfgTimeline = Config.Bind("UI", "ShowTimeline", true,
                "Show the replay's total length and the seek bar. Off hides both, for watching without knowing when the game ends.");
            _cfgPosX = Config.Bind("UI", "PanelX", 12f, "Playback control panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("UI", "PanelY", 300f, "Playback control panel Y in 1080p-logical pixels.");
            _cfgLocked = Config.Bind("UI", "Locked", false, "Keep the panel where it is: no dragging during playback.");
            _cfgScale = Config.Bind("UI", "PanelScale", 1f,
                "Playback control panel size, 1 being the default. Drag the grip in the panel's bottom corner to change it.");

            try
            {
                _harmony = new Harmony("com.sanctuarydb.replays." + Guid.NewGuid().ToString("N").Substring(0, 8));
                ReplayPlayer.ApplyPatches(_harmony);
                _harmony.Patch(AccessTools.Method(typeof(EngineLoader), nameof(EngineLoader.CleanUpGame)),
                    prefix: new HarmonyMethod(typeof(ReplaysPlugin), nameof(CleanUpPrefix)));
                ReplayPlayer.OnLuaStartup += InstallEarlyHooks;
            }
            catch (Exception e)
            {
                Logger.LogError($"Replays: patching failed, the replay panel is off: {e}");
                _harmony?.UnpatchSelf();
                _harmony = null;
            }

            Logger.LogInfo($"Replay Manager loaded: play a replay from the game's menu; {_cfgKey.Value} shows/hides the panel.");
        }

        private void OnDestroy()
        {
            ReplayPlayer.OnLuaStartup -= InstallEarlyHooks;
            Hook.Remove();
            ReplayPlayer.Stop();
            _harmony?.UnpatchSelf();
            // A hot reload leaves the old assembly loaded; its panel goes.
            HudCanvas.Destroy();
            _panel = null;
        }

        private static void CleanUpPrefix()
        {
            ReplayPlayer.Stop();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_cfgKey.Value) && ReplayPlayer.Active) _controlsOpen = !_controlsOpen;

            ReplayPlayer.Update();

            if (ReplayPlayer.Active)
            {
                PollLua(Time.unscaledDeltaTime);
            }
            else
            {
                // A rewind carries its view over; one that failed must not
                // hand it to the next replay opened from the menu.
                if (!ReplayPlayer.Restarting) _pendingFocus = int.MinValue;
                if (_lastArmiesRaw != null || _armies.Count > 0 || _seatNames.Count > 0)
                {
                    _lastArmiesRaw = null;
                    _lastEcoRaw = null;
                    _tableDirty = true;
                    _armies = new List<ArmyRow>();
                    _eco = new Dictionary<int, EcoRow>();
                    _seatNames = new Dictionary<int, string>();
                    _played = new HashSet<int>();
                    _focus = int.MinValue;
                    _lastFocus = int.MinValue;
                }
            }

            UpdatePanel();
        }

        // ---- Lua side ------------------------------------------------------

        // Installed as soon as the client VM exists, before the first host
        // packet, so the lobby roster (InitClient) is seen; the hook puts it
        // back in a VM that turns up without that (a hot reload mid-replay)
        // and takes it out again when the mod goes.
        //
        // - Every army's economy: the registry calls `command.Receive` through
        //   the command table each time, so swapping the field catches it, and
        //   the payload arrives already decoded. The wrapper only keeps each
        //   army's latest totals and the running sums; the poll turns them
        //   into text, twice a second, rather than every update doing it for
        //   every army (which, at 16x, was most of the hook's cost).
        // - The roster: `ReceiveDataClient` is a plain global (networking.lua
        //   is `require`d), and InitClient's data carries every seat's
        //   nickname, army and client id.
        // - Observer: so clicks can't issue orders into the void.
        // - The game's result panel, once the armies are registered (see
        //   S.Result below), so nothing here is the first to Import its module.
        //
        // The totals and the roster live in globals of their own, so a
        // reinstall in the same VM picks them up rather than starting the
        // whole-game sums again. Each wrapper is put back on Remove when it is
        // still the outermost one, and otherwise (another mod wrapped it
        // since) passes straight through.
        private const string InstallChunk = @"
do
  local S, undo = {}, {}
  local D = __SdbReplayData or { eco = {}, sum = {} }
  __SdbReplayData = D
  __SdbReplayEco = __SdbReplayEco or ''
  __SdbReplayPlayers = __SdbReplayPlayers or ''
  __SdbReplayHookErr = ''
  S.ecoDirty = true
  pcall(function() SetObserver(true) end) -- lua-check: ok
  local ok, err = pcall(function()
    local E = Import('common/commands/definitions/economy.lua')
    local cmd = E.UpdateEconomyTotals
    local orig = cmd.Receive
    local mine = function(data, commandData)
      if not S.off then
        local ok2, err2 = pcall(function()
          D.eco[data.armyId] = data.totals
          local a, e = data.totals.alloys or {}, data.totals.energy or {}
          local s = D.sum[data.armyId] or { ai = 0, ao = 0, ei = 0, eo = 0 }
          s.ai = s.ai + (a.income or 0)
          s.ao = s.ao + (a.outcome or 0)
          s.ei = s.ei + (e.income or 0)
          s.eo = s.eo + (e.outcome or 0)
          D.sum[data.armyId] = s
          S.ecoDirty = true
        end)
        if not ok2 then __SdbReplayHookErr = 'eco: ' .. tostring(err2) end
      end
      return orig(data, commandData)
    end
    cmd.Receive = mine
    undo[#undo + 1] = function() if cmd.Receive == mine then cmd.Receive = orig end end
  end)
  if not ok then __SdbReplayHookErr = 'install eco: ' .. tostring(err) end
  local ok3, err3 = pcall(function()
    local origRecv = _G.ReceiveDataClient
    if type(origRecv) ~= 'function' then error('ReceiveDataClient is not a global') end
    local mine = function(name, data)
      if name == 'InitClient' and not S.off then
        pcall(function() -- lua-check: ok
          local parts = {}
          for _, p in ipairs(data.playersInformation or {}) do
            parts[#parts + 1] = string.format('%s|%s|%s|%s', tostring(p.clientID), tostring(p.nickname), tostring(p.armyID), tostring(p.playerType))
          end
          __SdbReplayPlayers = table.concat(parts, ';')
        end)
      end
      return origRecv(name, data)
    end
    _G.ReceiveDataClient = mine
    undo[#undo + 1] = function() if _G.ReceiveDataClient == mine then _G.ReceiveDataClient = origRecv end end
  end)
  if not ok3 then __SdbReplayHookErr = __SdbReplayHookErr .. ' roster: ' .. tostring(err3) end

  -- The game's result panel. The client shows VICTORY or DEFEAT the first
  -- time the focused army's result comes in, and in the all-armies view
  -- every army counts as focused, so in a replay it came up the moment the
  -- first player was wiped out. A replay has no 'us': hold the panel back
  -- until the match is decided (some army has won, i.e. all its enemies are
  -- out), then show it for whichever view is being watched (GAME OVER in the
  -- all-armies view). The game's handler still runs for every update, with
  -- the panel calls muted, so anything else wrapping it (MatchStats records
  -- each army's result there) sees them all whichever order the wrappers
  -- went on in.
  S.Result = function()
    if S.resultDone or type(Armies) ~= 'table' or next(Armies) == nil then return end
    S.resultDone = true
    local ok4, err4 = pcall(function()
      local W = Import('client/winCondition.lua')
      local inner = W.WinConditionUpdate
      if type(inner) ~= 'function' then error('WinConditionUpdate is missing') end
      local GR = (UIPanelType and UIPanelType.GameResult) or 9
      local cond, shown = {}, false
      local mine = function(data, ...)
        if S.off then return inner(data, ...) end
        pcall(function() cond[data.armyID] = data.condition end) -- lua-check: ok
        local vis, txt = Engine.UI_SetPanelVisibility, Engine.UI_SetGameResultValues
        Engine.UI_SetPanelVisibility = function(t, v) if t ~= GR then return vis(t, v) end end
        Engine.UI_SetGameResultValues = function() end
        local ok2, err2 = pcall(inner, data, ...)
        Engine.UI_SetPanelVisibility, Engine.UI_SetGameResultValues = vis, txt
        if not ok2 then error(err2, 0) end
        if shown then return end
        local over = false
        for _, c in pairs(cond) do if c == 1 then over = true end end
        if not over then return end
        shown = true
        local c = cond[GetFocusArmy()]
        local text = (c == 1 and 'VICTORY!') or (c == 2 and 'DEFEAT!') or 'GAME OVER!'
        local ok3, err3 = pcall(function() txt(EngineClasses.UIGameResultValues(text)) end)
        if not ok3 then __SdbReplayHookErr = 'result text: ' .. tostring(err3) end
        vis(GR, true)
      end
      W.WinConditionUpdate = mine
      undo[#undo + 1] = function() if W.WinConditionUpdate == mine then W.WinConditionUpdate = inner end end
    end)
    if not ok4 then __SdbReplayHookErr = (__SdbReplayHookErr or '') .. ' result: ' .. tostring(err4) end
  end

  -- Twice a second from C#: the armies, the view, and the economy as text
  -- ('id|alloy cur|store|in|harvest|out|request|energy x6|alloy in total|
  -- out total|energy in total|out total;...').
  S.Poll = function()
    S.Result()
    local out = {}
    for id, a in pairs(Armies or {}) do
      if not a.civilian then
        local c = a.color or { x = 0.5, y = 0.5, z = 0.5 }
        out[#out + 1] = string.format('%d|%s|%.3f|%.3f|%.3f', id, tostring(a.name), c.x, c.y, c.z)
      end
    end
    __SdbReplayArmies = table.concat(out, ';')
    __SdbReplayFocus = tostring(GetFocusArmy())
    if not S.ecoDirty then return end
    S.ecoDirty = false
    local parts = {}
    for id, t in pairs(D.eco) do
      local a, e = t.alloys or {}, t.energy or {}
      local s = D.sum[id] or { ai = 0, ao = 0, ei = 0, eo = 0 }
      parts[#parts + 1] = string.format('%d|%.0f|%.0f|%.3f|%.3f|%.3f|%.3f|%.0f|%.0f|%.3f|%.3f|%.3f|%.3f|%.1f|%.1f|%.1f|%.1f',
        id, a.current or 0, a.storage or 0, a.income or 0, a.harvest or 0, a.outcome or 0, a.request or 0,
        e.current or 0, e.storage or 0, e.income or 0, e.harvest or 0, e.outcome or 0, e.request or 0,
        s.ai, s.ao, s.ei, s.eo)
    end
    __SdbReplayEco = table.concat(parts, ';')
  end

  S.Remove = function()
    S.off = true
    for _, f in ipairs(undo) do pcall(f) end -- lua-check: ok
    __SdbReplay = nil
  end
  __SdbReplay = S
end";

        private static readonly LuaHook Hook = new LuaHook("__SdbReplay", "replay hooks", InstallChunk);

        private void InstallEarlyHooks()
        {
            Hook.CheckNow();
            Hook.Tick();
        }

        private void PollLua(float dt)
        {
            // Normally already in from start-up; this is the fallback, and
            // what notices the VM a rewind brings.
            Hook.Tick();

            _luaAccum += dt;
            if (_luaAccum < 0.5f) return;
            _luaAccum = 0f;

            // Errors inside the poll are ignored as they always were: the
            // globals keep their last values and the table holds still.
            if (!Hook.Call("pcall(__SdbReplay.Poll)")) return;

            ParseArmies(GetLuaGlobal("__SdbReplayArmies"));
            ParseEco(GetLuaGlobal("__SdbReplayEco"));
            if (_seatNames.Count == 0) ParseSeats(GetLuaGlobal("__SdbReplayPlayers"));
            var focus = GetLuaGlobal("__SdbReplayFocus");
            if (int.TryParse(focus, out var f)) _focus = f;
            _tableDirty = true;

            // After a rewind the client is brand new and back on the
            // recorder's own army; restore the view that was being watched.
            if (_pendingFocus != int.MinValue && _focus != int.MinValue && _armies.Count > 0)
            {
                if (_pendingFocus != _focus) SetFocus(_pendingFocus);
                _pendingFocus = int.MinValue;
            }

            var err = GetLuaGlobal("__SdbReplayHookErr");
            if (!string.IsNullOrEmpty(err) && err != _lastHookErr)
            {
                _lastHookErr = err;
                Logger.LogWarning($"Replays: Lua hook reports: {err}");
            }

            // The fog overlay follows the focus unless the user has overridden it:
            // no overlay in the all-armies view, the army's own fog otherwise.
            if (_focus != _lastFocus)
            {
                _lastFocus = _focus;
                SetFogOverlay(_focus != -1);
            }
        }

        // "id|name|r|g|b;..." for every army that isn't civilian.
        private void ParseArmies(string raw)
        {
            if (raw == null || raw == _lastArmiesRaw) return;
            _lastArmiesRaw = raw;
            var rows = new List<ArmyRow>();
            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');
                if (f.Length < 2 || !int.TryParse(f[0], out var id)) continue;
                var row = new ArmyRow { Id = id, Name = f[1], Colour = AccentColour };
                if (f.Length >= 5)
                {
                    var inv = CultureInfo.InvariantCulture;
                    float.TryParse(f[2], NumberStyles.Float, inv, out var r);
                    float.TryParse(f[3], NumberStyles.Float, inv, out var g);
                    float.TryParse(f[4], NumberStyles.Float, inv, out var b);
                    // Army colours are picked for unit meshes and can be dark;
                    // lift them so a button in that colour still reads.
                    var lift = Mathf.Max(0.35f, Mathf.Max(r, Mathf.Max(g, b)));
                    row.Colour = new Color(r / lift, g / lift, b / lift, 1f);
                }
                rows.Add(row);
            }
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            _armies = rows;
        }

        private void ParseEco(string raw)
        {
            if (raw == null || raw == _lastEcoRaw) return;
            _lastEcoRaw = raw;
            var inv = CultureInfo.InvariantCulture;
            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');
                if (f.Length < 13 || !int.TryParse(f[0], out var id)) continue;
                float P(int i) => float.TryParse(f[i], NumberStyles.Float, inv, out var v) ? v : 0f;
                var row = new EcoRow
                {
                    ACur = P(1), AStore = P(2), AIn = P(3), AHarvest = P(4), AOut = P(5), AReq = P(6),
                    ECur = P(7), EStore = P(8), EIn = P(9), EHarvest = P(10), EOut = P(11), EReq = P(12),
                };
                if (f.Length >= 17)
                {
                    row.ATotalIn = P(13);
                    row.ATotalOut = P(14);
                    row.ETotalIn = P(15);
                    row.ETotalOut = P(16);
                }
                _eco[id] = row;
                if (row.Playing) _played.Add(id);
            }
        }

        // Empty slots on an oversized map are filtered out of the table. Until
        // the first army has shown an economy nothing is filtered, so the
        // table is never empty while the replay is still opening.
        private bool Playing(ArmyRow a) => _played.Count == 0 || _played.Contains(a.Id);

        // "clientID|nickname|armyID|playerType;..." from the recorded lobby.
        private void ParseSeats(string raw)
        {
            if (string.IsNullOrEmpty(raw) || _seatNames.Count > 0) return;
            var seats = new Dictionary<int, string>();
            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');
                if (f.Length < 3 || !int.TryParse(f[2], out var armyId)) continue;
                if (armyId > 0 && !string.IsNullOrEmpty(f[1]) && !seats.ContainsKey(armyId)) seats[armyId] = f[1];
            }
            if (seats.Count > 0) _seatNames = seats;
        }

        // The client only knows armies by slot ("Army_1"); the recorded
        // lobby, captured from InitClient, says who sat in each.
        private string DisplayName(ArmyRow a) =>
            _seatNames.TryGetValue(a.Id, out var n) ? n : a.Name;

        private void SetFocus(int armyId)
        {
            RunLua($"pcall(function() SetFocusArmy({armyId}) end)");
        }

        private void SetFogOverlay(bool on)
        {
            _fogOverlay = on;
            try { FowRenderer.SetFogOfWarActive(on); }
            catch (Exception e) { Logger.LogWarning($"Replays: fog toggle failed: {e.Message}"); }
        }

        // The only way backwards: playback restarts from the top, which
        // rebuilds the client on the recorder's own army, so the view being
        // watched has to be remembered and put back.
        private void Restart()
        {
            _pendingFocus = _focus;
            ReplayPlayer.SeekTo(0);
        }

        // ---- the panel -----------------------------------------------------
        //
        // On a canvas of the mod's own rather than the game's HUD canvas: a
        // rewind tears the scene down, HUD and all, while the panel says so,
        // and Camera Utilities can switch the game's UI off mid-replay.

        private static readonly Color OutColour = new Color(1f, 0.5f, 0.45f);

        // Column widths in canvas units, twice the 1080-logical pixels. The
        // panel is sized from these plus its row count, so it fits two
        // players or twelve.
        private const float NameW = 156, BarW = 148, CellW = 68, UsedW = 84;
        // Column dividers stand in for the gaps between cells: a hairline down
        // the middle of RuleW, and the wider SplitW between alloy and energy.
        private const float RuleW = 10, SplitW = 18;
        private const float RowH = 44, ButtonH = 40, HeadH = 28, TextSize = 22, HeadSize = 20;

        private HudPanel _panel;
        private RectTransform _body, _rewind, _armyRows;
        private TMP_Text _rewindText, _clock, _speedText;
        private HudButton _play, _fog, _timeline, _all;
        private HudSlider _speed, _seek;
        private CanvasGroup _seekGroup;
        private readonly List<ArmyView> _rows = new List<ArmyView>();
        private static Texture2D _restartIcon;

        private sealed class ResourceView
        {
            internal HudBar Bar;
            internal TMP_Text Net, In, Out, Used;
        }

        private sealed class ArmyView
        {
            internal int Id;
            internal RectTransform Row, Cells;
            internal HudButton Name;
            internal TMP_Text Waiting;
            internal ResourceView Alloy, Energy;
        }

        private void UpdatePanel()
        {
            var showing = (ReplayPlayer.Active || ReplayPlayer.Restarting) && _controlsOpen &&
                          ReplayPlayer.Current != ReplayPlayer.Stage.Loading && !MenuOpen(countResult: false);
            if (showing && (_panel == null || !_panel.Alive))
            {
                var root = HudCanvas.EnsureOwn();
                if (root != null) Build(root);
            }
            if (_panel == null || !_panel.Alive) return;
            if (showing) HudCanvas.EnsureOwn();
            _panel.Show(showing);
            if (!showing) return;

            var restarting = ReplayPlayer.Restarting;
            Activate(_rewind, restarting);
            Activate(_body, !restarting);
            if (restarting)
            {
                if (_rewindShown != ReplayPlayer.SeekTarget)
                {
                    _rewindShown = ReplayPlayer.SeekTarget;
                    HudCanvas.SetText(_rewindText, $"Rewinding to {Clock(_rewindShown)}, restarting playback...");
                }
            }
            else Refresh();

            var resized = _panel.TakeResized();
            if (resized != null) _cfgScale.Value = resized.Value;
            _panel.SetScale(Mathf.Clamp(_cfgScale.Value, MinScale, MaxScale));
            var at = _panel.Place(new Vector2(_cfgPosX.Value, _cfgPosY.Value));
            if (_panel.TakeDragged())
            {
                _cfgPosX.Value = at.x;
                _cfgPosY.Value = at.y;
            }
        }

        // What the clock, speed and rewind texts were last made from: they are
        // formatted when that changes, not every frame.
        private int _clockTick = -1, _clockTotal, _clockSeek, _rewindShown = int.MinValue;
        private bool _clockFinished, _clockTimeline;
        private float _shownSpeed = float.NaN;
        // The economy table only changes with a poll (twice a second) or a
        // sort click, so it is redrawn then rather than every frame.
        private bool _tableDirty = true;

        private void Refresh()
        {
            var tick = ReplayPlayer.CurrentTick;
            var total = ReplayPlayer.TotalTicks;
            var finished = ReplayPlayer.Current == ReplayPlayer.Stage.Finished;
            var seek = ReplayPlayer.SeekTarget;
            var timeline = _cfgTimeline.Value;

            if (tick != _clockTick || total != _clockTotal || seek != _clockSeek || finished != _clockFinished || timeline != _clockTimeline)
            {
                _clockTick = tick;
                _clockTotal = total;
                _clockSeek = seek;
                _clockFinished = finished;
                _clockTimeline = timeline;
                var status = finished ? " end" : seek >= 0 ? $" > {Clock(seek)}" : "";
                HudCanvas.SetText(_clock, (timeline ? $"{Clock(tick)} / {Clock(total)}" : Clock(tick)) + status);
            }
            _play.SetLabel(ReplayPlayer.Paused ? "PLAY" : "PAUSE");
            _speed.Set((Mathf.Log(Mathf.Max(0.1f, ReplayPlayer.Speed), 2f) + 2f) / 6f);
            if (ReplayPlayer.Speed != _shownSpeed)
            {
                _shownSpeed = ReplayPlayer.Speed;
                HudCanvas.SetText(_speedText, _shownSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "x");
            }
            _fog.SetOn(_fogOverlay);
            _timeline.SetOn(timeline);

            // The bar is laid out even when the timeline is hidden, so
            // toggling it never resizes the panel under the mouse.
            _seekGroup.alpha = timeline ? 1f : 0f;
            _seekGroup.blocksRaycasts = timeline;
            _seek.Set(total > 1 ? tick / (float)(total - 1) : 0f);

            if (!_tableDirty) return;
            _tableDirty = false;

            // Economy: one row per army, the name being the view button.
            var shown = 0;
            foreach (var a in SortedArmies())
            {
                if (shown == _rows.Count) _rows.Add(NewArmyRow());
                var row = _rows[shown++];
                Activate(row.Row, true);
                row.Id = a.Id;
                row.Name.SetLabel(DisplayName(a));
                row.Name.SetOn(_focus == a.Id, a.Colour);
                var known = _eco.TryGetValue(a.Id, out var e);
                Activate(row.Cells, known);
                Activate(row.Waiting.rectTransform, !known);
                if (!known) continue;
                Resource(row.Alloy, AlloyColour, e.ACur, e.AStore, e.AIn, e.AReq, e.AOut, e.ATotalOut);
                Resource(row.Energy, EnergyColour, e.ECur, e.EStore, e.EIn, e.EReq, e.EOut, e.ETotalOut);
            }
            for (var i = shown; i < _rows.Count; i++) Activate(_rows[i].Row, false);
            _all.SetOn(_focus == -1);
        }

        /// SetActive for something inside the panel: the panel only lays
        /// itself out again when told something in it moved.
        private static void Activate(RectTransform rt, bool on)
        {
            if (rt.gameObject.activeSelf == on) return;
            rt.gameObject.SetActive(on);
            HudCanvas.LayoutVersion++;
        }

        // Per-tick values become per-second; out is what the queue asks for,
        // same as the HUD strip, and net is what really moves the store.
        private static void Resource(ResourceView view, Color colour, float cur, float store, float income, float request, float outcome, float used)
        {
            var inc = Mathf.RoundToInt(income * 10);
            var req = Mathf.RoundToInt(request * 10);
            var net = Mathf.RoundToInt((income - outcome) * 10);
            view.Bar.Set(store > 0 ? cur / store : 0f, colour, $"{Short(cur)} / {Short(store)}");
            HudCanvas.SetText(view.Net, (net >= 0 ? "+" : "") + net);
            view.Net.color = net >= 0 ? GainColour : LossColour;
            HudCanvas.SetText(view.In, "+" + inc);
            HudCanvas.SetText(view.Out, "-" + req);
            HudCanvas.SetText(view.Used, Short(used));
        }

        private void Build(RectTransform root)
        {
            _rows.Clear();
            _tableDirty = true;
            _clockTick = -1;
            _rewindShown = int.MinValue;
            _shownSpeed = float.NaN;
            _panel = HudPanel.Create(root, "Replay controls", () => _cfgLocked.Value);
            _panel.HangLeft = true;
            _panel.EnableResize(MinScale, MaxScale);
            var rect = _panel.Rect;

            _rewind = HudControls.Column(rect, "Rewinding", 4f);
            HudControls.Label(_rewind, "Title", "REPLAY", TextSize, HudControls.TextDim, TextAlignmentOptions.MidlineLeft);
            _rewindText = HudControls.Label(_rewind, "Text", "", TextSize, HudControls.TextMid, TextAlignmentOptions.MidlineLeft);
            _rewind.gameObject.SetActive(false);

            _body = HudControls.Column(rect, "Body", 4f);

            // Clock, transport, speed, jumps, fog, timeline, quit.
            var transport = HudControls.Row(_body, "Transport", 8f);
            _clock = HudControls.Label(transport, "Clock", "", 26f, Color.white, TextAlignmentOptions.MidlineLeft, 216f);
            _play = HudButton.Create(transport, "Play", "PAUSE", 112f, ButtonH);
            _play.OnClick = () => ReplayPlayer.Paused = !ReplayPlayer.Paused;
            // Speed on a log scale, 0.25x to 16x with 1x a third of the way
            // along, in quarter stops. The game clamps at 16x.
            _speed = HudSlider.Create(transport, "Speed", 168f, ButtonH, AccentColour);
            _speed.OnChange = v =>
            {
                if (ReplayPlayer.SeekTarget < 0) ReplayPlayer.Speed = Mathf.Pow(2f, Mathf.Round((v * 6f - 2f) * 4f) / 4f);
            };
            _speedText = HudControls.Label(transport, "Speed", "", TextSize, HudControls.TextMid, TextAlignmentOptions.MidlineLeft, 68f);
            // Forward only, like the seek bar: a minute back would be a whole
            // restart and a fast-forward, which is what RESTART is for.
            HudButton.Create(transport, "Minute", "+1m", 80f, ButtonH).OnClick = () => ReplayPlayer.SeekTo(ReplayPlayer.CurrentTick + 600);
            _fog = HudButton.Create(transport, "Fog", "FOG", 80f, ButtonH);
            _fog.OnClick = () => SetFogOverlay(!_fogOverlay);
            _timeline = HudButton.Create(transport, "Timeline", "TIMELINE", 140f, ButtonH);
            _timeline.OnClick = () => _cfgTimeline.Value = !_cfgTimeline.Value;
            HudControls.Flexible(transport);
            HudButton.Create(transport, "Quit", "QUIT", 96f, ButtonH).OnClick = ReplayPlayer.Quit;

            // Seek bar. Dragging only moves the knob; the jump happens when
            // the mouse is released. Playback can't go back without a full
            // restart, so the bar seeks forward only: the knob won't go left
            // of the current tick, and RESTART is the way back to the start.
            var seekRow = HudControls.Row(_body, "Seek", 8f);
            _seek = HudSlider.Create(seekRow, "Seek", 0f, ButtonH, AccentColour);
            _seek.GetComponent<LayoutElement>().flexibleWidth = 1f;
            _seekGroup = _seek.gameObject.AddComponent<CanvasGroup>();
            _seek.Limit = v =>
            {
                var last = ReplayPlayer.TotalTicks - 1;
                return last > 0 ? Mathf.Max(v, ReplayPlayer.CurrentTick / (float)last) : v;
            };
            _seek.OnChange = v => _dragValue = v * Math.Max(1, ReplayPlayer.TotalTicks - 1);
            // Playback may have caught up to the knob while it was held, and
            // a release behind the current tick must not turn into a restart.
            _seek.OnRelease = () =>
            {
                if (_dragValue > ReplayPlayer.CurrentTick) ReplayPlayer.SeekTo((int)_dragValue);
            };
            // Kept with the mod's canvas, so a hot reload frees it; a cache
            // left holding the destroyed one sees null and draws it again.
            if (_restartIcon == null) _restartIcon = Generated.Keep(SkipToStartIcon(32));
            HudButton.Create(seekRow, "Restart", null, 52f, ButtonH).WithIcon(_restartIcon, 26f).OnClick = Restart;

            // The economy table: headings, a rule, a row per army, and ALL.
            var table = HudControls.Column(_body, "Table", 0f);
            var headings = HudControls.Row(table, "Headings", 0f);
            SortHeading(headings, 0, "ARMY", HudControls.TextDim, TextAlignmentOptions.MidlineLeft, NameW);
            HudControls.Cell(headings, "Gap", RuleW, HeadH);
            ResourceHeader(headings, "ALLOY", "alloy", AlloyColour, 1);
            HudControls.Cell(headings, "Gap", SplitW, HeadH);
            ResourceHeader(headings, "ENERGY", "energy", EnergyColour, 6);
            HudControls.HRule(table, 6f, 0.16f);
            _armyRows = HudControls.Column(table, "Armies", 0f);
            var allRow = HudControls.Row(table, "All", 0f);
            HudControls.Size(allRow.gameObject, -1f, RowH);
            _all = HudButton.Create(allRow, "All", "ALL", NameW, ButtonH);
            _all.OnClick = () => PickView(-1);
        }

        private void PickView(int armyId)
        {
            if (_focus == armyId) return;
            _pendingFocus = int.MinValue;   // a manual pick beats a seek's restore
            SetFocus(armyId);
        }

        // A resource's headings, each a sort button: the store (by what is
        // in it), then net, in, out and used. Their columns are first to
        // first + 4.
        private void ResourceHeader(Transform row, string name, string glyph, Color colour, int first)
        {
            // The resource's mark before its name, as on the HUD's card and strip.
            var mark = Glyphs.Get(glyph);
            if (mark != null)
            {
                var go = new GameObject("Mark", typeof(RectTransform));
                go.transform.SetParent(row, false);
                var image = go.AddComponent<RawImage>();
                image.texture = mark;
                image.color = colour;
                image.raycastTarget = false;
                HudControls.Size(go, 24f, 24f);
            }
            SortHeading(row, first, name, colour, TextAlignmentOptions.MidlineLeft, mark != null ? BarW - 24f : BarW);
            var column = first;
            foreach (var (heading, width) in new[] { ("NET", CellW), ("IN", CellW), ("OUT", CellW), ("USED", UsedW) })
            {
                HudControls.Cell(row, "Gap", RuleW, HeadH);
                SortHeading(row, ++column, heading, HudControls.TextDim, TextAlignmentOptions.MidlineRight, width);
            }
        }

        // ---- sorting the table -----------------------------------------------
        //
        // A heading sorts the rows by its column, highest first; again,
        // lowest first; ARMY puts them back in seat order. Armies with no
        // economy yet stay at the bottom, and ties keep seat order, so
        // rows don't trade places for nothing.

        private const int Columns = 11;   // 0 is seat order, 1-5 alloy, 6-10 energy
        private readonly HudButton[] _sortHeads = new HudButton[Columns];
        private readonly string[] _sortNames = new string[Columns];
        private int _sortBy;
        private bool _sortUp;
        private readonly List<ArmyRow> _sorted = new List<ArmyRow>();

        private void SortHeading(Transform row, int column, string name, Color colour, TextAlignmentOptions alignment, float width)
        {
            var head = HudButton.Create(row, name, name, width, HeadH, HeadSize).Plain(alignment, colour);
            head.OnClick = () =>
            {
                _tableDirty = true;
                if (column == 0) _sortBy = 0;
                else if (_sortBy == column) _sortUp = !_sortUp;
                else
                {
                    _sortBy = column;
                    _sortUp = false;
                }
            };
            _sortHeads[column] = head;
            _sortNames[column] = name;
        }

        private static float SortValue(EcoRow e, int column)
        {
            switch (column)
            {
                case 1: return e.ACur;
                case 2: return e.AIn - e.AOut;
                case 3: return e.AIn;
                case 4: return e.AReq;
                case 5: return e.ATotalOut;
                case 6: return e.ECur;
                case 7: return e.EIn - e.EOut;
                case 8: return e.EIn;
                case 9: return e.EReq;
                case 10: return e.ETotalOut;
                default: return 0f;
            }
        }

        private int CompareArmies(ArmyRow x, ArmyRow y)
        {
            var hasX = _eco.TryGetValue(x.Id, out var ex);
            var hasY = _eco.TryGetValue(y.Id, out var ey);
            if (hasX != hasY) return hasX ? -1 : 1;
            if (hasX)
            {
                var c = SortValue(ex, _sortBy).CompareTo(SortValue(ey, _sortBy));
                if (c != 0) return _sortUp ? c : -c;
            }
            // Seat order: _armies is kept sorted by id.
            return x.Id.CompareTo(y.Id);
        }

        private Comparison<ArmyRow> _compareArmies;

        /// The armies with a row, in the table's order.
        private List<ArmyRow> SortedArmies()
        {
            _sorted.Clear();
            foreach (var a in _armies) if (Playing(a)) _sorted.Add(a);
            if (_sortBy > 0) _sorted.Sort(_compareArmies ??= CompareArmies);
            for (var i = 0; i < Columns; i++)
            {
                var head = _sortHeads[i];
                if (head == null) continue;
                var on = i > 0 && i == _sortBy;
                head.SetOn(on);
                head.SetLabel(on ? _sortNames[i] + (_sortUp ? " ▲" : " ▼") : _sortNames[i]);
            }
            return _sorted;
        }

        private ArmyView NewArmyRow()
        {
            var view = new ArmyView();
            view.Row = HudControls.Row(_armyRows, "Army", 0f);
            HudControls.Size(view.Row.gameObject, -1f, RowH);
            view.Name = HudButton.Create(view.Row, "Name", "", NameW, ButtonH);
            view.Name.OnClick = () => PickView(view.Id);
            view.Cells = HudControls.Row(view.Row, "Cells", 0f);
            HudControls.Rule(view.Cells, RuleW, RowH, 0.09f);
            view.Alloy = ResourceCells(view.Cells);
            HudControls.Rule(view.Cells, SplitW, RowH, 0.2f);
            view.Energy = ResourceCells(view.Cells);
            view.Waiting = HudControls.Label(view.Row, "Waiting", "   waiting for economy data", TextSize, HudControls.TextDim, TextAlignmentOptions.MidlineLeft);
            return view;
        }

        private static ResourceView ResourceCells(Transform row)
        {
            var view = new ResourceView { Bar = HudBar.Create(row, "Store", BarW, 32f, HeadSize) };
            HudControls.Rule(row, RuleW, RowH, 0.09f);
            view.Net = HudControls.Label(row, "Net", "", TextSize, Color.white, TextAlignmentOptions.MidlineRight, CellW);
            HudControls.Rule(row, RuleW, RowH, 0.09f);
            view.In = HudControls.Label(row, "In", "", TextSize, GainColour, TextAlignmentOptions.MidlineRight, CellW);
            HudControls.Rule(row, RuleW, RowH, 0.09f);
            view.Out = HudControls.Label(row, "Out", "", TextSize, OutColour, TextAlignmentOptions.MidlineRight, CellW);
            HudControls.Rule(row, RuleW, RowH, 0.09f);
            view.Used = HudControls.Label(row, "Used", "", TextSize, HudControls.TextMid, TextAlignmentOptions.MidlineRight, UsedW);
            return view;
        }

        /// A "skip to start" glyph (a bar plus two left-pointing triangles),
        /// drawn white so it takes the button's tint. Rendered oversized and
        /// shrunk on the button, which is what anti-aliases the triangle
        /// edges - there's no per-pixel AA in this rasterizer.
        private static Texture2D SkipToStartIcon(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color[size * size];
            var cy = size / 2f;
            var barW = size * 0.11f;
            var barX0 = size * 0.12f;
            var barX1 = barX0 + barW;
            var triTop = size * 0.16f;
            var triBot = size * 0.84f;
            var triW = size * 0.32f;
            var gap = size * 0.08f;
            var t1X0 = barX1 + gap;
            var t1X1 = t1X0 + triW;
            var t2X0 = t1X1 + gap * 0.6f;
            var t2X1 = t2X0 + triW;

            Vector2 t1A = new Vector2(t1X1, triTop), t1B = new Vector2(t1X1, triBot), t1C = new Vector2(t1X0, cy);
            Vector2 t2A = new Vector2(t2X1, triTop), t2B = new Vector2(t2X1, triBot), t2C = new Vector2(t2X0, cy);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var inBar = p.x >= barX0 && p.x <= barX1 && p.y >= triTop && p.y <= triBot;
                    var inTri = PointInTriangle(p, t1A, t1B, t1C) || PointInTriangle(p, t2A, t2B, t2C);
                    px[y * size + x] = new Color(1f, 1f, 1f, inBar || inTri ? 1f : 0f);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Sign(Vector2 p1, Vector2 p2, Vector2 p3) => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
            var d1 = Sign(p, a, b);
            var d2 = Sign(p, b, c);
            var d3 = Sign(p, c, a);
            var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static string Clock(int ticks)
        {
            var s = Math.Max(0, ticks) / 10;
            return $"{s / 60}:{s % 60:00}";
        }

        private static string Short(float v)
        {
            if (v >= 1_000_000f) return (v / 1_000_000f).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (v >= 10_000f) return (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture);
        }
    }
}
