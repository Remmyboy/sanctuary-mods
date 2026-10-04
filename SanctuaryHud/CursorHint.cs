using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The cursor shows what a right-click would do.
    //
    // The game never changes its cursor (mouseCursor.lua's cursor image is a
    // todo), so nothing tells you whether a right-click on that wreck will
    // reclaim it or on that frame will assist or resume building it. The Lua
    // below makes the same decision OnMouseRightUp makes
    // (inputEventsFunctions.lua), from the same inputs, every frame, and the
    // cursor changes to match: a sword for attack-move, a crosshair for
    // attack, a hand for assist, a wrench for repair, a pickaxe for reclaim,
    // a fist for capture, and the pack's plain arrow for a move and anywhere
    // else over the map. The UI and the menus keep the normal cursor. The game sets no cursor of its own, so
    // "normal" is Cursor.SetCursor(null).
    //
    // The art is Kenney's Cursor Pack (CC0, cursors\License.txt), embedded
    // at 64 pixels and scaled to the screen.
    internal static class CursorHint
    {
        internal static ConfigEntry<bool> Enabled;

        // Each order's cursor, and the point on the 64-pixel art that clicks:
        // the sword's tip, the tool's working end, the middle of the rest.
        private static readonly Dictionary<string, (string file, Vector2 hotspot)> Art =
            new Dictionary<string, (string, Vector2)>
            {
                { "pointer", ("pointer_d", new Vector2(16f, 15f)) },
                { "move", ("pointer_d", new Vector2(16f, 15f)) },
                { "rally", ("pointer_d", new Vector2(16f, 15f)) },
                { "attackmove", ("tool_sword_a", new Vector2(4f, 4f)) },
                { "attack", ("target_round_b", new Vector2(28f, 28f)) },
                { "assist", ("hand_open", new Vector2(28f, 30f)) },
                { "repair", ("tool_wrench", new Vector2(10f, 10f)) },
                { "reclaim", ("tool_pickaxe", new Vector2(20f, 20f)) },
                { "capture", ("hand_closed", new Vector2(28f, 28f)) },
            };

        private const float ArtSize = 64f;

        private static int _size;
        private static readonly Dictionary<string, Texture2D> _cursors = new Dictionary<string, Texture2D>();
        private static string _current;
        private static bool _artFailed;
        private static bool _readLogged;

        private static readonly LuaHook Hook = new LuaHook("__SdbHint", "right-click cursor hint", InstallChunk)
        {
            LogInstalls = false,
        };

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "RightClickCursors", false,
                "The cursor shows what a right-click would do there: a sword for attack-move, a crosshair for " +
                "attack, a hand for assist, a wrench for repair (which also resumes building), a pickaxe for " +
                "reclaim, a fist for capture, and a plain arrow for a move and anywhere else over the map. Hold Alt " +
                "and it shows attack-move, as the click would be. The UI and the menus keep the normal cursor.");
        }

        internal static void Shutdown()
        {
            SetCursor(null);
            ClearCursors();
            Hook.Remove();
        }

        /// From Update, every frame.
        internal static void Tick()
        {
            var enabled = Enabled != null && Enabled.Value;
            // A new match's VM starts without it; switched off, it comes out.
            Hook.Tick(enabled);
            // The pause menu and the Mods page keep the normal cursor.
            var want = enabled && InMatch && LuaReady && !_artFailed && !GamePanel.GameMenuOpen();
            string hint = null;
            if (want)
            {
                try { hint = Read(); }
                catch (Exception e)
                {
                    hint = null;
                    if (!_readLogged)
                    {
                        _readLogged = true;
                        _log?.LogWarning($"Right-click cursor: reading the hint failed (logged once): {e.Message}");
                    }
                }
            }
            SetCursor(hint);
        }

        private static string Read()
        {
            if (!Hook.Call("__SdbHint.Poll()")) return null;
            var hint = GetLuaGlobal("__SdbHintOut");
            return string.IsNullOrEmpty(hint) ? null : hint;
        }

        /// Only on a change: the OS cursor is swapped, not redrawn.
        private static void SetCursor(string hint)
        {
            Texture2D cursor = null;
            var hotspot = Vector2.zero;
            if (hint != null && Art.TryGetValue(hint, out var art))
            {
                try
                {
                    cursor = CursorFor(hint, art.file);
                    if (cursor != null) hotspot = art.hotspot * (cursor.width / ArtSize);
                }
                catch (Exception e)
                {
                    _artFailed = true;
                    _log?.LogWarning($"Right-click cursors could not be loaded; the normal cursor stays ({e.Message}).");
                }
            }
            if (cursor == null) hint = null;
            if (hint == _current) return;
            _current = hint;
            if (cursor != null) Cursor.SetCursor(cursor, hotspot, CursorMode.Auto);
            else Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        private static void ClearCursors()
        {
            foreach (var t in _cursors.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _cursors.Clear();
            _current = null;
        }

        /// The cursor for that order at the screen's size, made once.
        private static Texture2D CursorFor(string hint, string file)
        {
            // The OS cursor size tracks the screen: 32 at 1080p.
            var size = Screen.height >= 2000 ? 64 : Screen.height >= 1400 ? 48 : 32;
            if (size != _size)
            {
                // The current one is about to be destroyed; let go of it first.
                if (_current != null) Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                ClearCursors();
                _size = size;
            }
            if (_cursors.TryGetValue(hint, out var made)) return made;
            var art = Load(file);
            if (art == null) return null;
            try { made = Scale(art, size); }
            finally { UnityEngine.Object.Destroy(art); }
            _cursors[hint] = made;
            return made;
        }

        /// Decodes an embedded PNG into a texture the caller destroys once
        /// it has been scaled.
        private static Texture2D Load(string file)
        {
            byte[] bytes;
            using (var stream = typeof(CursorHint).Assembly.GetManifestResourceStream("SanctuaryHud.cursors." + file + ".png"))
            {
                if (stream == null) throw new InvalidOperationException($"cursor '{file}' is not embedded");
                bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "cursor " + file };
            if (!Generated.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidOperationException($"cursor '{file}' did not decode");
            }
            return texture;
        }

        /// A size-by-size copy, box-filtered on premultiplied colour so the
        /// outline stays clean; SetCursor needs it readable.
        private static Texture2D Scale(Texture2D source, int size)
        {
            var sw = source.width;
            var sh = source.height;
            var src = source.GetPixels();
            var dst = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float x0 = x * (float)sw / size, x1 = (x + 1) * (float)sw / size;
                float y0 = y * (float)sh / size, y1 = (y + 1) * (float)sh / size;
                float r = 0f, g = 0f, b = 0f, a = 0f, weight = 0f;
                for (var sy = (int)y0; sy < Mathf.CeilToInt(y1) && sy < sh; sy++)
                for (var sx = (int)x0; sx < Mathf.CeilToInt(x1) && sx < sw; sx++)
                {
                    var w = (Mathf.Min(x1, sx + 1) - Mathf.Max(x0, sx)) * (Mathf.Min(y1, sy + 1) - Mathf.Max(y0, sy));
                    if (w <= 0f) continue;
                    var p = src[sy * sw + sx];
                    r += p.r * p.a * w;
                    g += p.g * p.a * w;
                    b += p.b * p.a * w;
                    a += p.a * w;
                    weight += w;
                }
                dst[y * size + x] = a > 0f ? new Color(r / a, g / a, b / a, a / weight) : Color.clear;
            }
            var texture = Generated.Keep(new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            });
            texture.SetPixels(dst);
            texture.Apply(false, false);
            return texture;
        }

        // OnMouseRightUp's decision, kept in step with it, returning what the
        // click would issue ('' for nothing). Module functions are reached
        // through their tables; see sanctuary-lua-module-scoping.
        private const string InstallChunk = @"
local MC = Import('client/input/mouseCursor.lua')
local BM = Import('client/input/buildmodeTemp.lua')
local SS = Import('client/input/selectionSystem.lua')
local IS = Import('client/input/inputSystem.lua')
local IEF = Import('client/inputEventsFunctions.lua')
local builders = Tags.ENGINEER + Tags.COMMAND + Tags.ENGINEERING_STATION
local function decide()
  if IsObserver() then return '' end
  if BM.GetBuildMode() or SS.SelectionBox:IsActive() then return '' end
  local sel = SS.GetSelectedUnits()
  local _, first = next(sel)
  if not first or first.armyId ~= GetFocusArmy() then return '' end
  local hover = IEF.GetHoverUnit()
  if hover == nil then
    if IS.IsRawKeyPressed('Alt') then return 'attackmove' end
    local mobile, factory = false, false
    for _, u in pairs(sel) do
      if u.tp and u.tp.movement then mobile = true
      elseif u.tp and Tags.FACTORY[u.tp.general.tpId] then factory = true end
    end
    if mobile then return 'move' end
    if factory then return 'rally' end
    return ''
  end
  if hover.entityType == 'prop' then
    if first:HasTags(builders) then return 'reclaim' end
    return ''
  end
  if hover.entityType ~= 'unit' then return '' end
  local ally = hover.army:IsAlly(first.army)
  local enemy = hover.army:IsEnemy(first.army)
  if first:HasTags(builders) then
    if ally then
      if not hover:IsCompleted() and not hover:IsUpgrading() then return 'repair' end
      if (hover:GetHealth() < hover:GetMaxHealth() and not hover:IsUpgrading())
        or (hover.captureProgress or 0) > 0 or (hover.reclaimProgress or 0) > 0 then return 'repair' end
      return 'assist'
    elseif enemy then
      if first:HasTags(Tags.COMMAND) then return 'attack' end
      if not hover:IsCompleted() then return 'reclaim' end
      return 'capture'
    end
    return ''
  end
  if hover:HasTags(Tags.STRUCTURE * Tags.FACTORY) and first:HasTags(Tags.STRUCTURE * Tags.FACTORY) then
    return ally and 'assist' or ''
  end
  if ally then return 'assist' end
  if enemy then return 'attack' end
  return ''
end
-- Over the map the cursor is always one of ours: the plain arrow where a
-- right-click would do nothing. Off it (the UI, the menus) it is left alone.
local function hint()
  if not MC.IsCursorInGameView() or Engine.IsMouseOverUI() then return '' end
  local h = decide()
  if h == '' then return 'pointer' end
  return h
end
-- Called every frame; the answer is left in a global for the plugin to read.
__SdbHint = {
  Poll = function()
    local ok, r = pcall(hint)
    __SdbHintOut = ok and r or ''
  end,
}";
    }
}
