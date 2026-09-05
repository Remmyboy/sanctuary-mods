using System.Globalization;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud.CameraUtils
{
    /// What to do with strategic icons.
    internal enum IconMode
    {
        /// Leave them to the game.
        Show = 0,
        /// Hide them while the camera is closer to the ground than
        /// `HideIconsBelow` — the rule the game's own rendering.lua has a
        /// TODO for, and the one that matters for cinematics.
        HideWhenClose = 1,
        /// Never draw them.
        Hide = 2,
    }

    // The whole mod bar the panel: a small agent installed into the client's
    // own Lua VM, plus the C# state it mirrors.
    //
    // Everything here is presentation-side. Icons, range rings, progress bars
    // and the UI HUD are client-only rendering flags with engine setters the
    // client's Lua already calls; no simulation state is touched and no hashed
    // file changes, so a client running this stays lobby-compatible.
    //
    // Why an agent rather than a chunk per frame: the flags have to be
    // re-asserted (the game rewrites the icon flag every render update, and
    // units spawn with their rings on), and pushing a chunk across the FFI at
    // frame rate to do that would be silly. So one chunk installs a wrapper
    // around the client's `rendering.RenderUpdate`, and C# only pushes the
    // wanted state when it actually changes.
    internal static class RenderState
    {
        // ---- wanted state, driven by the config entries and the panel ----
        internal static IconMode Icons;
        internal static float HideIconsBelow = 100f;
        internal static bool HideIntel;
        internal static bool HideAttack;
        internal static bool HideBuild;
        internal static bool HideHealthBars;
        internal static bool HideGameUi;

        /// Camera height read back out of Lua, so the panel can show what the
        /// icon threshold is being compared against. -1 until one arrives.
        internal static float CameraHeight = -1f;

        /// True while the agent is live in the client VM — so, in a match or a
        /// replay. The panel hides when it isn't.
        internal static bool Active => _installed;

        private static bool _installed;
        private static string _pushed;
        private static float _accum;
        private static string _lastErr;

        // No version constant to remember to bump: the version global carries
        // a hash of the install chunk, so editing the chunk re-installs it
        // over a hot reload instead of leaving the last build's agent running.
        private static readonly string Version = "cu" + InstallTemplate.GetHashCode().ToString("x8");
        private static string InstallChunk => InstallTemplate.Replace("@VER@", Version);

        internal static void Reset()
        {
            _installed = false;
            _pushed = null;
            CameraHeight = -1f;
        }

        internal static void Poll(float dt, BepInEx.Logging.ManualLogSource log)
        {
            EnsureLuaBridge();
            // No client VM: the menu, loading, or a finished match. Calling
            // into Lua here would dereference a null state natively.
            if (!LuaReady)
            {
                if (_installed) Reset();
                return;
            }

            // Installing and reading back are on a quarter second; a change of
            // state is not, so a click in the panel reads as instant.
            _accum += dt;
            var due = _accum >= 0.25f;
            if (due) _accum = 0f;

            if (!_installed)
            {
                if (!due) return;
                // Every match brings a fresh Lua state, so the agent has to go
                // back in; a hot reload of this DLL finds it already there.
                if (GetLuaGlobal(VersionGlobal) != Version)
                {
                    RunLua(InstallChunk);
                    _pushed = null;
                }
                _installed = GetLuaGlobal(VersionGlobal) == Version;
                if (!_installed) return;
                log?.LogInfo("Camera Utilities: render agent installed in the client VM.");
            }

            var want = StateChunk();
            if (want != _pushed && RunLua(want)) _pushed = want;

            if (!due) return;

            var height = GetLuaGlobal(HeightGlobal);
            if (height != null && float.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            {
                CameraHeight = h;
            }

            var err = GetLuaGlobal(ErrorGlobal);
            if (!string.IsNullOrEmpty(err) && err != _lastErr)
            {
                _lastErr = err;
                log?.LogWarning($"Camera Utilities: the Lua agent reports: {err}");
            }
        }

        /// Puts every flag back and takes the wrapper off `RenderUpdate`, so
        /// unloading the mod (or switching it off on the Mods page) leaves the
        /// client exactly as the game had it.
        internal static void Uninstall()
        {
            if (LuaReady) RunLua("pcall(function() if __CameraUtils then __CameraUtils.restore() end end)");
            Reset();
        }

        // ---- the Lua side --------------------------------------------------

        private const string VersionGlobal = "__CameraUtilsVersion";
        private const string HeightGlobal = "__CameraUtilsHeight";
        private const string ErrorGlobal = "__CameraUtilsErr";

        private static string Lua(bool value) => value ? "true" : "false";

        private static string StateChunk()
        {
            var inv = CultureInfo.InvariantCulture;
            return "if __CameraUtils then local S = __CameraUtils " +
                   $"S.icons = {(int)Icons} S.height = {HideIconsBelow.ToString("0.##", inv)} " +
                   $"S.intel = {Lua(HideIntel)} S.attack = {Lua(HideAttack)} S.build = {Lua(HideBuild)} " +
                   $"S.bars = {Lua(HideHealthBars)} S.ui = {Lua(HideGameUi)} " +
                   // Apply straight away rather than waiting for the next
                   // sweep, so a click in the panel reads as instant.
                   "pcall(S.sweep) end";
        }

        // Installed once per match. `S` is the shared state C# writes into,
        // and the wrapper around rendering.RenderUpdate re-asserts it.
        //
        // - Icons: rendering.UpdateIcons calls Engine.SetIconsRenderingEnabled
        //   every render update from two flags of its own, one of which the
        //   camera controller drives while you hold rotate — so the only
        //   stable place to override it is after that call, hence the wrapper
        //   rather than a flag of theirs we could set.
        // - Range rings: every unit carries one ring per material, and the
        //   per-ring enable is a flag the game itself never writes (it only
        //   moves the per-unit master, on intel and selection changes, which
        //   ANDs with ours). So the sweep only has to catch units as they
        //   appear, and skips any unit already at the wanted mask.
        // - Health bars: the per-unit master is rewritten every tick by
        //   ClientUnit:UpdateProgressBars, so the global scale — where 0 means
        //   "do not render" — is the one that sticks.
        // - Game UI: Engine.ToggleUIHUD has no getter, so we keep our own
        //   belief of it and only ever toggle on a change.
        private const string InstallTemplate =
            "local ok, err = pcall(function() " +
            "  local S = _G.__CameraUtils " +
            "  if not S then " +
            "    S = { icons = 0, height = 100, intel = false, attack = false, build = false, bars = false, ui = false } " +
            "    _G.__CameraUtils = S " +
            "  end " +
            "  local M = RangeRingMaterial " +
            "  local INTEL = { [M.IntelRadar] = 1, [M.IntelSonar] = 1, [M.IntelOmni] = 1, [M.IntelCounter] = 1, " +
            "                  [M.Vision] = 1, [M.WaterVision] = 1, [M.FogOfWar] = 1 } " +
            "  local ATTACK = { [M.AttackDirect] = 1, [M.AttackIndirect] = 1, [M.AttackAntiAir] = 1, " +
            "                   [M.AttackAntiNavy] = 1, [M.AttackCounter] = 1 } " +
            "  local BUILD = { [M.Build] = 1, [M.Assist] = 1 } " +
            // Weak keys, so a dead unit takes its entry with it.
            "  S.applied = S.applied or setmetatable({}, { __mode = 'k' }) " +
            // Both of these are compared against the wanted flag to decide
            // whether to act, so they have to start at "not hidden" — left nil
            // the first sweep reads as a change and toggles the UI off, and
            // overwrites the bar scale, with nothing having been asked for.
            "  if S.uiHidden == nil then S.uiHidden = false end " +
            "  if S.barsHidden == nil then S.barsHidden = false end " +
            "  S.sweep = function() " +
            "    local mask = (S.intel and 1 or 0) + (S.attack and 2 or 0) + (S.build and 4 or 0) " +
            "    for _, u in pairs(__Entities.Units) do " +
            "      local rings = u.rangeRings " +
            "      local was = S.applied[u] " +
            "      if rings and was ~= mask then " +
            // Nothing to hide and never touched: leave the unit alone rather
            // than writing back the state it already has.
            "        if mask ~= 0 or was ~= nil then " +
            "          for material, ring in pairs(rings) do " +
            "            if ring.SetEnabled then " +
            "              local hide = (INTEL[material] and S.intel) or (ATTACK[material] and S.attack) " +
            "                        or (BUILD[material] and S.build) " +
            "              ring:SetEnabled(not hide) " +
            "            end " +
            "          end " +
            "        end " +
            "        S.applied[u] = mask " +
            "      end " +
            "    end " +
            "    if S.bars ~= S.barsHidden then " +
            "      if S.bars then " +
            "        if not S.barScale then local _, v = Engine.GetProgressBarScaling() S.barScale = v end " +
            "        Engine.SetProgressBarScaling(0) " +
            "      else " +
            "        Engine.SetProgressBarScaling(S.barScale or 1) " +
            "      end " +
            "      S.barsHidden = S.bars " +
            "    end " +
            "    if S.ui ~= S.uiHidden then Engine.ToggleUIHUD() S.uiHidden = S.ui end " +
            "    _G." + HeightGlobal + " = string.format('%.0f', Engine.GetCameraWorldSpaceHeight()) " +
            "  end " +
            "  local tick = function() " +
            "    if S.icons == 2 then " +
            "      Engine.SetIconsRenderingEnabled(false) " +
            "    elseif S.icons == 1 then " +
            "      Engine.SetIconsRenderingEnabled(Engine.GetCameraWorldSpaceHeight() >= S.height) " +
            "    end " +
            // The sweep walks every unit, so it runs on a quarter second; the
            // icon check above is per frame because it is two engine calls and
            // has to follow the camera without lagging it.
            "    S.frames = (S.frames or 0) + 1 " +
            "    if S.frames >= 15 then S.frames = 0 S.sweep() end " +
            "  end " +
            "  local rendering = Import('client/rendering/rendering.lua') " +
            "  S.origRenderUpdate = S.origRenderUpdate or rendering.RenderUpdate " +
            "  local orig = S.origRenderUpdate " +
            "  rendering.RenderUpdate = function(...) " +
            "    orig(...) " +
            "    local ok2, err2 = pcall(tick) " +
            "    if not ok2 then _G." + ErrorGlobal + " = tostring(err2) end " +
            "  end " +
            "  S.restore = function() " +
            "    pcall(function() Import('client/rendering/rendering.lua').RenderUpdate = S.origRenderUpdate end) " +
            "    S.icons = 0 S.intel = false S.attack = false S.build = false S.bars = false S.ui = false " +
            "    pcall(S.sweep) " +
            "    _G.__CameraUtils = nil " +
            "    _G." + VersionGlobal + " = nil " +
            "  end " +
            "  _G." + ErrorGlobal + " = '' " +
            "end) " +
            "if ok then _G." + VersionGlobal + " = '@VER@' " +
            "else Warn('CameraUtilities: agent install failed: ' .. tostring(err)) end";
    }
}
