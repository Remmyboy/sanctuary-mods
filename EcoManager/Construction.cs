using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    /// One tile on the panel: everything of one template that our army has
    /// under construction right now, with the builders working on it.
    internal class BuildGroup
    {
        public string TpId;
        public int Tier;
        /// Build-menu art and the land/air/water plate behind it, as AssetID
        /// indices for ResolveSprite (0 = none).
        public uint IconId;
        public uint PlateId;
        public string Name;
        /// Units of this template under construction.
        public int Count;
        /// What the builders on them are asking for, per second. This is the
        /// demand, before any stall scales it down.
        public float AlloyRate;
        public float EnergyRate;
        /// Mean fraction complete across the group.
        public float Progress;
        /// How many of them nobody is working on.
        public int Unattended;
        /// LocalID indices, for selection and pausing.
        public readonly List<int> BuilderIds = new List<int>();
        public readonly List<int> TargetIds = new List<int>();

        public float Rate(bool energy) => energy ? EnergyRate : AlloyRate;
    }

    // The construction sweep behind the tiles.
    //
    // The economy stream only carries army totals, but the client does know
    // enough to split the spend by what is being built: every builder holds
    // `buildTarget` while its build routine runs (OnStartBuildRoutine), and
    // its `buildPower` is kept current by the host's SetBuildPower command.
    // The host's own drain (ResourceEntity:RecalculateBuildDrain) is
    // cost x sum(buildPower) / buildTime per second, so summing the build
    // power of everything pointed at a target reproduces its demand. What
    // this misses is adjacency discounts, which the client is never told
    // about, so a structure next to a storage will read a little high.
    //
    // Factories, engineers, assisting engineers and upgrading extractors all
    // go through the same build routine, so one sweep covers the lot: an
    // upgrade shows as its higher-tier template under construction, built by
    // the extractor below it, exactly as the game models it.
    public partial class EcoManagerPlugin
    {
        private List<BuildGroup> _buildGroups = new List<BuildGroup>();
        private float _buildPollAccum;
        private bool _loggedBuildPollFail;

        // Builders paused from a tile, by template. A paused builder ends its
        // build routine and drops `buildTarget`, so once paused nothing on the
        // client ties it to what it was building; this is what lets the same
        // tile release exactly what it held.
        private readonly Dictionary<string, List<int>> _pausedByTile = new Dictionary<string, List<int>>();

        // Placement ghosts (progress 0) are queued, not started, so they are
        // left out; the demand is zero and the tile would only be noise.
        private const string SweepChunk =
            "__SdbBuild = '' " +
            "local ok, err = pcall(function() " +
            "  local mine = {} " +
            "  for _, a in pairs(Armies or {}) do " +
            "    if a.focused then " +
            "      for _, u in pairs(a.units or {}) do mine[#mine+1] = u end " +
            "    end " +
            "  end " +
            "  local targets = {} " +
            "  for _, u in ipairs(mine) do " +
            "    local p = tonumber(u.progress) " +
            "    if p and p > 0 and u.id and u.tpId and u.IsCompleted and not u:IsCompleted() then " +
            "      targets[u.id.index] = { u = u, bp = 0, builders = {} } " +
            "    end " +
            "  end " +
            "  for _, u in ipairs(mine) do " +
            "    local t = u.buildTarget " +
            "    local e = t and t.id and targets[t.id.index] " +
            "    if e then " +
            "      e.bp = e.bp + (tonumber(u.buildPower) or 0) " +
            "      if u.localId then e.builders[#e.builders+1] = u.localId.index end " +
            "    end " +
            "  end " +
            "  local groups = {} " +
            "  for _, e in pairs(targets) do " +
            "    local u = e.u " +
            "    local g = groups[u.tpId] " +
            "    if not g then " +
            "      local t = u.tp or (__Templates and __Templates.Units and __Templates.Units[u.tpId]) or {} " +
            "      local gen, eco = t.general or {}, t.economy or {} " +
            "      local cost = eco.cost or {} " +
            // The FFI hands ids back as uint32 cdata, hence tonumber.
            "      g = { n = 0, a = 0, e = 0, prog = 0, idle = 0, builders = {}, tids = {}, " +
            "            tech = tonumber(gen.techNumber) or 1, " +
            "            icon = gen.foregroundIconID and tonumber(gen.foregroundIconID.index) or 0, " +
            "            plate = gen.backgroundIconID and tonumber(gen.backgroundIconID.index) or 0, " +
            "            name = (tostring(gen.displayName or gen.name or u.tpId):gsub('[;|]', ' ')), " +
            "            ca = tonumber(cost.alloys) or 0, ce = tonumber(cost.energy) or 0, " +
            "            bt = tonumber(eco.buildTime) or 0 } " +
            "      groups[u.tpId] = g " +
            "    end " +
            "    g.n = g.n + 1 " +
            "    g.prog = g.prog + ((u.GetFractionComplete and u:GetFractionComplete()) or 0) " +
            "    if g.bt > 0 then " +
            "      g.a = g.a + g.ca * e.bp / g.bt " +
            "      g.e = g.e + g.ce * e.bp / g.bt " +
            "    end " +
            "    if #e.builders == 0 then g.idle = g.idle + 1 end " +
            "    for _, b in ipairs(e.builders) do g.builders[#g.builders+1] = b end " +
            "    if u.localId then g.tids[#g.tids+1] = u.localId.index end " +
            "  end " +
            "  local out = {} " +
            "  for tp, g in pairs(groups) do " +
            "    out[#out+1] = string.format('%s;%d;%d;%d;%s;%d;%.2f;%.2f;%.3f;%d;%s;%s', " +
            "      tp, g.tech, g.icon, g.plate, g.name, g.n, g.a, g.e, g.prog / g.n, g.idle, " +
            "      table.concat(g.builders, ','), table.concat(g.tids, ',')) " +
            "  end " +
            // pairs() order changes between polls; sorted, the string only
            // changes when the answer does.
            "  table.sort(out) " +
            "  __SdbBuild = table.concat(out, '|') " +
            "end) " +
            "if not ok then Warn('SanctuaryHud eco sweep: ' .. tostring(err)) end";

        /// Called each frame from Update. One sweep a second, like the ECS poll.
        private void UpdateConstruction(float deltaTime)
        {
            if (!InMatch)
            {
                if (_buildGroups.Count > 0) _buildGroups = new List<BuildGroup>();
                // The VM goes with the match, and the builders with it.
                _pausedByTile.Clear();
                return;
            }

            _buildPollAccum += deltaTime;
            if (_buildPollAccum < 1f) return;
            _buildPollAccum = 0f;
            if (!LuaReady) return;

            try
            {
                if (!RunLua(SweepChunk)) return;
                var raw = GetLuaGlobal("__SdbBuild");
                if (raw == null) return;
                var groups = Parse(raw);
                _buildGroups = groups;
                ReleaseVanished(groups);
            }
            catch (Exception e)
            {
                if (!_loggedBuildPollFail)
                {
                    _loggedBuildPollFail = true;
                    Logger.LogWarning($"Construction sweep failed (the build tiles will stay hidden): {e.Message}");
                }
            }
        }

        private static List<BuildGroup> Parse(string raw)
        {
            var groups = new List<BuildGroup>();
            foreach (var record in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = record.Split(';');
                if (f.Length != 12) continue;
                var g = new BuildGroup
                {
                    TpId = f[0],
                    Tier = Int(f[1], 1),
                    IconId = (uint)Int(f[2], 0),
                    PlateId = (uint)Int(f[3], 0),
                    Name = f[4],
                    Count = Int(f[5], 0),
                    AlloyRate = Float(f[6]),
                    EnergyRate = Float(f[7]),
                    Progress = Mathf.Clamp01(Float(f[8])),
                    Unattended = Int(f[9], 0),
                };
                if (g.Count <= 0) continue;
                Ids(f[10], g.BuilderIds);
                Ids(f[11], g.TargetIds);
                groups.Add(g);
            }
            // Tier, then template: a fixed order, so tiles don't shuffle as
            // their rates move.
            return groups.OrderBy(g => g.Tier).ThenBy(g => g.TpId, StringComparer.Ordinal).ToList();
        }

        private static int Int(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static float Float(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static void Ids(string s, List<int> into)
        {
            foreach (var part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) into.Add(id);
            }
        }

        // ---- pausing from a tile ---------------------------------------------

        /// Whether a tile's builders are being held by this panel.
        private bool TilePaused(BuildGroup g) => _pausedByTile.TryGetValue(g.TpId, out var held) && held.Count > 0;

        /// The builders this panel is holding paused for a tile.
        private List<int> HeldBuilders(BuildGroup g) =>
            _pausedByTile.TryGetValue(g.TpId, out var held) ? held : new List<int>();

        /// Pauses every builder working on the tile's template, or releases
        /// the ones this panel paused earlier. The pause is the game's own
        /// Pause toggle, sent the way the orders panel sends it, so the host
        /// validates it like any click.
        private void TogglePause(BuildGroup g)
        {
            if (_pausedByTile.TryGetValue(g.TpId, out var held) && held.Count > 0)
            {
                _pausedByTile.Remove(g.TpId);
                SetPause(held, false);
                Logger.LogInfo($"Released {held.Count} builder(s) on {g.Name}.");
            }
            else if (g.BuilderIds.Count > 0)
            {
                var ids = g.BuilderIds.Distinct().ToList();
                _pausedByTile[g.TpId] = ids;
                SetPause(ids, true);
                Logger.LogInfo($"Paused {ids.Count} builder(s) on {g.Name}.");
            }
        }

        /// A tile that is gone — its targets finished, or were cancelled or
        /// destroyed — must not leave builders paused with nothing on screen
        /// to say why. Pause also stops an extractor producing, so this is the
        /// same care AssistUpgrade takes with a cancelled upgrade.
        private void ReleaseVanished(List<BuildGroup> current)
        {
            if (_pausedByTile.Count == 0) return;
            var present = new HashSet<string>(current.Select(g => g.TpId));
            foreach (var key in _pausedByTile.Keys.Where(k => !present.Contains(k)).ToList())
            {
                var held = _pausedByTile[key];
                _pausedByTile.Remove(key);
                SetPause(held, false);
                Logger.LogInfo($"Released {held.Count} paused builder(s): nothing of {key} is under construction any more.");
            }
        }

        /// From OnDestroy: unloading must not strand anything paused.
        private void ReleaseAllPaused()
        {
            if (_pausedByTile.Count == 0) return;
            var all = _pausedByTile.Values.SelectMany(v => v).Distinct().ToList();
            _pausedByTile.Clear();
            if (LuaReady) SetPause(all, false);
        }

        /// Sends the Pause toggle for a set of LocalID indices. The command
        /// wants GlobalIDs, so the chunk walks the unit table for them, and
        /// asks only units that actually carry the toggle.
        private static void SetPause(List<int> localIdIndices, bool on)
        {
            if (localIdIndices == null || localIdIndices.Count == 0) return;
            var want = string.Join(",", localIdIndices.Distinct().Take(300).Select(i => $"[{i}]=true"));
            var flag = on ? "true" : "false";
            RunLua(
                "local ok, err = pcall(function() " +
                $"  local want = {{{want}}} " +
                "  local ids = {} " +
                "  for _, u in pairs(__Entities.Units) do " +
                "    local li = u.localId and u.localId.index " +
                "    if li and want[li] and u.id and u.HasToggle and u:HasToggle('Pause') then ids[#ids+1] = u.id end " +
                "  end " +
                "  if #ids > 0 then " +
                "    Import('common/commands/definitions/toggles.lua').RequestUnitsToggle.Send( " +
                $"      ids, Import('common/toggles.lua').ToggleNameToToggleType('Pause'), {flag}) " +
                "  end " +
                "end) " +
                "if not ok then Warn('SanctuaryHud eco pause: ' .. tostring(err)) end");
        }
    }
}
