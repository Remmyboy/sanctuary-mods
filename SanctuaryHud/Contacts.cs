using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // What the mini-map plots: unit contacts, the colours to draw them in, and
    // the alloy deposits nobody has taken yet.
    //
    // All of it comes out of the client's own Lua, on a poll, by the pattern
    // WorldOverlays established — a chunk wrapped in pcall packs a delimited
    // string into a global and C# reads it back. Lua is the right side for
    // this: it is where a unit knows which army it belongs to and whether the
    // player can currently see it, and one sweep inside LuaJIT is far cheaper
    // than the same walk through reflection.
    //
    // The fog rule is the important part. The host broadcasts every unit to
    // every client and leaves the filtering to the client, so plotting
    // everything would be a maphack. ClientUnit:IsHighlightable() —
    // (vision or radar) and not an upgrade shell — is the game's own test, and
    // the same one it gates a unit's strategic icon on, so applying it here
    // means the mini-map shows exactly the contacts the game already draws on
    // the battlefield.
    internal static class Contacts
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal struct Contact
        {
            public float X;
            public float Z;
            public int Army;
            /// Index into the strategic icon atlas, or -1 while the registry
            /// hasn't caught up and the contact draws as a plain mark.
            public int Icon;
            /// False for a contact the player only has on radar. The game
            /// draws those as a blank plate in white rather than in the army's
            /// colour, because it hasn't told the player whose it is.
            public bool Seen;
            /// How far this unit sees, in world units, but only for units
            /// whose intel the viewer actually shares — 0 for everyone else's.
            /// This is what the fog is drawn from.
            public float VisionRadius;
        }

        /// False when nothing is hidden from the viewer — the all-armies
        /// observer view a replay uses — so the fog is left off rather than
        /// shading a map the viewer can see all of.
        internal static bool FogApplies { get; private set; }

        internal static List<Contact> Live = new List<Contact>();
        internal static readonly Dictionary<int, Color> ArmyColours = new Dictionary<int, Color>();
        /// Unclaimed alloy deposits, as world x/z.
        internal static List<Vector2> AlloySpots = new List<Vector2>();

        private static float _contactAccum = 999f;
        private static float _slowAccum = 999f;
        private static bool _loggedContactErr;
        private static bool _loggedSlowErr;

        /// Name-to-atlas-index answers, kept across polls: the distinct icon
        /// set of a match is small and stable, so this settles within a poll
        /// or two and then costs nothing.
        private static readonly Dictionary<string, int> _iconIndexCache = new Dictionary<string, int>();

        // One pass over every army's units. Per contact it emits the army id,
        // the position, an ordinal into a table of the distinct icon names
        // seen this pass, and whether the contact is seen or merely on radar —
        // repeating "structure1_t2_alloy_normal" a hundred times would dwarf
        // everything else in the payload.
        //
        // A radar-only contact must not get its real icon. The game swaps them
        // itself: ClientUnit:OnIntelRadar disables the StrategicIcon and
        // enables the RadarIcon, which unitTemplateLoader builds as
        // "<shape>_<tech>_none_normal" — the same plate with the role symbol
        // left out, drawn pure white. Sending the strategic icon for a radar
        // blip would tell the player it is a T3 bomber when the game has
        // deliberately not told them that. Both names come off the template,
        // which is where the game put them, and are cached per template since
        // neither ever changes.
        private const string ContactsChunk =
            "__SdbMmData = '' __SdbMmIcons = '' " +
            "local ok, err = pcall(function() " +
            "  local cache = __SdbMmIconCache " +
            "  if not cache then cache = {} __SdbMmIconCache = cache end " +
            "  local names, nameIndex, nn = {}, {}, 0 " +
            "  local out, n = {}, 0 " +
            "  local function ordinal(nm) " +
            "    local i = nameIndex[nm] " +
            "    if not i then nn = nn + 1 names[nn] = nm nameIndex[nm] = nn i = nn end " +
            "    return i " +
            "  end " +
            // Whose eyes the viewer is looking through: their own army and its
            // allies. A unit's vision is only reported for those, so the fog
            // can never be lifted by an enemy's sight. The all-armies observer
            // view sees everything, so there is no fog to draw at all.
            "  local focus = GetFocusArmy() " +
            "  local fa = focus and focus ~= -1 and Armies and Armies[focus] or nil " +
            "  __SdbMmFog = (focus == -1 or fa == nil) and '0' or '1' " +
            "  local share = {} " +
            "  for aid, a in pairs(Armies or {}) do " +
            "    share[aid] = (fa ~= nil) and (a.focused == true or (fa.allyIDs and fa.allyIDs[aid] and true or false)) or false " +
            "  end " +
            "  for aid, a in pairs(Armies or {}) do " +
            "    for _, u in pairs(a.units or {}) do " +
            // A placement ghost is a queued building nobody has started yet
            // (progress <= 0). The game draws those on the battlefield as
            // outlines; on a mini-map they would read as finished structures,
            // which is worse than not showing them at all.
            "      if not u.deleted and not u.dead and u.IsHighlightable and u:IsHighlightable() and u.GetPosition " +
            "         and not (u.IsPlacementGhost and u:IsPlacementGhost()) then " +
            // A nil key would throw rather than miss, so anything without a
            // template id shares slot 0 and looks its icons up each time.
            "        local key = u.tpId or 0 " +
            "        local pair = cache[key] " +
            "        if pair == nil then " +
            "          local ri = u.tp and u.tp.general and u.tp.general.icon and u.tp.general.icon.runtimeInformation " +
            "          pair = { (ri and ri.StrategicIcon and ri.StrategicIcon.name) or '', " +
            "                   (ri and ri.RadarIcon and ri.RadarIcon.name) or '', " +
            "                   (u.tp and u.tp.intel and u.tp.intel.visionRadius) or 0 } " +
            "          cache[key] = pair " +
            "        end " +
            "        local vis = u.isIntelVisionVisible and 1 or 0 " +
            "        local idx = ordinal(vis == 1 and pair[1] or pair[2]) " +
            "        local vr = share[aid] and pair[3] or 0 " +
            "        local p = u:GetPosition() " +
            "        n = n + 1 " +
            "        out[n] = string.format('%d,%.0f,%.0f,%d,%d,%.0f', aid, p.x, p.z, idx, vis, vr) " +
            "      end " +
            "    end " +
            "  end " +
            "  __SdbMmIcons = table.concat(names, ';') " +
            "  __SdbMmData = table.concat(out, ';') " +            "end) " +
            "if not ok then __SdbMmErr = tostring(err) else __SdbMmErr = '' end";

        // Colours and the deposits, both of which change rarely enough to sit
        // on their own slow poll.
        //
        // The colour has to come off the army object: they are handed out by
        // registration-order colour id rather than by lobby army id, so
        // deriving one any other way lands on somebody else's colour.
        //
        // The deposits are the ones the game itself marks on the ground, which
        // is not simply the enabled ones: ClientAlloyResourceSpot
        // .RecalculateRendering hides a marker while the spot is disabled *or*
        // while an extractor the player can see is sitting on it. Both tests
        // are repeated here rather than reading the marker's own flag, because
        // CameraUtilities' "hide alloy spot markers" switch writes that flag —
        // and running both mods should not silently empty this layer.
        private const string SlowChunk =
            "__SdbMmArmies = '' __SdbMmSpots = '' " +
            "local ok, err = pcall(function() " +
            "  local out, n = {}, 0 " +
            "  for aid, a in pairs(Armies or {}) do " +
            "    local c = a.color or { x = 0.6, y = 0.6, z = 0.6 } " +
            "    n = n + 1 " +
            "    out[n] = string.format('%d,%.3f,%.3f,%.3f', aid, c.x, c.y, c.z) " +
            "  end " +
            "  __SdbMmArmies = table.concat(out, ';') " +
            "  local spots, m = {}, 0 " +
            "  local rs = Import('common/resourceSpot.lua').resourceSpots or {} " +
            "  for _, s in pairs(rs) do " +
            "    local show = s.IsEnabled and s:IsEnabled() and s.GetPosition and true or false " +
            "    if show then " +
            "      for _, e in ipairs(s.extractors or {}) do " +
            "        if e.IsHighlightable and e:IsHighlightable() then show = false break end " +
            "      end " +
            "    end " +
            "    if show then " +
            "      local p = s:GetPosition() " +
            "      m = m + 1 " +
            "      spots[m] = string.format('%.0f,%.0f', p.x, p.z) " +
            "    end " +
            "  end " +
            "  __SdbMmSpots = table.concat(spots, ';') " +
            "end) " +
            "if not ok then __SdbMmSlowErr = tostring(err) else __SdbMmSlowErr = '' end";

        /// Drives both polls. `hz` is how often contacts are refreshed; the
        /// colours and deposits go at a fixed slow rate underneath.
        internal static void Poll(float deltaTime, float hz, bool wantSpots)
        {
            EnsureLuaBridge();
            if (!LuaReady) return;
            EnsureIconRegistry();

            _contactAccum += deltaTime;
            _slowAccum += deltaTime;

            var interval = 1f / Mathf.Clamp(hz, 1f, 30f);
            if (_contactAccum >= interval)
            {
                _contactAccum = 0f;
                PollContacts();
            }

            if (_slowAccum >= 2f)
            {
                _slowAccum = 0f;
                PollSlow(wantSpots);
            }
        }

        private static void PollContacts()
        {
            if (!RunLua(ContactsChunk)) return;

            var err = GetLuaGlobal("__SdbMmErr");
            if (!string.IsNullOrEmpty(err) && !_loggedContactErr)
            {
                _loggedContactErr = true;
                _log?.LogWarning($"MiniMap: contact query failed ({err}); the map stays empty.");
            }

            var icons = ResolveIcons(GetLuaGlobal("__SdbMmIcons"));
            FogApplies = GetLuaGlobal("__SdbMmFog") == "1";

            var raw = GetLuaGlobal("__SdbMmData");
            if (raw == null) return;

            var list = new List<Contact>(raw.Length / 14 + 1);
            foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 6) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, Inv, out var army) ||
                    !float.TryParse(f[1], NumberStyles.Float, Inv, out var x) ||
                    !float.TryParse(f[2], NumberStyles.Float, Inv, out var z) ||
                    !int.TryParse(f[3], NumberStyles.Integer, Inv, out var ordinal)) continue;
                float.TryParse(f[5], NumberStyles.Float, Inv, out var visionRadius);
                list.Add(new Contact
                {
                    X = x,
                    Z = z,
                    Army = army,
                    Icon = ordinal >= 1 && ordinal <= icons.Length ? icons[ordinal - 1] : -1,
                    Seen = f[4] == "1",
                    VisionRadius = visionRadius,
                });
            }
            Live = list;
        }

        /// Turns this pass's distinct icon names into atlas indices, in the
        /// order the chunk numbered them.
        private static int[] ResolveIcons(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<int>();
            var names = raw.Split(';');
            var result = new int[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (!_iconIndexCache.TryGetValue(name, out var index))
                {
                    index = IconIndexByName(name);
                    // A miss before the registry is populated must not be
                    // remembered as permanent, or the whole match draws blank.
                    if (index >= 0) _iconIndexCache[name] = index;
                }
                result[i] = index;
            }
            return result;
        }

        private static void PollSlow(bool wantSpots)
        {
            if (!RunLua(SlowChunk)) return;

            var err = GetLuaGlobal("__SdbMmSlowErr");
            if (!string.IsNullOrEmpty(err) && !_loggedSlowErr)
            {
                _loggedSlowErr = true;
                _log?.LogWarning($"MiniMap: army/deposit query failed ({err}); colours fall back to grey.");
            }

            var armies = GetLuaGlobal("__SdbMmArmies");
            if (armies != null)
            {
                ArmyColours.Clear();
                foreach (var entry in armies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = entry.Split(',');
                    if (f.Length < 4 || !int.TryParse(f[0], NumberStyles.Integer, Inv, out var id)) continue;
                    float.TryParse(f[1], NumberStyles.Float, Inv, out var r);
                    float.TryParse(f[2], NumberStyles.Float, Inv, out var g);
                    float.TryParse(f[3], NumberStyles.Float, Inv, out var b);
                    // Army colours are chosen for unit meshes and can be very
                    // dark; lift them so a few pixels of icon still read, the
                    // way the replay panel lifts them for its buttons.
                    var lift = Mathf.Max(0.45f, Mathf.Max(r, Mathf.Max(g, b)));
                    ArmyColours[id] = new Color(r / lift, g / lift, b / lift, 1f);
                }
            }

            if (!wantSpots)
            {
                if (AlloySpots.Count > 0) AlloySpots = new List<Vector2>();
                return;
            }

            var spots = GetLuaGlobal("__SdbMmSpots");
            if (spots == null) return;
            var found = new List<Vector2>();
            foreach (var entry in spots.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 2) continue;
                if (!float.TryParse(f[0], NumberStyles.Float, Inv, out var x) ||
                    !float.TryParse(f[1], NumberStyles.Float, Inv, out var z)) continue;
                found.Add(new Vector2(x, z));
            }
            AlloySpots = found;
        }

        internal static Color ColourFor(int army)
            => ArmyColours.TryGetValue(army, out var c) ? c : new Color(0.7f, 0.7f, 0.7f, 1f);

        /// Between matches. The icon registry and the Lua-side template cache
        /// are both per-match, so neither is carried over.
        internal static void Clear()
        {
            Live = new List<Contact>();
            AlloySpots = new List<Vector2>();
            ArmyColours.Clear();
            _iconIndexCache.Clear();
            _contactAccum = 999f;
            _slowAccum = 999f;
            _loggedContactErr = false;
            _loggedSlowErr = false;
            FogApplies = false;
            ClearIconRegistry();
        }
    }
}
