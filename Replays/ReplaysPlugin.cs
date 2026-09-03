using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using EM.DOTS.Engine.Loader;
using HarmonyLib;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.Replays
{
    // Records every match to a file and plays recordings back in-game with
    // the fog lifted, any player's point of view, and every army's economy.
    //
    // Recording: see ReplayRecorder. Playback: see ReplayPlayer. This class
    // is the config, the hotkey, the replay browser (main menu) and the
    // control panel (during playback), plus the runtime Lua hooks that make
    // the client keep every army's economy totals instead of only the
    // focused one.
    [BepInPlugin("com.sanctuarydb.replays", "Replays", "0.1.0")]
    public class ReplaysPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgRecord;
        private ConfigEntry<string> _cfgFolder;
        private ConfigEntry<KeyCode> _cfgKey;
        private ConfigEntry<bool> _cfgRecIndicator;
        private ConfigEntry<bool> _cfgTimeline;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;

        // Browser (main menu).
        private bool _browserOpen;
        private List<Entry> _entries;
        private Vector2 _scroll;
        private string _status;
        private Rect _browserRect = new Rect(160, 100, 1000, 620);

        // Control panel (playback).
        private bool _controlsOpen = true;
        private Rect _ctrlRect = new Rect(12, 300, 0, 0);
        private bool _fogOverlay;
        private int _lastFocus = int.MinValue;

        // Lua-side state, polled.
        private float _luaAccum;
        private bool _luaHooked;
        private List<ArmyRow> _armies = new List<ArmyRow>();
        private Dictionary<int, EcoRow> _eco = new Dictionary<int, EcoRow>();
        private int _focus = int.MinValue;
        private string _lastHookErr;
        private bool _loggedLuaSample;

        // Seek bar: the knob sets a target while dragged; it is applied when
        // the mouse comes up, so a drag doesn't fire a restart per pixel.
        private bool _dragging;
        private float _dragValue;
        // View to put back after a rewind rebuilds the client.
        private int _pendingFocus = int.MinValue;

        private sealed class Entry
        {
            public string Path;
            public string Name;
            public DateTime Written;
            public ReplayHeader Header;
            public bool Unfinished;
            public string Error;
        }

        private sealed class ArmyRow
        {
            public int Id;
            public string Name;
            public string Faction;
            public bool Human;
            public Color Colour;
        }

        private sealed class EcoRow
        {
            public float ACur, AStore, AIn, AHarvest, AOut, AReq;
            public float ECur, EStore, EIn, EHarvest, EOut, EReq;
            public float ATotalIn, ATotalOut, ETotalIn, ETotalOut;   // whole game so far
        }

        private void Awake()
        {
            _log ??= Logger;

            _cfgRecord = Config.Bind("Recording", "Enabled", true,
                "Record every match you play or observe to a replay file.");
            _cfgFolder = Config.Bind("Recording", "Folder", "",
                "Where replays are saved. Empty means Documents\\Sanctuary Replays.");
            _cfgTimeline = Config.Bind("UI", "ShowTimeline", true,
                "Show the replay's total length and the seek bar. Off hides both, for watching without knowing when the game ends.");
            _cfgRecIndicator = Config.Bind("Recording", "ShowIndicator", true,
                "Show a small REC marker in the corner while a match is being recorded.");
            _cfgKey = Config.Bind("UI", "ToggleKey", KeyCode.F7,
                "Opens the replay browser in the main menu, and shows/hides the control panel during playback.");
            _cfgPosX = Config.Bind("UI", "PanelX", 12f, "Playback control panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("UI", "PanelY", 300f, "Playback control panel Y in 1080p-logical pixels.");
            _ctrlRect.x = _cfgPosX.Value;
            _ctrlRect.y = _cfgPosY.Value;

            ReplayRecorder.Folder = ResolveFolder();
            ReplayRecorder.Enabled = _cfgRecord.Value;

            try
            {
                _harmony = new Harmony("com.sanctuarydb.replays." + Guid.NewGuid().ToString("N").Substring(0, 8));
                // In-match signal, same as the other mods.
                ApplyEconomyPatch(_harmony);
                ReplayRecorder.ApplyPatches(_harmony);
                ReplayPlayer.ApplyPatches(_harmony);
                _harmony.Patch(AccessTools.Method(typeof(EngineLoader), nameof(EngineLoader.CleanUpGame)),
                    prefix: new HarmonyMethod(typeof(ReplaysPlugin), nameof(CleanUpPrefix)));
            }
            catch (Exception e)
            {
                Logger.LogError($"Replays: patching failed, recording and playback are off: {e}");
                _harmony?.UnpatchSelf();
                _harmony = null;
            }

            Logger.LogInfo($"Replays loaded: {_cfgKey.Value} for the browser, saving to {ReplayRecorder.Folder}.");
        }

        private void OnDestroy()
        {
            ReplayRecorder.Stop();
            ReplayPlayer.Stop();
            _harmony?.UnpatchSelf();
        }

        // Leaving a match, by any route, ends both.
        private static void CleanUpPrefix()
        {
            ReplayRecorder.Stop();
            ReplayPlayer.Stop();
        }

        private string ResolveFolder()
        {
            var folder = _cfgFolder.Value;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sanctuary Replays");
            }
            return folder;
        }

        private void Update()
        {
            ReplayRecorder.Enabled = _cfgRecord.Value;
            ReplayRecorder.Folder = ResolveFolder();

            if (Input.GetKeyDown(_cfgKey.Value))
            {
                if (ReplayPlayer.Active) _controlsOpen = !_controlsOpen;
                else if (!InMatch)
                {
                    _browserOpen = !_browserOpen;
                    if (_browserOpen) Rescan();
                }
            }

            ReplayPlayer.Update(Time.unscaledDeltaTime);

            if (_dragging && !Input.GetMouseButton(0))
            {
                _dragging = false;
                Seek((int)_dragValue);
            }

            if (ReplayPlayer.Active)
            {
                PollLua(Time.unscaledDeltaTime);
                if (!Input.GetMouseButton(0) &&
                    (Math.Abs(_cfgPosX.Value - _ctrlRect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _ctrlRect.y) > 0.5f))
                {
                    _cfgPosX.Value = _ctrlRect.x;
                    _cfgPosY.Value = _ctrlRect.y;
                }
            }
            else if (_luaHooked || _armies.Count > 0)
            {
                _luaHooked = false;
                _armies = new List<ArmyRow>();
                _eco = new Dictionary<int, EcoRow>();
                _focus = int.MinValue;
                _lastFocus = int.MinValue;
            }
        }

        // ---- Lua side ------------------------------------------------------

        // Keeps every army's economy totals, not just the focused army's. The
        // receiver is looked up from the command table each time, so swapping
        // the table entry catches it; the format table it needs is a local of
        // commands.lua, reachable only as an upvalue of the original receiver.
        // Also marks this client an observer so clicks can't issue orders.
        private const string InstallChunk =
            "if not __SdbReplayHook then " +
            "  __SdbReplayHook = true " +
            "  __SdbReplayEco = '' " +
            "  __SdbReplayHookErr = '' " +
            "  pcall(function() SetObserver(true) end) " +
            "  local ok, err = pcall(function() " +
            "    local C = Import('common/systems/commands.lua') " +
            "    local cmd = C.HostCustomCommands.UpdateEconomyTotalsCommand " +
            "    local orig = cmd.ClientReceive " +
            "    local bs = Import('common/systems/binarySerialization.lua') " +
            "    local fmt = nil " +
            "    pcall(function() " +
            "      for i = 1, 64 do " +
            "        local n, v = debug.getupvalue(orig, i) " +
            "        if not n then break end " +
            "        if n == 'commandFormats' then fmt = v.UpdateEconomyTotalsCommand end " +
            "      end " +
            "    end) " +
            // The receiver's format table is a local of commands.lua; if it
            // can't be reached as an upvalue, fall back to a copy of it.
            "    if not fmt then " +
            "      local f = {} " +
            "      for _, k in ipairs({'current','storage','satisfaction','income','harvest','outcome','request','balance'}) do " +
            "        f[#f+1] = { key = k, type = 'float' } " +
            "      end " +
            "      fmt = { { key = 'armyId', type = 'int' }, " +
            "              { key = 'totals', type = { type = 'dictionary', key_type = 'string', value_type = f } } } " +
            "      __SdbReplayHookErr = 'format copied' " +
            "    end " +
            "    local eco = {} " +
            "    local sum = {} " +
            "    cmd.ClientReceive = function(command) " +
            "      local ok2, err2 = pcall(function() " +
            "        local data = bs.Deserialize(fmt, command.commandData) " +
            "        eco[data.armyId] = data.totals " +
            // Totals arrive once per army per tick, so summing the per-tick
            // figures here gives the whole game so far.
            "        local a, e = data.totals.alloys or {}, data.totals.energy or {} " +
            "        local s = sum[data.armyId] or { ai = 0, ao = 0, ei = 0, eo = 0 } " +
            "        s.ai = s.ai + (a.income or 0) + (a.harvest or 0) " +
            "        s.ao = s.ao + (a.outcome or 0) " +
            "        s.ei = s.ei + (e.income or 0) + (e.harvest or 0) " +
            "        s.eo = s.eo + (e.outcome or 0) " +
            "        sum[data.armyId] = s " +
            "        local parts = {} " +
            "        for id, t in pairs(eco) do " +
            "          local a, e = t.alloys or {}, t.energy or {} " +
            "          local s = sum[id] or { ai = 0, ao = 0, ei = 0, eo = 0 } " +
            "          parts[#parts+1] = string.format('%d|%.0f|%.0f|%.3f|%.3f|%.3f|%.3f|%.0f|%.0f|%.3f|%.3f|%.3f|%.3f|%.1f|%.1f|%.1f|%.1f', " +
            "            id, a.current or 0, a.storage or 0, a.income or 0, a.harvest or 0, a.outcome or 0, a.request or 0, " +
            "            e.current or 0, e.storage or 0, e.income or 0, e.harvest or 0, e.outcome or 0, e.request or 0, " +
            "            s.ai, s.ao, s.ei, s.eo) " +
            "        end " +
            "        __SdbReplayEco = table.concat(parts, ';') " +
            "      end) " +
            "      if not ok2 then __SdbReplayHookErr = 'receive: ' .. tostring(err2) end " +
            "      return orig(command) " +
            "    end " +
            "  end) " +
            "  if not ok then __SdbReplayHookErr = 'install: ' .. tostring(err) end " +
            "end";

        private const string QueryChunk =
            "pcall(function() " +
            "  local out = {} " +
            "  for id, a in pairs(Armies or {}) do " +
            "    if not a.civilian then " +
            "      local c = a.color or { x = 0.5, y = 0.5, z = 0.5 } " +
            "      out[#out+1] = string.format('%d|%s|%s|%s|%.3f|%.3f|%.3f', id, tostring(a.name), tostring(a.factionId), tostring(a.human), c.x, c.y, c.z) " +
            "    end " +
            "  end " +
            "  __SdbReplayArmies = table.concat(out, ';') " +
            "  __SdbReplayFocus = tostring(GetFocusArmy()) " +
            "end)";

        private void PollLua(float dt)
        {
            _luaAccum += dt;
            if (_luaAccum < 0.5f) return;
            _luaAccum = 0f;

            EnsureLuaBridge();
            if (!LuaReady) return;

            if (!_luaHooked && RunLua(InstallChunk)) _luaHooked = true;
            if (!RunLua(QueryChunk)) return;

            var armies = GetLuaGlobal("__SdbReplayArmies");
            var eco = GetLuaGlobal("__SdbReplayEco");
            ParseArmies(armies);
            ParseEco(eco);
            var focus = GetLuaGlobal("__SdbReplayFocus");
            if (int.TryParse(focus, out var f)) _focus = f;

            // After a rewind the client is brand new and back on the seat's
            // own army; restore the view that was being watched.
            if (_pendingFocus != int.MinValue && _luaHooked && _focus != int.MinValue && _armies.Count > 0)
            {
                if (_pendingFocus != _focus) SetFocus(_pendingFocus);
                _pendingFocus = int.MinValue;
            }

            // Say once what the Lua side is producing, so a silent hook can be
            // diagnosed from the log alone.
            var err = GetLuaGlobal("__SdbReplayHookErr");
            if (!string.IsNullOrEmpty(err) && err != _lastHookErr)
            {
                _lastHookErr = err;
                Logger.LogWarning($"Replays: economy hook reports: {err}");
            }
            if (!_loggedLuaSample && !string.IsNullOrEmpty(eco))
            {
                _loggedLuaSample = true;
                Logger.LogDebug($"Replays: armies '{armies}', focus {focus}, eco sample '{eco}'");
            }

            // The fog overlay follows the focus unless the user has overridden it:
            // no overlay in the all-armies view, the army's own fog otherwise.
            if (_focus != _lastFocus)
            {
                _lastFocus = _focus;
                SetFogOverlay(_focus != -1);
            }
        }

        private void ParseArmies(string raw)
        {
            if (raw == null) return;
            var rows = new List<ArmyRow>();
            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');
                if (f.Length < 4 || !int.TryParse(f[0], out var id)) continue;
                var row = new ArmyRow { Id = id, Name = f[1], Faction = FactionName(f[2]), Human = f[3] == "true", Colour = Accent };
                if (f.Length >= 7)
                {
                    var inv = CultureInfo.InvariantCulture;
                    float.TryParse(f[4], NumberStyles.Float, inv, out var r);
                    float.TryParse(f[5], NumberStyles.Float, inv, out var g);
                    float.TryParse(f[6], NumberStyles.Float, inv, out var b);
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
            if (raw == null) return;
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
            }
        }

        private static string FactionName(string factionId)
        {
            switch (factionId)
            {
                case "0": return "EDA";
                case "1": return "Chosen";
                case "2": return "Guard";
                default: return factionId;
            }
        }

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

        private void Seek(int tick)
        {
            if (tick < ReplayPlayer.CurrentTick) _pendingFocus = _focus;   // a rewind rebuilds the client
            ReplayPlayer.SeekTo(tick);
        }

        // ---- browser -------------------------------------------------------

        private void Rescan()
        {
            _status = null;
            var list = new List<Entry>();
            try
            {
                var folder = ReplayRecorder.Folder;
                if (Directory.Exists(folder))
                {
                    foreach (var path in Directory.GetFiles(folder))
                    {
                        var unfinished = path.EndsWith(ReplayFile.Extension + ReplayFile.PartSuffix, StringComparison.OrdinalIgnoreCase);
                        if (!unfinished && !path.EndsWith(ReplayFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                        var e = new Entry { Path = path, Name = Path.GetFileName(path), Written = File.GetLastWriteTime(path), Unfinished = unfinished };
                        try { e.Header = ReplayFile.ReadHeaderOnly(path); }
                        catch (Exception ex) { e.Error = ex.Message; }
                        list.Add(e);
                    }
                }
            }
            catch (Exception e)
            {
                _status = "Could not read the replay folder: " + e.Message;
            }
            list.Sort((a, b) => b.Written.CompareTo(a.Written));
            _entries = list;
        }

        // ---- look ----------------------------------------------------------

        private static readonly Color Accent = new Color(0.3f, 0.6f, 0.95f, 0.95f);
        private static readonly Color TextDim = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color TextMid = new Color(1f, 1f, 1f, 0.78f);
        private static readonly Color OutColour = new Color(1f, 0.5f, 0.45f);

        private static bool _uiReady;
        private static Texture2D _texPanelBg, _texBtn, _texBtnHover, _texBtnOn, _texBtnOnHover, _texKnob, _texTrack, _texRow;
        private static GUIStyle _stPanel, _stTitle, _stTime, _stBody, _stDim, _stHead, _stButton, _stToggleBtn, _stCheck;
        private static GUIStyle _stNet, _stIn, _stOut, _stBar, _stEntryTitle, _stEntrySub, _stPrimary;

        private static void EnsureUi()
        {
            if (_uiReady) return;
            _uiReady = true;

            _texPanelBg = Rounded(10, new Color(0.05f, 0.07f, 0.09f, 0.9f));
            _texBtn = Rounded(5, new Color(1f, 1f, 1f, 0.09f));
            _texBtnHover = Rounded(5, new Color(1f, 1f, 1f, 0.17f));
            // White, so the on-state takes its colour from GUI.backgroundColor
            // at draw time: the accent for plain toggles, the army's own
            // colour for the view rows.
            _texBtnOn = Rounded(5, Color.white);
            _texBtnOnHover = Rounded(5, new Color(0.92f, 0.92f, 0.92f, 1f));
            _texKnob = Rounded(7, new Color(0.95f, 0.96f, 0.98f, 1f));
            // Opaque white; tracks, fills and bars tint it at draw time, so the
            // fill colour isn't washed out by a translucent source.
            _texTrack = Rounded(3, Color.white);
            _texRow = Rounded(5, new Color(1f, 1f, 1f, 0.05f));

            _stPanel = new GUIStyle
            {
                normal = { background = _texPanelBg },
                border = new RectOffset(11, 11, 11, 11),
                padding = new RectOffset(12, 12, 10, 12),
            };
            _stTitle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextDim } };
            _stTime = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _stBody = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextMid }, clipping = TextClipping.Clip };
            _stDim = new GUIStyle(_stBody) { normal = { textColor = TextDim } };
            _stHead = new GUIStyle { fontSize = 10, alignment = TextAnchor.LowerLeft, normal = { textColor = TextDim } };
            _stButton = new GUIStyle
            {
                fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { background = _texBtn, textColor = TextMid },
                hover = { background = _texBtnHover, textColor = Color.white },
                active = { background = _texBtnHover, textColor = Color.white },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(8, 8, 3, 3),
                margin = new RectOffset(2, 2, 2, 2),
                fixedHeight = 22,
                clipping = TextClipping.Clip,
            };
            _stToggleBtn = new GUIStyle(_stButton)
            {
                onNormal = { background = _texBtnOn, textColor = Color.white },
                onHover = { background = _texBtnOnHover, textColor = Color.white },
                onActive = { background = _texBtnOnHover, textColor = Color.white },
            };
            _stCheck = new GUIStyle(_stToggleBtn) { alignment = TextAnchor.MiddleCenter };
            // A solid button, tinted by GUI.backgroundColor at draw time.
            _stPrimary = new GUIStyle(_stButton)
            {
                normal = { background = _texBtnOn, textColor = Color.white },
                hover = { background = _texBtnOnHover, textColor = Color.white },
                active = { background = _texBtnOnHover, textColor = Color.white },
            };
            _stNet = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };
            _stIn = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleRight, normal = { textColor = GainColour } };
            _stOut = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleRight, normal = { textColor = OutColour } };
            _stBar = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _stEntryTitle = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _stEntrySub = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextDim }, wordWrap = true };
        }

        // A rounded square with an anti-aliased edge; sliced by the style's
        // border so it stretches into any rectangle.
        private static Texture2D Rounded(int radius, Color colour)
        {
            var size = radius * 2 + 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the nearest corner arc centre, only in the corner quadrants.
                    float cx = x < radius ? radius : x >= size - radius ? size - radius - 1 : x;
                    float cy = y < radius ? radius : y >= size - radius ? size - radius - 1 : y;
                    var d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    var cover = Mathf.Clamp01(radius + 0.5f - d);
                    px[y * size + x] = new Color(colour.r, colour.g, colour.b, colour.a * cover);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        /// A thin slider with a filled track. Returns the (possibly new)
        /// value; `changed` is true only while the user is dragging it.
        private static float Slider(Rect r, float value, float min, float max, Color fill, out bool changed)
        {
            changed = false;
            var id = GUIUtility.GetControlID(FocusType.Passive);
            var ev = Event.current;
            var frac = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
            var result = value;

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (r.Contains(ev.mousePosition) && ev.button == 0)
                    {
                        GUIUtility.hotControl = id;
                        changed = true;
                        ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        changed = true;
                        ev.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        ev.Use();
                    }
                    break;
            }
            if (changed)
            {
                var f = Mathf.Clamp01((ev.mousePosition.x - r.x - 6) / Mathf.Max(1, r.width - 12));
                result = min + f * (max - min);
                frac = f;
            }

            if (ev.type == EventType.Repaint)
            {
                var track = new Rect(r.x + 6, r.y + r.height / 2 - 2, r.width - 12, 4);
                GUI.DrawTexture(track, _texTrack, ScaleMode.StretchToFill, true, 0, new Color(1f, 1f, 1f, 0.16f), 0, 3);
                var filled = track;
                filled.width = track.width * frac;
                if (filled.width > 1) GUI.DrawTexture(filled, _texTrack, ScaleMode.StretchToFill, true, 0, fill, 0, 3);
                var knob = new Rect(track.x + track.width * frac - 6, r.y + r.height / 2 - 6, 12, 12);
                GUI.DrawTexture(knob, _texKnob, ScaleMode.StretchToFill, true, 0, Color.white, 0, 6);
            }
            return result;
        }

        /// A flat toggle button whose on-state is solid `colour` and whose
        /// off-state carries a faint wash of it. Light colours get dark text.
        private static bool Toggle(bool on, string label, Color colour, params GUILayoutOption[] options)
        {
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = on ? colour : new Color(colour.r, colour.g, colour.b, 1f);
            var luma = 0.299f * colour.r + 0.587f * colour.g + 0.114f * colour.b;
            var onText = luma > 0.62f ? new Color(0.08f, 0.1f, 0.12f) : Color.white;
            var savedOn = _stToggleBtn.onNormal.textColor;
            var savedOnHover = _stToggleBtn.onHover.textColor;
            _stToggleBtn.onNormal.textColor = onText;
            _stToggleBtn.onHover.textColor = onText;
            _stToggleBtn.onActive.textColor = onText;
            var result = GUILayout.Toggle(on, label, _stToggleBtn, options);
            _stToggleBtn.onNormal.textColor = savedOn;
            _stToggleBtn.onHover.textColor = savedOnHover;
            _stToggleBtn.onActive.textColor = savedOnHover;
            GUI.backgroundColor = oldBg;
            return result;
        }

        private static void Bar(Rect r, float fraction, Color colour, string text)
        {
            // Plain flat rectangles: a faint track with a solid fill.
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.1f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                var fill = r;
                fill.width = r.width * Mathf.Clamp01(fraction);
                GUI.color = colour;
                if (fill.width > 1) GUI.DrawTexture(fill, Texture2D.whiteTexture);
                GUI.color = old;
            }
            GUI.Label(r, text, _stBar);
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

        // ---- drawing -------------------------------------------------------

        // Column widths, 1080p-logical. The panel is sized from these plus its
        // row count, so it fits two players or twelve.
        private const float NameW = 84, BarW = 84, CellW = 36, UsedW = 46, Gap = 4;
        // Row height; the name button is 22 tall with a 2px margin, so 26
        // puts its centre on the same line as full-height cells.
        private const float RowH = 26;
        private const float ResourceW = BarW + Gap + CellW * 3 + Gap + UsedW;
        private const float PanelW = 24 + NameW + Gap + ResourceW + 10 + ResourceW;
        private int _lastRowCount = -1;

        private void OnGUI()
        {
            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;
            var logicalHeight = Screen.height / scale;
            EnsureUi();

            try
            {
                if (ReplayPlayer.Active || ReplayPlayer.Restarting)
                {
                    if (_controlsOpen && ReplayPlayer.Current != ReplayPlayer.Stage.Loading)
                    {
                        // A layout window grows to its content but never
                        // shrinks, so zero the height once when the row count
                        // changes. Not every frame: drag events skip the layout
                        // pass, and a zero-height rect then draws nothing.
                        _ctrlRect.width = PanelW;
                        if (_armies.Count != _lastRowCount)
                        {
                            _lastRowCount = _armies.Count;
                            _ctrlRect.height = 0;
                        }
                        _ctrlRect.x = Mathf.Clamp(_ctrlRect.x, -PanelW + 80, logicalWidth - 80);
                        _ctrlRect.y = Mathf.Clamp(_ctrlRect.y, 0, logicalHeight - 40);
                        _ctrlRect = GUILayout.Window(0x53445250, _ctrlRect, DrawControls, GUIContent.none, _stPanel, GUILayout.Width(PanelW));
                    }
                }
                else if (_browserOpen && !InMatch)
                {
                    _browserRect.x = (logicalWidth - _browserRect.width) / 2;
                    _browserRect.y = Mathf.Max(40, (logicalHeight - _browserRect.height) / 2);
                    _browserRect = GUILayout.Window(0x53445251, _browserRect, DrawBrowser, GUIContent.none, _stPanel,
                        GUILayout.Width(_browserRect.width), GUILayout.Height(_browserRect.height));
                }
                else if (_cfgRecIndicator.Value && ReplayRecorder.Recording && InMatch)
                {
                    var t = Math.Max(0, ReplayRecorder.LastTick) / 10;
                    var old = GUI.color;
                    GUI.color = new Color(1f, 0.35f, 0.35f, 0.9f);
                    GUI.Label(new Rect(8, logicalHeight - 22, 200, 20), $"● REC {t / 60}:{t % 60:00}", _stBody);
                    GUI.color = old;
                }
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }

        private void DrawControls(int id)
        {
            var tick = ReplayPlayer.CurrentTick;
            var total = ReplayPlayer.TotalTicks;
            var finished = ReplayPlayer.Current == ReplayPlayer.Stage.Finished;
            var seeking = ReplayPlayer.SeekTarget >= 0;

            if (ReplayPlayer.Restarting)
            {
                GUILayout.Label("REPLAY", _stTitle);
                GUILayout.Label($"Rewinding to {Clock(ReplayPlayer.SeekTarget)}, restarting the client...", _stBody);
                GUI.DragWindow(new Rect(0, 0, 10000, 30));
                return;
            }

            // Header: clock, transport, speed, jumps, fog, quit.
            GUILayout.BeginHorizontal(GUILayout.Height(22));
            var status = finished ? " end" : seeking ? $" > {Clock(ReplayPlayer.SeekTarget)}" : "";
            var timeline = _cfgTimeline.Value;
            var clock = timeline ? $"{Clock(tick)} / {Clock(total)}" : Clock(tick);
            GUILayout.Label(clock + status, _stTime, GUILayout.Width(NameW + 30));
            if (GUILayout.Button(ReplayPlayer.Paused ? "PLAY" : "PAUSE", _stButton, GUILayout.Width(56))) ReplayPlayer.Paused = !ReplayPlayer.Paused;
            GUILayout.Space(6);
            // Speed on a log scale, 0.25x to 8x with 1x in the middle, in quarter stops.
            var exp = Mathf.Log(Mathf.Max(0.01f, ReplayPlayer.Speed), 2f);
            var speedRect = GUILayoutUtility.GetRect(90, 22, GUILayout.Width(90));
            var newExp = Slider(speedRect, exp, -2f, 3f, Accent, out var speedChanged);
            if (speedChanged) ReplayPlayer.Speed = Mathf.Pow(2f, Mathf.Round(newExp * 4f) / 4f);
            GUILayout.Label(ReplayPlayer.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x", _stBody, GUILayout.Width(34));
            if (GUILayout.Button("-1m", _stButton, GUILayout.Width(40))) Seek(tick - 600);
            if (GUILayout.Button("+1m", _stButton, GUILayout.Width(40))) Seek(tick + 600);
            GUILayout.Space(6);
            var fog = Toggle(_fogOverlay, "FOG", Accent, GUILayout.Width(40));
            if (fog != _fogOverlay) SetFogOverlay(fog);
            var tl = Toggle(timeline, "TIMELINE", Accent, GUILayout.Width(70));
            if (tl != timeline)
            {
                _cfgTimeline.Value = tl;
                _lastRowCount = -1;   // the panel changes height; let it re-fit
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("QUIT", _stButton, GUILayout.Width(48))) ReplayPlayer.Quit();
            GUILayout.EndHorizontal();

            // Seek bar. Dragging only moves the knob; the jump happens when the
            // mouse is released (see Update).
            if (timeline)
            {
                var seekRect = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
                var shown = _dragging ? _dragValue : tick;
                var v2 = Slider(seekRect, shown, 0, Math.Max(1, total - 1), Accent, out var seekChanged);
                if (seekChanged)
                {
                    _dragging = true;
                    _dragValue = v2;
                }
            }

            // Economy: one row per army, the name being the view button.
            // The seek bar carries its own breathing room; without it the
            // table needs some.
            GUILayout.Space(timeline ? 2 : 8);
            GUILayout.BeginHorizontal(GUILayout.Height(16));
            GUILayout.Label("ARMY", _stHead, GUILayout.Width(NameW));
            GUILayout.Space(Gap);
            ResourceHeader("ALLOY", AlloyColour);
            GUILayout.Space(14);
            ResourceHeader("ENERGY", EnergyColour);
            GUILayout.EndHorizontal();

            foreach (var a in _armies)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(RowH));
                var on = _focus == a.Id;
                if (Toggle(on, DisplayName(a), a.Colour, GUILayout.Width(NameW)) && !on) SetFocus(a.Id);
                GUILayout.Space(Gap);
                if (_eco.TryGetValue(a.Id, out var e))
                {
                    Resource(AlloyColour, e.ACur, e.AStore, e.AIn + e.AHarvest, e.AReq, e.AOut, e.ATotalOut);
                    GUILayout.Space(14);
                    Resource(EnergyColour, e.ECur, e.EStore, e.EIn + e.EHarvest, e.EReq, e.EOut, e.ETotalOut);
                }
                else
                {
                    GUILayout.Label("waiting for economy data", _stDim);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal(GUILayout.Height(RowH));
            var all = _focus == -1;
            if (Toggle(all, "ALL", Accent, GUILayout.Width(NameW)) && !all) SetFocus(-1);
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        // The client only knows armies by slot ("Army_1"); the replay header
        // carries the lobby, which maps each slot to who sat in it.
        private static string DisplayName(ArmyRow a)
        {
            var players = ReplayPlayer.Header?.Players;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p.ArmyId == a.Id && !string.IsNullOrEmpty(p.Name) && p.Type != "Observer") return p.Name;
                }
            }
            return a.Name;
        }

        private static void ResourceHeader(string name, Color colour)
        {
            var old = GUI.color;
            GUI.color = colour;
            GUILayout.Label(name, _stHead, GUILayout.Width(BarW));
            GUI.color = old;
            GUILayout.Space(Gap);
            var right = new GUIStyle(_stHead) { alignment = TextAnchor.LowerRight };
            GUILayout.Label("NET", right, GUILayout.Width(CellW));
            GUILayout.Label("IN", right, GUILayout.Width(CellW));
            GUILayout.Label("OUT", right, GUILayout.Width(CellW));
            GUILayout.Space(Gap);
            GUILayout.Label("USED", right, GUILayout.Width(UsedW));
        }

        // Per-tick values become per-second; out is what the queue asks for,
        // same as the HUD strip, and net is what really moves the store.
        private static void Resource(Color colour, float cur, float store, float income, float request, float outcome, float used)
        {
            var inc = Mathf.RoundToInt(income * 10);
            var req = Mathf.RoundToInt(request * 10);
            var net = Mathf.RoundToInt((income - outcome) * 10);

            // Every cell is the full row height so the text and the bar all
            // centre on the same line as the name button.
            var r = GUILayoutUtility.GetRect(BarW, RowH, GUILayout.Width(BarW));
            r.y += (RowH - 16) / 2;
            r.height = 16;
            Bar(r, store > 0 ? cur / store : 0f, colour, $"{Short(cur)} / {Short(store)}");
            GUILayout.Space(Gap);
            var old = GUI.color;
            GUI.color = net >= 0 ? GainColour : LossColour;
            GUILayout.Label((net >= 0 ? "+" : "") + net, _stNet, GUILayout.Width(CellW), GUILayout.Height(RowH));
            GUI.color = old;
            GUILayout.Label("+" + inc, _stIn, GUILayout.Width(CellW), GUILayout.Height(RowH));
            GUILayout.Label("-" + req, _stOut, GUILayout.Width(CellW), GUILayout.Height(RowH));
            GUILayout.Space(Gap);
            var right = new GUIStyle(_stBody) { alignment = TextAnchor.MiddleRight };
            GUILayout.Label(Short(used), right, GUILayout.Width(UsedW), GUILayout.Height(RowH));
        }

        // ---- browser -------------------------------------------------------

        private void DrawBrowser(int id)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(RowH));
            GUILayout.Label("REPLAYS", _stTitle, GUILayout.Width(80));
            GUILayout.Label(ReplayRecorder.Folder, _stDim, GUILayout.ExpandWidth(true));
            var rec = Toggle(_cfgRecord.Value, "RECORD MATCHES", Accent, GUILayout.Width(130));
            if (rec != _cfgRecord.Value) _cfgRecord.Value = rec;
            if (GUILayout.Button("REFRESH", _stButton, GUILayout.Width(80))) Rescan();
            if (GUILayout.Button("OPEN FOLDER", _stButton, GUILayout.Width(100)))
            {
                try
                {
                    Directory.CreateDirectory(ReplayRecorder.Folder);
                    Application.OpenURL("file:///" + ReplayRecorder.Folder.Replace('\\', '/'));
                }
                catch (Exception e) { _status = e.Message; }
            }
            if (GUILayout.Button("CLOSE", _stButton, GUILayout.Width(64))) _browserOpen = false;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, _stBody);
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_entries == null || _entries.Count == 0)
            {
                GUILayout.Label("No replays yet. Play a match with recording on and it will show up here.", _stBody);
            }
            else
            {
                foreach (var e in _entries) DrawEntry(e);
            }
            GUILayout.EndScrollView();
        }

        private void DrawEntry(Entry e)
        {
            var box = new GUIStyle { normal = { background = _texRow }, border = new RectOffset(6, 6, 6, 6), padding = new RectOffset(10, 10, 6, 6), margin = new RectOffset(0, 0, 0, 6) };
            GUILayout.BeginVertical(box);
            GUILayout.BeginHorizontal();

            var h = e.Header;
            var when = e.Written.ToString("yyyy-MM-dd HH:mm");
            if (h == null)
            {
                GUILayout.Label($"{when}   {e.Name}", _stEntryTitle, GUILayout.ExpandWidth(true));
                GUILayout.Label("unreadable: " + e.Error, _stDim, GUILayout.Width(300));
            }
            else
            {
                var map = Path.GetFileNameWithoutExtension(h.Map ?? "") ?? "?";
                var length = h.TickCount > 0 ? Clock(h.TickCount) : "?:??";
                var flag = e.Unfinished ? "   unfinished" : "";
                GUILayout.BeginVertical();
                GUILayout.Label($"{map.Replace('_', ' ')}   {length}{flag}", _stEntryTitle);
                var players = h.Players
                    .Where(p => p.Type != "Empty")
                    .Select(p => p.Type == "Observer" ? $"{p.Name} (observer)" : $"{p.Name}  {p.Faction}, team {p.Team}");
                GUILayout.Label($"{when}   {string.Join("   ·   ", players)}", _stEntrySub);
                // A replay is tied to the build it was recorded on; a
                // different one may still play, but say so up front.
                if (!string.IsNullOrEmpty(h.GameVersion) && h.GameVersion != Application.version)
                {
                    var old = GUI.color;
                    GUI.color = new Color(0.95f, 0.75f, 0.3f);
                    GUILayout.Label($"Recorded on game build {h.GameVersion}; this is {Application.version}. It may not play back correctly.", _stEntrySub);
                    GUI.color = old;
                }
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = Accent;
                var watch = GUILayout.Button("WATCH", _stPrimary, GUILayout.Width(80));
                GUI.backgroundColor = oldBg;
                if (watch)
                {
                    _status = ReplayPlayer.Start(e.Path, h.RecorderClientId);
                    if (_status == null) _browserOpen = false;
                }
                GUILayout.EndHorizontal();
                // One seat per human, for opening straight into their view.
                var seats = h.Players.Where(p => p.Type == "Player" && p.ClientId != 255).ToList();
                if (seats.Count > 1)
                {
                    GUILayout.BeginHorizontal();
                    foreach (var p in seats)
                    {
                        if (GUILayout.Button(p.Name, _stButton, GUILayout.Width(110)))
                        {
                            _status = ReplayPlayer.Start(e.Path, p.ClientId);
                            if (_status == null) _browserOpen = false;
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}
