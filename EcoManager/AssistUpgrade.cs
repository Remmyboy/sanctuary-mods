using System;
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
    public partial class EcoManagerPlugin
    {
        private ConfigEntry<bool> _cfgAssistStartsUpgrade;
        private bool _assistHookInstalled;
        private float _installAccum;
        private int _upgradesQueued;

        // Guarded by a global inside the VM, so re-running it is harmless —
        // which is what makes retrying safe. Each match builds a fresh Lua
        // state, so the flag (and the hook) go away with the old one.
        private const string InstallChunk =
            "if not __SdbAssistUpgrade then " +
            "  __SdbAssistUpgrade = true " +
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
            "end";

        // Puts the client's own IssueAssistOrder back. Without this, unloading
        // the mod (or switching the setting off) would leave the wrapper live
        // in the VM and assists would keep queueing upgrades with nothing
        // running to explain it.
        private const string RemoveChunk =
            "if __SdbAssistUpgrade and __SdbAssistUpgradeOrig then " +
            "  local m = Import('client/inputEventsFunctions.lua') " +
            "  m.IssueAssistOrder = __SdbAssistUpgradeOrig " +
            "  IssueAssistOrder = __SdbAssistUpgradeOrig " +
            "  __SdbAssistUpgrade = nil " +
            "  __SdbAssistUpgradeOrig = nil " +
            "end";

        private void RemoveAssistHook()
        {
            if (!_assistHookInstalled) return;
            _assistHookInstalled = false;
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
        }

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
                return;
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
                if (!RunLua(InstallChunk)) return;
                _assistHookInstalled = true;
                _upgradesQueued = 0;
                Logger.LogInfo("Assist-starts-upgrade hook installed for this match.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Assist-starts-upgrade hook could not be installed: {e.Message}");
            }
        }
    }
}
