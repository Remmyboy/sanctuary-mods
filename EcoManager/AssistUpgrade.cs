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
    // engineer actually starts building it — see the Lua below for why that is
    // worth the machinery.
    public partial class EcoManagerPlugin
    {
        private ConfigEntry<bool> _cfgAssistStartsUpgrade;
        private ConfigEntry<bool> _cfgAssistPauses;
        private ConfigEntry<float> _cfgAssistPauseDelay;

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
            "  __SdbAssistPendingList = {} " +
            "  local m = Import('client/inputEventsFunctions.lua') " +
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
            // Builders only: the assist has to come from an engineer or the
            // commander, something that can actually work on the upgrade. A
            // tank told to assist an extractor just guards it, and must not
            // spend alloy on the way.
            "      local sel = Import('client/input/selectionSystem.lua') " +
            "      local pickedNow = (sel.GetSelectedUnits and sel.GetSelectedUnits()) " +
            "        or (sel.GetSelectedEntities and sel.GetSelectedEntities()) or {} " +
            "      local builder = false " +
            "      for _, e in pairs(pickedNow) do " +
            "        if e ~= hover and e.tp and e.tp.construction and e.tp.movement then builder = true break end " +
            "      end " +
            "      if not builder then return end " +
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
            // Remember the extractor, so the tick below can hold its upgrade
            // until work on it actually begins.
            "      if __SdbAssistPause then " +
            "        local now = (os and os.clock) and os.clock() or 0 " +
            "        table.insert(__SdbAssistPendingList, " +
            "          { u = hover, due = now + __SdbAssistPauseDelay, paused = false }) " +
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
            // the moment it starts, then releasing it when an engineer starts
            // building it, spreads that cost over the walk instead.
            //
            // "Starts building" is the host's own signal, not distance. The
            // host sends OnStartBuilding only once a builder is in range and
            // working, which sets isBuilding and buildTarget on that unit
            // here. An engineer assisting an upgrade builds the upgrade site —
            // the separate structure growing on the extractor's spot — whose
            // `upgrader` points back at the extractor. So an engineer still
            // walking over, one with the assist further down its queue, or
            // one working on the extractor next door releases nothing.
            //
            // The pause itself waits for that site to show some progress.
            // Until then the site is still a placement ghost, which an
            // assisting engineer will not start on, so a pause landing that
            // early would hold the upgrade for good. The delay before any of
            // this lets the queued upgrade settle first.
            "  function __SdbAssistToggle(unit, on) " +
            "    Import('common/commands/definitions/toggles.lua').RequestUnitsToggle.Send( " +
            "      { unit.id }, Import('common/toggles.lua').ToggleNameToToggleType('Pause'), on) " +
            "  end " +

            "  function __SdbAssistTick() " +
            "    local list = __SdbAssistPendingList " +
            "    if not list or #list == 0 then return end " +
            // Upgrades with a builder on them right now, by upgrading
            // structure. The structure building its own upgrade doesn't count;
            // anyone else's engineer, an ally's included, does.
            "    local worked = {} " +
            "    for _, a in pairs(Armies or {}) do " +
            "      for _, w in pairs(a.units or {}) do " +
            "        local t = not w.deleted and w.isBuilding and w.buildTarget " +
            "        local up = t and t.upgrader " +
            "        if up and up ~= w and up.id then worked[up.id.index] = true end " +
            "      end " +
            "    end " +
            "    local now = (os and os.clock) and os.clock() or 0 " +
            "    for i = #list, 1, -1 do " +
            "      local e = list[i] " +
            "      local u = e.u " +
            "      local drop = false " +
            "      if not (u and u.id) or u.deleted then " +
            "        drop = true " +
            "      elseif now >= e.due then " +
            "        if worked[u.id.index] or not (u.IsUpgradeQueued and u:IsUpgradeQueued()) then " +
            // Being built, or finished or cancelled: let go either way, so a
            // cancelled upgrade never strands a paused extractor.
            "          if e.paused then __SdbAssistToggle(u, false) end " +
            "          drop = true " +
            "        elseif not e.paused then " +
            "          local site = u.upgradeTarget " +
            "          if site and site.progress and site.progress > 0 then " +
            "            if u.HasToggle and u:HasToggle('Pause') then " +
            "              __SdbAssistToggle(u, true) " +
            "              e.paused = true " +
            "            else " +
            "              drop = true " +
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
                "Hold each upgrade started this way paused until an engineer actually starts building it, " +
                "not just when one is nearby. Without it, five engineers sent to five extractors start five " +
                "upgrades at once and the economy stalls; with it the cost is spread across the walk.");
            _cfgAssistPauseDelay = Config.Bind("Assist", "AssistPauseSeconds", 1f,
                "The least time to wait after queueing before pausing. The pause also waits for the upgrade to " +
                "have actually begun, since an engineer cannot start on one paused before then.");
        }

        private string AssistSignature() =>
            $"{_cfgAssistPauses.Value}|{_cfgAssistPauseDelay.Value}";

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
            // applies the delayed pause and watches for an engineer starting
            // work, and a second's granularity would be visible on both.
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
                    .Replace("__DELAY__", Math.Max(0f, _cfgAssistPauseDelay.Value).ToString(CultureInfo.InvariantCulture));
                if (!RunLua(chunk)) return;
                _assistHookInstalled = true;
                _assistSignature = AssistSignature();
                _upgradesQueued = 0;
                Logger.LogInfo("Assist-starts-upgrade hook installed for this match" +
                               (_cfgAssistPauses.Value ? " (upgrades held paused until an engineer starts building them)." : "."));
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Assist-starts-upgrade hook could not be installed: {e.Message}");
            }
        }
    }
}
