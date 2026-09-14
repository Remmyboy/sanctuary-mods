using System;
using System.Collections.Generic;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Which element a unit type belongs to — land, water, both, or the air —
    // for colouring the unit tiles. The game's unit buttons carry no
    // template id, but their portrait is the template's foreground icon,
    // one per unit type; so once a match, a Lua query lists every template's
    // foreground icon id with its tags, and each id is resolved to the
    // Sprite the game hands its buttons. A button's portrait sprite then
    // looks the domain up by reference.
    internal static class UnitDomains
    {
        internal enum Domain { Unknown, Land, Naval, Amphibious, Air }

        internal static BepInEx.Configuration.ConfigEntry<bool> Enabled;

        internal static void Bind(BepInEx.Configuration.ConfigFile config)
        {
            Enabled = config.Bind("Construction", "DomainColours", false,
                "Colour the unit tiles in the selection row, build options and queue by element: green for land, blue for naval, " +
                "a lighter blue for air, and green over blue split diagonally for a unit that goes on both. Off: one neutral tile for all.");
        }

        private static readonly Dictionary<Sprite, Domain> _bySprite = new Dictionary<Sprite, Domain>();
        private static readonly List<KeyValuePair<uint, Domain>> _pending = new List<KeyValuePair<uint, Domain>>();
        private static bool _queried;
        private static float _nextTry;

        private const string Chunk =
            "local ok, err = pcall(function() " +
            "  local out = {} " +
            "  for tpId, tp in pairs(__Templates.Units) do " +
            "    local g = tp.general " +
            "    local id = g and g.foregroundIconID and tonumber(g.foregroundIconID.index) or 0 " +
            "    if id and id > 0 and tp.tags then " +
            "      local air, amph, naval, land = false, false, false, false " +
            "      for _, t in pairs(tp.tags) do " +
            "        if t == 'AIR' then air = true " +
            "        elseif t == 'AMPHIBIOUS' or t == 'HOVER' then amph = true " +
            "        elseif t == 'NAVAL' then naval = true " +
            "        elseif t == 'LAND' then land = true end " +
            "      end " +
            "      local d = 0 " +
            "      if air then d = 4 elseif amph or (naval and land) then d = 3 elseif naval then d = 2 elseif land then d = 1 end " +
            "      out[#out + 1] = id .. ',' .. d " +
            "    end " +
            "  end " +
            "  __SdbDomains = table.concat(out, ';') " +
            "end) " +
            "if not ok then __SdbDomainsErr = tostring(err) end";

        /// Forgets last match's sprites: the registry reloads per match.
        /// Called every frame outside a match; cheap once empty.
        internal static void Reset()
        {
            if (!_queried && _bySprite.Count == 0) return;
            _bySprite.Clear();
            _pending.Clear();
            _queried = false;
            _nextTry = 0f;
        }

        /// From Update, in a match. Runs the query once the Lua side is up,
        /// then resolves the ids to sprites a few at a time as they load.
        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value) return;
            if (!_queried)
            {
                if (Time.realtimeSinceStartup < _nextTry) return;
                _nextTry = Time.realtimeSinceStartup + 2f;
                EnsureLuaBridge();
                if (!LuaReady) return;
                if (!RunLua(Chunk)) return;
                var err = GetLuaGlobal("__SdbDomainsErr");
                if (!string.IsNullOrEmpty(err))
                {
                    _log?.LogWarning($"Unit domains: query failed ({err}); tiles stay one colour.");
                    _queried = true;
                    return;
                }
                var raw = GetLuaGlobal("__SdbDomains");
                if (string.IsNullOrEmpty(raw)) return;   // templates not loaded yet
                foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var comma = entry.IndexOf(',');
                    if (comma < 0) continue;
                    if (!uint.TryParse(entry.Substring(0, comma), out var id) || !int.TryParse(entry.Substring(comma + 1), out var d)) continue;
                    _pending.Add(new KeyValuePair<uint, Domain>(id, (Domain)Mathf.Clamp(d, 0, 4)));
                }
                _queried = true;
                _log?.LogInfo($"Unit domains: {_pending.Count} template(s) listed.");
                return;
            }

            // Resolve a batch per frame; an id whose art has not loaded yet
            // stays pending and is tried again.
            if (_pending.Count == 0 || Time.realtimeSinceStartup < _nextTry) return;
            var budget = 40;
            for (var i = _pending.Count - 1; i >= 0 && budget > 0; i--, budget--)
            {
                var sprite = ResolveSprite(_pending[i].Key);
                if (sprite == null) continue;
                _bySprite[sprite] = _pending[i].Value;
                _pending.RemoveAt(i);
            }
            if (_pending.Count > 0) _nextTry = Time.realtimeSinceStartup + 1f;
        }

        internal static Domain Of(Sprite portrait) =>
            portrait != null && _bySprite.TryGetValue(portrait, out var d) ? d : Domain.Unknown;
    }
}
