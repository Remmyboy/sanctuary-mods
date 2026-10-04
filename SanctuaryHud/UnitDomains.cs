using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Which unit type a button stands for. The game's unit buttons carry no
    // template id, but their portrait is the template's foreground icon,
    // one per unit type; so once a match, a Lua query lists every template's
    // foreground icon id, and each id is resolved to the Sprite the game
    // hands its buttons. A button's portrait sprite then looks the template
    // up by reference (the build card asks which option is under the mouse).
    // The name is from when it also gave each type its element for
    // colouring the tiles, which nothing does any more.
    internal static class UnitDomains
    {
        private static readonly Dictionary<Sprite, string> _bySprite = new Dictionary<Sprite, string>();
        private static readonly List<KeyValuePair<uint, string>> _pending = new List<KeyValuePair<uint, string>>();
        private static bool _queried;
        private static float _nextTry;
        private static int _cursor;   // where the next batch starts, so every entry gets its turn

        // Templates with tags only, as before: the rest are not units a
        // panel shows.
        private const string Chunk =
            "local ok, err = pcall(function() " +
            "  local out = {} " +
            "  for tpId, tp in pairs(__Templates.Units) do " +
            "    local g = tp.general " +
            "    local id = g and g.foregroundIconID and tonumber(g.foregroundIconID.index) or 0 " +
            "    if id and id > 0 and tp.tags then " +
            "      out[#out + 1] = id .. ',' .. tostring(tpId) " +
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
            _cursor = 0;
            _queried = false;
            _nextTry = 0f;
        }

        /// From Update, in a match. Runs the query once the Lua side is up,
        /// then resolves the ids to sprites a few at a time as they load.
        internal static void Tick()
        {
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
                    if (comma <= 0 || !uint.TryParse(entry.Substring(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                    _pending.Add(new KeyValuePair<uint, string>(id, entry.Substring(comma + 1)));
                }
                _queried = true;
                _log?.LogInfo($"Unit domains: {_pending.Count} template(s) listed.");
                return;
            }

            // Resolve a batch per frame; an id whose art has not loaded yet
            // stays pending and is tried again.
            if (_pending.Count == 0 || Time.realtimeSinceStartup < _nextTry) return;
            // The batch walks on from where the last one stopped: starting
            // at the same end each time, forty entries that never load (a
            // faction's art not in memory) would starve all the rest.
            for (var budget = 40; budget > 0 && _pending.Count > 0; budget--)
            {
                if (_cursor >= _pending.Count) _cursor = 0;
                var sprite = ResolveSprite(_pending[_cursor].Key);
                if (sprite == null) { _cursor++; continue; }
                _bySprite[sprite] = _pending[_cursor].Value;
                _pending.RemoveAt(_cursor);   // the next entry slides into _cursor
            }
            if (_pending.Count > 0) _nextTry = Time.realtimeSinceStartup + 1f;
        }

        /// The template id behind a button's portrait, or null.
        internal static string TemplateOf(Sprite portrait) =>
            portrait != null && _bySprite.TryGetValue(portrait, out var tpId) ? tpId : null;
    }
}
