using System;
using System.Globalization;
using BepInEx.Configuration;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Assisting an idle extractor starts its upgrade.
    //
    // Ordering an engineer to assist a structure with nothing queued is a
    // no-op today — the engineer walks over and stands there — so the natural
    // gesture for "help this extractor along" does nothing. This makes that
    // gesture queue the upgrade first, then issue the assist exactly as the
    // game would, so the engineer arrives to real work and keeps its order.
    //
    // Unlike the rest of these mods this one *acts*: it queues a build item.
    // It does so through the game's own client path — the same
    // ModifyBuildQueue prediction plus UpdateQueueAmount command that the
    // construction panel sends when you click the upgrade button — so the host
    // validates and replicates it like any other order. No files change, so
    // the lobby's Lua hash is untouched and this stays MP-compatible; what it
    // costs you is that an assist click now spends alloy. Set
    // `AssistStartsUpgrade` false to turn it off.
    //
    // The hook itself is a runtime wrapper around the client's global
    // IssueAssistOrder. inputActions.lua binds the key to
    // `Import("client/inputEventsFunctions.lua").IssueAssistOrder()`, looked up
    // at press time, so replacing that field (and the global, for the two
    // internal callers) intercepts every assist without touching a file.
    //
    // `AssistPausesUpgrade` then holds each of those upgrades paused until an
    // engineer actually turns up — see the Lua below for why that is worth the
    // machinery.
    public partial class EcoManagerPlugin
    {
        private ConfigEntry<bool> _cfgAssistStartsUpgrade;
        private ConfigEntry<bool> _cfgAssistPauses;
        private ConfigEntry<float> _cfgAssistPauseDelay;
        private ConfigEntry<float> _cfgAssistPauseRadius;

        private bool _assistHookInstalled;
        private string _assistSignature;
        private float _installAccum;
        private float _tickAccum;
        private int _upgradesQueued;

        // Guarded by a global inside the VM, so re-running it is harmless —
        // which is what makes retrying safe. Each match builds a fresh Lua
        // state, so the flag (and the hook) go away with the old one.
        private const string InstallChunk =
            "if not __SdbAssistUpgrade then " +
            "  __SdbAssistUpgrade = true " +
            "  __SdbAssistPause = __PAUSE__ " +
            "  __SdbAssistPauseDelay = __DELAY__ " +
            "  __SdbAssistPauseRadius = __RADIUS__ " +
            "  __SdbAssistPendingList = {} " +
            "  local m = Import('client/inputEventsFunctions.lua') " +
            "  local sel = Import('client/input/selectionSystem.lua') " +
            "  local orig = m.IssueAssistOrder " +
            "  __SdbAssistUpgradeOrig = orig " +
            "  local wrapped = function(...) " +
            "    local ok, err = pcall(function() " +
            // GetHoverUnit is a global *of that module's environment table*,
            // not of _G — Import gives every file its own env (with _G only as
            // an __index fallback) and the file's globals land there. This
            // chunk runs in _G, so it has to go through the module table.
            "      local hover = m.GetHoverUnit and m.GetHoverUnit() " +
            "      if not (hover and hover.id and hover.tpId) then return end " +
            // Ours only. Armies is a real _G global and each army's `units` is
            // keyed by global id index, so this needs nothing module-scoped.
            "      local mine = false " +
            "      for _, a in pairs(Armies or {}) do " +
            "        if a.focused and a.units and a.units[hover.id.index] then mine = true end " +
            "      end " +
            "      if not mine then return end " +
            // Extractors only. Factories upgrade too, and silently spending a
            // fortune because someone assisted one would be a nasty surprise.
            "      if not (Tags and Tags.ALLOYS_EXTRACTION and Tags.ALLOYS_EXTRACTION[hover.tpId]) then return end " +
            "      local up = hover.tp and hover.tp.construction and hover.tp.construction.upgradesTo " +
            "      if not up or up == '' then return end " +
            // Half-built, or already upgrading: leave it alone.
            "      if hover.IsCompleted and not hover:IsCompleted() then return end " +
            "      if hover.IsUpgradeQueued and hover:IsUpgradeQueued() then return end " +
            // Exactly what constructionPanel.lua does for an upgrade click:
            // predict locally, record the pending op, tell the host.
            "      local itemId = buildQueueUtils.ModifyBuildQueue(hover, -1, up, 1, true) " +
            "      if not itemId then return end " +
            "      hover.buildQueuePendingOperations = hover.buildQueuePendingOperations or {} " +
            "      table.insert(hover.buildQueuePendingOperations, " +
            "        { deltaAmount = 1, queueItemId = itemId, tpID = up }) " +
            // Since the 2026-09-04 update commands live in a registry; this
            // is the same call constructionPanel.lua makes for an upgrade click.
            "      Import('common/commands/definitions/buildQueue.lua')" +
            ".RequestQueueAmount.Send({ hover.id }, { itemId }, up, 1) " +
            // Remember who was told to go and help, so the tick below knows
            // which engineers arriving should release the pause.
            "      if __SdbAssistPause then " +
            "        local engs = {} " +
            "        local picked = (sel.GetSelectedUnits and sel.GetSelectedUnits()) " +
            "          or (sel.GetSelectedEntities and sel.GetSelectedEntities()) or {} " +
            "        for _, e in pairs(picked) do " +
            "          if e ~= hover and e.tp and e.tp.construction then table.insert(engs, e) end " +
            "        end " +
            "        local now = (os and os.clock) and os.clock() or 0 " +
            "        table.insert(__SdbAssistPendingList, " +
            "          { u = hover, engs = engs, due = now + __SdbAssistPauseDelay, paused = false }) " +
            "      end " +
            // Lets the C# side confirm the hook is actually firing; the Lua
            // log is not much use for that from here.
            "      __SdbAssistUpgradeCount = (__SdbAssistUpgradeCount or 0) + 1 " +
            "    end) " +
            "    if not ok then Warn('SanctuaryHud assist-upgrade: ' .. tostring(err)) end " +
            // The assist itself always goes through untouched, upgrade or not.
            "    return orig(...) " +
            "  end " +
            "  m.IssueAssistOrder = wrapped " +
            "  IssueAssistOrder = wrapped " +

            // Queue five engineers onto five extractors and all five upgrades
            // start at once, which drains the economy flat. Pausing each one
            // the moment it starts, then releasing it when its engineer
            // actually arrives, spreads that cost over the walk instead.
            //
            // The pause has to lag the queueing: the upgrade is not registered
            // as in progress on the same frame it is requested, and pausing
            // before then does nothing. So each entry waits out its delay
            // first, and is dropped if the upgrade never took.
            "  function __SdbAssistToggle(unit, on) " +
            "    Import('common/commands/definitions/toggles.lua').RequestUnitsToggle.Send( " +
            "      { unit.id }, Import('common/toggles.lua').ToggleNameToToggleType('Pause'), on) " +
            "  end " +

            "  function __SdbAssistTick() " +
            "    local list = __SdbAssistPendingList " +
            "    if not list or #list == 0 then return end " +
            "    local now = (os and os.clock) and os.clock() or 0 " +
            "    for i = #list, 1, -1 do " +
            "      local e = list[i] " +
            "      local u = e.u " +
            "      local drop = false " +
            "      if not (u and u.id) then " +
            "        drop = true " +
            "      elseif not e.paused then " +
            "        if now >= e.due then " +
            "          local upgrading = u.IsUpgradeQueued and u:IsUpgradeQueued() " +
            "          local pauseable = u.HasToggle and u:HasToggle('Pause') " +
            "          if upgrading and pauseable then " +
            "            __SdbAssistToggle(u, true) " +
            "            e.paused = true " +
            "          else " +
            "            drop = true " +
            "          end " +
            "        end " +
            "      elseif not (u.IsUpgradeQueued and u:IsUpgradeQueued()) then " +
            // Finished or cancelled while we held it: let go either way, so a
            // cancelled upgrade never strands a paused extractor.
            "        __SdbAssistToggle(u, false) " +
            "        drop = true " +
            "      else " +
            "        local p = u.GetPosition and u:GetPosition() " +
            "        if p then " +
            "          for _, eng in pairs(e.engs) do " +
            "            local q = eng.GetPosition and eng:GetPosition() " +
            "            if q then " +
            "              local reach = (eng.tp and eng.tp.construction and eng.tp.construction.range or 5) " +
            "                + __SdbAssistPauseRadius " +
            "              local dx, dz = p.x - q.x, p.z - q.z " +
            "              if dx * dx + dz * dz <= reach * reach then " +
            "                __SdbAssistToggle(u, false) " +
            "                drop = true " +
            "                break " +
            "              end " +
            "            end " +
            "          end " +
            "        end " +
            "      end " +
            "      if drop then table.remove(list, i) end " +
            "    end " +
            "  end " +
            "end";

        // Puts the client's own IssueAssistOrder back, and releases anything
        // still held paused — leaving an extractor stopped with nothing running
        // to explain it would be the worst way to unload.
        private const string RemoveChunk =
            "if __SdbAssistUpgrade and __SdbAssistUpgradeOrig then " +
            "  for _, e in pairs(__SdbAssistPendingList or {}) do " +
            "    if e.paused and e.u and e.u.id then pcall(__SdbAssistToggle, e.u, false) end " +
            "  end " +
            "  local m = Import('client/inputEventsFunctions.lua') " +
            "  m.IssueAssistOrder = __SdbAssistUpgradeOrig " +
            "  IssueAssistOrder = __SdbAssistUpgradeOrig " +
            "  __SdbAssistUpgrade = nil " +
            "  __SdbAssistUpgradeOrig = nil " +
            "  __SdbAssistPendingList = nil " +
            "end";

        private void RemoveAssistHook()
        {
            if (!_assistHookInstalled) return;
            _assistHookInstalled = false;
            _assistSignature = null;
            try
            {
                if (LuaReady) RunLua(RemoveChunk);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Assist-starts-upgrade hook could not be removed: {e.Message}");
            }
        }

        private void AwakeAssistUpgrade()
        {
            _cfgAssistStartsUpgrade = Config.Bind("Assist", "AssistStartsUpgrade", true,
                "Ordering an engineer to assist one of your own finished alloy extractors queues its upgrade " +
                "first, so the assist has something to work on. Sends the same command the upgrade button does. " +
                "Set false to leave assist behaviour alone.");
            _cfgAssistPauses = Config.Bind("Assist", "AssistPausesUpgrade", true,
                "Hold each upgrade started this way paused until its engineer arrives. Without it, five " +
                "engineers sent to five extractors start five upgrades at once and the economy stalls; with " +
                "it the cost is spread across the walk.");
            _cfgAssistPauseDelay = Config.Bind("Assist", "AssistPauseSeconds", 1f,
                "How long to wait after queueing before pausing. The upgrade is not registered as in " +
                "progress on the frame it is requested, and pausing before then does nothing.");
            _cfgAssistPauseRadius = Config.Bind("Assist", "AssistPauseRadius", 3f,
                "Extra distance beyond the engineer's own build range at which it counts as having arrived, " +
                "which releases the pause. Raise it if upgrades stay paused after the engineer is clearly there.");
        }

        private string AssistSignature() =>
            $"{_cfgAssistPauses.Value}|{_cfgAssistPauseDelay.Value}|{_cfgAssistPauseRadius.Value}";

        /// Called each frame; installs the hook once the match's Lua VM is up.
        private void UpdateAssistUpgrade(float deltaTime)
        {
            if (!_cfgAssistStartsUpgrade.Value)
            {
                // Turned off mid-match: take the hook back out.
                if (_assistHookInstalled) RemoveAssistHook();
                return;
            }

            if (!InMatch)
            {
                // The VM is torn down between matches, taking the hook with
                // it, so the next match reinstalls from scratch.
                _assistHookInstalled = false;
                _assistSignature = null;
                return;
            }

            // The pause settings are baked into the chunk, so a change from the
            // mod manager has to reinstall rather than wait for the next match.
            if (_assistHookInstalled && _assistSignature != AssistSignature()) RemoveAssistHook();

            // Faster than the install upkeep below: this is what actually
            // applies the delayed pause and watches for the engineer arriving,
            // and a second's granularity would be visible on both.
            if (_assistHookInstalled && _cfgAssistPauses.Value)
            {
                _tickAccum += deltaTime;
                if (_tickAccum >= 0.2f)
                {
                    _tickAccum = 0f;
                    // pcall so one dead unit reference cannot spam the log five times a second.
                    RunLua("if __SdbAssistTick then pcall(__SdbAssistTick) end");
                }
            }

            _installAccum += deltaTime;
            if (_installAccum < 1f) return;
            _installAccum = 0f;

            if (_assistHookInstalled)
            {
                // Report each upgrade the hook starts, so "is it working?" is
                // answerable from the log rather than by inference.
                var raw = GetLuaGlobal("__SdbAssistUpgradeCount");
                if (int.TryParse(raw, out var count) && count > _upgradesQueued)
                {
                    _upgradesQueued = count;
                    Logger.LogInfo($"Assist started an extractor upgrade ({count} this match).");
                }
                return;
            }

            if (!LuaReady) return;
            try
            {
                var chunk = InstallChunk
                    .Replace("__PAUSE__", _cfgAssistPauses.Value ? "true" : "false")
                    .Replace("__DELAY__", Math.Max(0f, _cfgAssistPauseDelay.Value).ToString(CultureInfo.InvariantCulture))
                    .Replace("__RADIUS__", Math.Max(0f, _cfgAssistPauseRadius.Value).ToString(CultureInfo.InvariantCulture));
                if (!RunLua(chunk)) return;
                _assistHookInstalled = true;
                _assistSignature = AssistSignature();
                _upgradesQueued = 0;
                Logger.LogInfo("Assist-starts-upgrade hook installed for this match" +
                               (_cfgAssistPauses.Value ? " (upgrades held paused until the engineer arrives)." : "."));
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Assist-starts-upgrade hook could not be installed: {e.Message}");
            }
        }
    }
}
