using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.BuildSpacing
{
    // Spaces out buildings dragged in a line. With a building picked to
    // place, Shift + scroll up adds a tile of gap between each one and the
    // next, scroll down takes one away, down to the game's own touching
    // rows. The gap is one number for every building and every engineer,
    // kept between matches. Buildings whose death explosion would reach the
    // next one in the line (power generators, storage) get at least the gap
    // that keeps it out, so one going up doesn't take the row with it.
    //
    // The drag line is constructionPreviewSystem.lua's
    // GetBuildingPlacementPositions: a Bresenham walk from where the drag
    // started to the cursor that drops a building each time it has moved a
    // footprint (skirtSize) along. The wrapper is the same walk with the gap
    // added to the footprint, and the game's own function when the gap is
    // 0. Both the ghosts (UpdateConstructionPreview) and the order
    // (OnMouseLeftUp) go through the module table, so both see it.
    //
    // Shift + scroll is the camera's zoom (the cameraZoom action takes any
    // modifier), which looks up cameraController.SetZoomAmount at each
    // event; the wrapper keeps the event in build mode with Shift held.
    // Plain scroll still zooms.
    [BepInPlugin("com.sanctuarydb.buildspacing", "Build Spacing", "0.1.0")]
    public class BuildSpacingPlugin : BaseUnityPlugin
    {
        private const int MaxSpacing = 20;
        /// Scroll that counts as one step. The game's Input System (1.20)
        /// reports a wheel notch as 1 by default, which the camera's own
        /// zoom step (0.066 × 10 per step) also assumes.
        private const float Notch = 0.5f;
        private const float PopupSeconds = 1.4f, PopupFade = 0.35f;

        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<int> _cfgSpacing;
        private ConfigEntry<bool> _cfgBlast;

        private LuaHook _hook;
        /// What was last sent into the VM ("spacing|blast"); null after an install.
        private string _pushed;
        private string _lastEvent;

        private RectTransform _plate;
        private CanvasGroup _group;
        private TMP_Text _text;
        private float _shownAt = -100f;

        private void Awake()
        {
            HudCore._log ??= Logger;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Shift + mouse wheel while placing a building changes the gap between buildings dragged in a line.");
            _cfgSpacing = Config.Bind("General", "Spacing", 0,
                new ConfigDescription(
                    "Empty tiles left between buildings dragged in a line. 0 is the game's own touching rows. Shift + scroll up " +
                    "while placing a building adds one, Shift + scroll down takes one away; the last value is kept for every " +
                    "building and every match.",
                    new AcceptableValueRange<int>(0, MaxSpacing)));
            _cfgBlast = Config.Bind("General", "BlastSpacing", true,
                "Buildings that explode when destroyed (power generators, storage) always get at least the gap that keeps " +
                "their blast off the next one in the line, whatever Spacing is.");

            _hook = new LuaHook("__BldSpc", "build spacing", InstallChunk)
            {
                Installed = () =>
                {
                    _pushed = null;
                    _lastEvent = GetLuaGlobal("__BldSpcEvent");
                },
            };

            Logger.LogInfo("Build Spacing loaded: Shift + scroll while placing a building spaces out the line.");
        }

        private void OnDestroy()
        {
            _hook?.Remove();
            if (_plate != null) Destroy(_plate.gameObject);
            _plate = null;
        }

        private void Update()
        {
            var enabled = _cfgEnabled.Value;
            _hook.Tick(enabled);
            if (enabled && _hook.Live)
            {
                Push();
                Poll();
            }
            UpdatePopup(enabled && _hook.Live);
        }

        /// The settings into the VM: after an install, and after a change on
        /// the Mods page.
        private void Push()
        {
            var spacing = Mathf.Clamp(_cfgSpacing.Value, 0, MaxSpacing);
            var want = spacing.ToString(CultureInfo.InvariantCulture) + "|" + (_cfgBlast.Value ? "true" : "false");
            if (want == _pushed) return;
            if (_hook.Call($"__BldSpc.Set({spacing}, {(_cfgBlast.Value ? "true" : "false")})")) _pushed = want;
        }

        /// A scroll in build mode leaves "seq:spacing:effective" in
        /// __BldSpcEvent: the new spacing is saved, and the popup says what
        /// this building now gets.
        private void Poll()
        {
            var ev = GetLuaGlobal("__BldSpcEvent");
            if (ev == null || ev == _lastEvent) return;
            _lastEvent = ev;
            var parts = ev.Split(':');
            if (parts.Length < 3
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var spacing)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var effective))
                return;
            if (_cfgSpacing.Value != spacing) _cfgSpacing.Value = spacing;
            _pushed = spacing.ToString(CultureInfo.InvariantCulture) + "|" + (_cfgBlast.Value ? "true" : "false");
            ShowPopup(spacing, effective);
        }

        // ---- the popup -------------------------------------------------------------

        private static string Tiles(int n) => n == 1 ? "1 tile" : n + " tiles";

        private void ShowPopup(int spacing, int effective)
        {
            var root = HudCanvas.Ensure();
            if (root == null) return;
            if (_plate == null) Build(root);
            var line = spacing == 0 ? "Spacing: none" : "Spacing: " + Tiles(spacing);
            if (effective > spacing) line += $"  <alpha=#99>({Tiles(effective)} for its blast)";
            HudCanvas.SetText(_text, line);
            _shownAt = Time.unscaledTime;
        }

        private void Build(RectTransform root)
        {
            _plate = HudCanvas.Plate(root, "Build spacing");
            // Under the cursor: it must never take a click meant for the map.
            _plate.GetComponent<Image>().raycastTarget = false;
            _plate.anchorMin = _plate.anchorMax = new Vector2(0.5f, 0.5f);
            _plate.pivot = new Vector2(0f, 1f);
            _group = _plate.gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;
            var layout = _plate.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 10, 12);
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            HudCanvas.FitToContents(_plate);
            _text = HudCanvas.Text(_plate, "Text", 24f, Color.white, TextAlignmentOptions.MidlineLeft);
            _text.richText = true;
            _plate.gameObject.SetActive(false);
        }

        /// Beside the cursor while it lasts, then fades.
        private void UpdatePopup(bool live)
        {
            if (_plate == null) return;
            var age = Time.unscaledTime - _shownAt;
            var show = live && age < PopupSeconds + PopupFade;
            if (_plate.gameObject.activeSelf != show) _plate.gameObject.SetActive(show);
            if (!show) return;

            _group.alpha = age < PopupSeconds ? 1f : 1f - (age - PopupSeconds) / PopupFade;
            var canvas = _plate.GetComponentInParent<Canvas>();
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var parent = (RectTransform)_plate.parent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Input.mousePosition, cam, out var local))
            {
                // Down and right of the pointer, clear of the arrow.
                var u = HudCanvas.UnitsPerLogical;
                _plate.anchoredPosition = local + new Vector2(22f * u, -22f * u);
            }
        }

        // ---- the Lua ---------------------------------------------------------------

        private static readonly string InstallChunk = @"
do
  local CPS = Import('client/input/constructionPreviewSystem.lua')
  local BM = Import('client/input/buildmodeTemp.lua')
  local CC = Import('client/input/cameraController.lua')
  local IS = Import('client/input/inputSystem.lua')
  local MC = Import('client/input/mouseCursor.lua')
  local PU = Import('common/systems/placementUtils.lua')
  if type(CPS.GetBuildingPlacementPositions) ~= 'function' or type(CPS.CanPlaceAtArea) ~= 'function'
    or type(CC.SetZoomAmount) ~= 'function' or type(IS.IsRawKeyPressed) ~= 'function' then
    error('BuildSpacing: the game no longer has the functions this mod wraps')
  end

  local S = {
    spacing = 0, blast = true, acc = 0,
    seq = tonumber(string.match(__BldSpcEvent or '', '^(%d+)')) or 0,
    orig = { place = CPS.GetBuildingPlacementPositions, zoom = CC.SetZoomAmount },
    blastGaps = {},
  }

  -- The gap that keeps the next building of the same kind in a line out of
  -- this one's death explosion: the explosion is a capsule of radius r round
  -- the centre (host collisionUpdate.ProcessAreaDamage) and hits the
  -- neighbour's box collider, whose near face is centre-to-centre
  -- (footprint + gap) less half the box. 0 for buildings that don't explode
  -- on their own side.
  function S.BlastGap(tpId)
    local cached = S.blastGaps[tpId]
    if cached then return cached end
    local tp = __Templates.Units[tpId]
    local gap = 0
    if tp and tp.skirtSize then
      local size = math.min(tp.skirtSize.x, tp.skirtSize.y)
      local box = tp.collisionInfo and tp.collisionInfo.collisionSize
      local half = box and math.max(box.x or 0, box.z or 0) / 2 or size / 2
      for _, w in ipairs(tp.weapons or {}) do
        if w.category == 'DeathExplosion' and w.damageFriendly and (w.damageRadius or 0) > 0 and (w.damage or 0) > 0 then
          gap = math.max(gap, math.floor(w.damageRadius + half - size) + 1)
        end
      end
    end
    S.blastGaps[tpId] = gap
    return gap
  end

  function S.Gap(tpId)
    if not tpId or tpId == '' then return S.spacing end
    if S.blast then return math.max(S.spacing, S.BlastGap(tpId)) end
    return S.spacing
  end

  -- From the plugin: the saved spacing and switch, without a popup.
  function S.Set(spacing, blast)
    S.spacing = spacing
    S.blast = blast
  end

  local function walk(x0, z0, x1, z1, stepX, stepZ, positions)
    x0 = math.truncateToInt(x0)
    x1 = math.truncateToInt(x1)
    z0 = math.truncateToInt(z0)
    z1 = math.truncateToInt(z1)
    local dx = math.abs(x1 - x0)
    local sx = x0 < x1 and 1 or -1
    local dz = math.abs(z1 - z0)
    local sz = z0 < z1 and 1 or -1
    local err = dx - dz
    local lastX, lastZ = x0, z0
    table.insert(positions, { x = x0, z = z0 })
    while x0 ~= x1 or z0 ~= z1 do
      local e2 = 2 * err
      if e2 > -dz then
        err = err - dz
        x0 = x0 + sx
      end
      if e2 < dx then
        err = err + dx
        z0 = z0 + sz
      end
      if math.abs(x0 - lastX) >= stepX or math.abs(z0 - lastZ) >= stepZ then
        table.insert(positions, { x = x0, z = z0 })
        lastX, lastZ = x0, z0
      end
    end
  end

  -- The game's GetBuildingPlacementPositions with the gap added to each
  -- step of the line; the game's own when there is no gap.
  CPS.GetBuildingPlacementPositions = function(includeInvalid, ...)
    local tpId = BM.GetBuildTpId()
    local tp = __Templates.Units[tpId]
    local gap = tp and S.Gap(tpId) or 0
    if gap <= 0 or Tags.ALLOYS_EXTRACTION[tpId] then return S.orig.place(includeInvalid, ...) end

    local start = BM.GetStartPosition()
    if not start then
      start = MC.GetMousePosition()
      start.x = math.truncateToInt(start.x)
      start.z = math.truncateToInt(start.z)
    end
    local finish = MC.GetMousePosition()
    local points = {}
    walk(start.x, start.z, finish.x, finish.z, tp.skirtSize.x + gap, tp.skirtSize.y + gap, points)

    local out = {}
    local mapX, mapY = GameInfo.MapInfo.size[1], GameInfo.MapInfo.size[2]
    local domain = PU.GetPlacementDomain(tp)
    for _, p in ipairs(points) do
      local minX, minY, maxX, maxY = PU.GetPlacementBounds(p, tp.skirtSize)
      if minX >= 0 and minY >= 0 and maxX <= mapX and maxY <= mapY then
        if includeInvalid or CPS.CanPlaceAtArea(minX, minY, maxX, maxY, domain, false) then
          table.insert(out, EngineClasses.float3(p.x, PU.GetPlacementHeight(tp, p.x, p.z), p.z))
        end
      end
    end
    return out
  end

  -- Shift + scroll while placing: one notch, one tile. A notch is one
  -- event; a smooth-scrolling wheel's smaller events add up to one.
  CC.SetZoomAmount = function(value, ...)
    if type(value) == 'number' and BM.GetBuildMode()
      and (IS.IsRawKeyPressed('LeftShift') or IS.IsRawKeyPressed('RightShift')) then
      if value == 0 then return end
      if (value > 0) ~= (S.acc > 0) then S.acc = 0 end
      S.acc = S.acc + value
      local step = 0
      if S.acc >= __NOTCH__ then step = 1 elseif S.acc <= -__NOTCH__ then step = -1 end
      if step ~= 0 then
        S.acc = 0
        S.spacing = math.max(0, math.min(__MAX__, S.spacing + step))
        S.seq = S.seq + 1
        __BldSpcEvent = S.seq .. ':' .. S.spacing .. ':' .. S.Gap(BM.GetBuildTpId())
      end
      return
    end
    return S.orig.zoom(value, ...)
  end

  S.mine = { place = CPS.GetBuildingPlacementPositions, zoom = CC.SetZoomAmount }
  S.Remove = function()
    if CPS.GetBuildingPlacementPositions == S.mine.place then CPS.GetBuildingPlacementPositions = S.orig.place end
    if CC.SetZoomAmount == S.mine.zoom then CC.SetZoomAmount = S.orig.zoom end
    __BldSpc = nil
  end
  __BldSpc = S
end"
            .Replace("__NOTCH__", Notch.ToString(CultureInfo.InvariantCulture))
            .Replace("__MAX__", MaxSpacing.ToString(CultureInfo.InvariantCulture));
    }
}
