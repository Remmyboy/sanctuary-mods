using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Labels anchored to the map rather than the screen edge: the reclaim
    // value of wrecks and harvestable props, and a time-to-finish on every
    // structure of ours still under construction.
    //
    // Both read the client's own Lua state (the tables the game fills from
    // the host stream) through the HudCore bridge, on a slow poll, and
    // project the cached world positions through the game's camera every
    // frame. Nothing here reaches into the simulation or the hashed Lua tree.
    internal static class WorldOverlays
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- reclaim -----------------------------------------------------

        internal struct Reclaim
        {
            public Vector3 Position;
            public float Alloys;
            public float Energy;
        }

        private static List<Reclaim> _reclaim = new List<Reclaim>();
        private static float _reclaimAccum = 10f;   // poll straight away
        private static bool _loggedReclaimErr;

        // Every prop the client knows about is in __Entities.Props: map props
        // (trees, rocks) and wrecks alike, each carrying its template. A
        // harvestable one has tp.economy.harvest = { alloys, energy }, and
        // the host streams reclaimProgress (0..harvestTime) as it is eaten,
        // so what is left is harvest * (1 - progress / harvestTime).
        //
        // Positions never change, so they are cached Lua-side keyed by the
        // prop's global index, with the prop object itself kept alongside to
        // notice an index being reused by a new prop. Only the value is
        // recomputed per poll, and only props with something left are sent.
        private const string ReclaimChunk =
            "__SdbReclaim = '' " +
            "local ok, err = pcall(function() " +
            "  local cache = __SdbReclaimCache " +
            "  if not cache then cache = {} __SdbReclaimCache = cache end " +
            "  local out, n = {}, 0 " +
            "  for id, p in pairs(__Entities.Props or {}) do " +
            // A decayed wreck loses its render entity but stays in the Lua
            // table (nothing removes it), so ask the engine whether the id
            // still names anything before counting it.
            "    if not p.deleted and p.localId and Engine.IsValidLocalID(p.localId) then " +
            "      local c = cache[id] " +
            "      if c == nil or (c and c[7] ~= p) then " +
            "        local eco = p.tp and p.tp.economy " +
            "        local h = eco and eco.harvest " +
            "        if h and ((h.alloys or 0) > 0 or (h.energy or 0) > 0) and p.GetPosition then " +
            "          local pos = p:GetPosition() " +
            "          c = { pos.x, pos.y, pos.z, h.alloys or 0, h.energy or 0, eco.harvestTime or 1, p } " +
            "        else " +
            "          c = false " +
            "        end " +
            "        cache[id] = c " +
            "      end " +
            "      if c then " +
            "        local frac = 1 - (p.reclaimProgress or 0) / c[6] " +
            "        if frac > 0.001 then " +
            "          n = n + 1 " +
            "          out[n] = string.format('%.1f,%.1f,%.1f,%.0f,%.0f', c[1], c[2], c[3], c[4] * frac, c[5] * frac) " +
            "        end " +
            "      end " +
            "    end " +
            "  end " +
            "  __SdbReclaim = table.concat(out, ';') " +
            "end) " +
            "if not ok then __SdbReclaimErr = tostring(err) else __SdbReclaimErr = '' end";

        private static void PollReclaim()
        {
            EnsureLuaBridge();
            if (!LuaReady || !RunLua(ReclaimChunk)) return;

            var err = GetLuaGlobal("__SdbReclaimErr");
            if (!string.IsNullOrEmpty(err) && !_loggedReclaimErr)
            {
                _loggedReclaimErr = true;
                _log?.LogWarning($"Reclaim overlay: Lua query failed ({err}); the overlay stays empty.");
            }

            var raw = GetLuaGlobal("__SdbReclaim");
            if (raw == null) return;

            var list = new List<Reclaim>(raw.Length / 24 + 1);
            foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 5) continue;
                if (!float.TryParse(f[0], NumberStyles.Float, Inv, out var x) ||
                    !float.TryParse(f[1], NumberStyles.Float, Inv, out var y) ||
                    !float.TryParse(f[2], NumberStyles.Float, Inv, out var z) ||
                    !float.TryParse(f[3], NumberStyles.Float, Inv, out var a) ||
                    !float.TryParse(f[4], NumberStyles.Float, Inv, out var e)) continue;
                list.Add(new Reclaim { Position = new Vector3(x, y, z), Alloys = a, Energy = e });
            }
            _reclaim = list;
        }

        // ---- build ETA -----------------------------------------------------

        internal class Build
        {
            public int Id;
            public string Name;
            public Vector3 Position;
            public float Fraction;
            /// The template's build time: seconds at one unit of build power,
            /// the nominal rate before anything has been measured.
            public float BuildTime;
            /// Tech tier from the template (1..4).
            public int Tier;
            /// What kind of structure, from its tags: factory, extractor,
            /// energy, intel, defence, tech, strategic, other.
            public string Role;
            /// True when this structure is the higher tier of an upgrade
            /// rather than a fresh build. The game sets `upgrader` on the new
            /// entity for as long as the upgrade runs, so it is read while the
            /// site is in progress and remembered here.
            public bool IsUpgrade;
            /// Fraction per second, filtered over the last few samples.
            public float Rate;
            public float LastChange;    // realtime the fraction last moved
            public float LastSeen;
            public float LastFraction = -1f;
        }

        private static readonly Dictionary<int, Build> _builds = new Dictionary<int, Build>();
        private static List<Build> _buildList = new List<Build>();
        private static float _buildAccum = 10f;
        private static bool _loggedBuildErr;

        /// Raised when a tracked structure vanishes from the in-progress set
        /// having been (near enough) finished, so Alerts can announce it.
        internal static event Action<Build> OnBuildFinished;

        // Our own army's structures with a build under way: past the placement
        // ghost (progress > 0) and not yet complete. An upgrade counts too,
        // since the higher-tier replacement is a second structure building
        // on the same spot. progress is in build-time units, so it is
        // divided by tp.economy.buildTime to get a fraction.
        private const string BuildChunk =
            "__SdbBuilds = '' " +
            "local ok, err = pcall(function() " +
            "  local out, n = {}, 0 " +
            "  local S = Tags and Tags.STRUCTURE " +
            // A player has exactly one focused army. A replay's all-armies
            // view focuses every army and an observer none; neither is
            // anyone's base, so neither gets countdowns or completions.
            "  local focused = 0 " +
            "  for _, a in pairs(Armies or {}) do if a.focused and not a.civilian then focused = focused + 1 end end " +
            "  for _, a in pairs(Armies or {}) do " +
            "    if a.focused and focused == 1 then " +
            "      for _, u in pairs(a.units or {}) do " +
            "        if not u.deleted and u.tp and u.id and S and S[u.tpId] " +
            "           and u.progress and u.progress > 0 and u.IsCompleted and not u:IsCompleted() then " +
            "          local bt = u.tp.economy and u.tp.economy.buildTime or 0 " +
            "          if bt > 0 then " +
            "            local pos = u:GetPosition() " +
            "            local g = u.tp.general or {} " +
            "            local name = g.displayName or g.unitTypeName or g.name or u.tpId " +
            "            local id = u.tpId " +
            "            local function has(t) return Tags[t] and Tags[t][id] and true or false end " +
            "            local role = 'other' " +
            "            if has('FACTORY') then role = 'factory' " +
            "            elseif has('ALLOYS_EXTRACTION') or has('ALLOYS_PRODUCTION') or has('ALLOYS_STORAGE') then role = 'extractor' " +
            "            elseif has('ENERGY_PRODUCTION') or has('ENERGY_STORAGE') then role = 'energy' " +
            "            elseif has('RADAR') or has('SONAR') or has('INTEL') then role = 'intel' " +
            "            elseif has('DEFENCE') or has('SHIELD') or has('WALL') then role = 'defence' " +
            "            elseif has('TECH_CENTRE') then role = 'tech' " +
            "            elseif has('STRATEGIC') then role = 'strategic' end " +
            "            n = n + 1 " +
            "            out[n] = string.format('%d|%.4f|%.1f|%.1f|%.1f|%.1f|%d|%s|%d|%s', u.id.index, u.progress / bt, pos.x, pos.y, pos.z, bt, " +
            "              tonumber(g.techNumber) or 0, role, u.upgrader and 1 or 0, tostring(name)) " +
            "          end " +
            "        end " +
            "      end " +
            "    end " +
            "  end " +
            "  __SdbBuilds = table.concat(out, ';') " +
            "end) " +
            "if not ok then __SdbBuildsErr = tostring(err) else __SdbBuildsErr = '' end";

        /// How long a stalled site keeps its last rate before showing as
        /// stalled. The host only sends progress when it changes, and a
        /// half-second poll can miss one update without meaning anything.
        private const float StallAfter = 2.5f;
        private const float RateTau = 1.5f;

        private static void PollBuilds()
        {
            EnsureLuaBridge();
            if (!LuaReady) return;

            // A pause is not a stall: nothing moves because nothing is
            // simulating. Hold every site's clocks at "now" so neither the
            // stall timer nor the next rate sample counts the paused time.
            if (Paused)
            {
                var t = Time.realtimeSinceStartup;
                foreach (var b in _builds.Values) { b.LastSeen = t; b.LastChange = t; }
                return;
            }
            if (!RunLua(BuildChunk)) return;

            var err = GetLuaGlobal("__SdbBuildsErr");
            if (!string.IsNullOrEmpty(err) && !_loggedBuildErr)
            {
                _loggedBuildErr = true;
                _log?.LogWarning($"Build ETA: Lua query failed ({err}); labels stay off.");
            }

            var raw = GetLuaGlobal("__SdbBuilds");
            if (raw == null) return;

            var now = Time.realtimeSinceStartup;
            var seen = new HashSet<int>();
            foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(new[] { '|' }, 10);
                if (f.Length < 10) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, Inv, out var id) ||
                    !float.TryParse(f[1], NumberStyles.Float, Inv, out var frac) ||
                    !float.TryParse(f[2], NumberStyles.Float, Inv, out var x) ||
                    !float.TryParse(f[3], NumberStyles.Float, Inv, out var y) ||
                    !float.TryParse(f[4], NumberStyles.Float, Inv, out var z) ||
                    !float.TryParse(f[5], NumberStyles.Float, Inv, out var bt) ||
                    !int.TryParse(f[6], NumberStyles.Integer, Inv, out var tier)) continue;
                seen.Add(id);

                if (!_builds.TryGetValue(id, out var b))
                {
                    b = new Build { Id = id, LastChange = now };
                    _builds[id] = b;
                }
                b.Name = f[9];
                b.BuildTime = bt;
                b.Tier = tier;
                b.Role = f[7];
                // Sticky: the flag is cleared by the game the moment the
                // upgrade lands, which may be before the last poll sees it.
                if (f[8] == "1") b.IsUpgrade = true;
                b.Position = new Vector3(x, y, z);

                if (b.LastFraction >= 0f)
                {
                    var dt = now - b.LastSeen;
                    if (frac > b.LastFraction + 1e-5f && dt > 0.05f)
                    {
                        var instant = (frac - b.LastFraction) / dt;
                        // First movement snaps, later ones are filtered so
                        // the countdown doesn't jitter with the poll phase.
                        var alpha = b.Rate <= 0f ? 1f : 1f - Mathf.Exp(-dt / RateTau);
                        b.Rate += (instant - b.Rate) * alpha;
                        b.LastChange = now;
                    }
                }
                b.LastFraction = frac;
                b.Fraction = frac;
                b.LastSeen = now;
            }

            // Anything no longer in progress is either finished or gone.
            List<int> drop = null;
            foreach (var kv in _builds)
            {
                if (seen.Contains(kv.Key)) continue;
                drop ??= new List<int>();
                drop.Add(kv.Key);
            }
            if (drop != null)
            {
                foreach (var id in drop)
                {
                    var b = _builds[id];
                    _builds.Remove(id);
                    // Project the last sample forward: a site last seen at
                    // 90% moving at 5%/s half a second ago has finished, one
                    // at 60% that vanished was destroyed or cancelled.
                    var projected = b.Fraction + Mathf.Max(0f, b.Rate) * (now - b.LastSeen);
                    if (projected >= 0.95f) OnBuildFinished?.Invoke(b);
                }
            }

            var list = new List<Build>(_builds.Count);
            foreach (var b in _builds.Values) list.Add(b);
            _buildList = list;
        }

        // ---- upkeep --------------------------------------------------------

        internal static bool ReclaimEnabled;
        internal static bool BuildEtaEnabled;

        /// Called every frame from the plugin's Update.
        internal static void Tick()
        {
            if (!InMatch)
            {
                if (_reclaim.Count > 0 || _builds.Count > 0)
                {
                    _reclaim = new List<Reclaim>();
                    _builds.Clear();
                    _buildList = new List<Build>();
                    // The cache keys are global ids, which restart per match.
                    if (LuaReady) RunLua("__SdbReclaimCache = nil");
                }
                _reclaimAccum = 10f;
                _buildAccum = 10f;
                return;
            }

            if (ReclaimEnabled)
            {
                _reclaimAccum += Time.unscaledDeltaTime;
                if (_reclaimAccum >= 1f)
                {
                    _reclaimAccum = 0f;
                    PollReclaim();
                }
            }
            else if (_reclaim.Count > 0) _reclaim = new List<Reclaim>();

            if (BuildEtaEnabled)
            {
                _buildAccum += Time.unscaledDeltaTime;
                if (_buildAccum >= 0.5f)
                {
                    _buildAccum = 0f;
                    PollBuilds();
                }
            }
            else if (_builds.Count > 0)
            {
                _builds.Clear();
                _buildList = new List<Build>();
            }
        }

        // ---- projection ----------------------------------------------------

        private static Camera _camera;
        private static int _cameraFrame = -1;

        private static Camera SceneCamera()
        {
            if (_cameraFrame == Time.frameCount) return _camera;
            _cameraFrame = Time.frameCount;
            _camera = Camera.main;
            if (_camera == null)
            {
                var all = Camera.allCameras;
                _camera = all != null && all.Length > 0 ? all[0] : null;
            }
            return _camera;
        }

        /// World to GUI coordinates under the plugin's scaled GUI matrix.
        /// False when the point is behind the camera or off screen.
        private static bool Project(Camera camera, Vector3 world, float scale, float logicalWidth, float logicalHeight, out Vector2 gui)
        {
            var p = camera.WorldToScreenPoint(world);
            gui = new Vector2(p.x / scale, (Screen.height - p.y) / scale);
            if (p.z <= 0f) return false;
            const float margin = 40f;
            return gui.x > -margin && gui.x < logicalWidth + margin && gui.y > -margin && gui.y < logicalHeight + margin;
        }

        // ---- drawing -------------------------------------------------------

        private static GUIStyle _stReclaim, _stReclaimEnergy, _stEta, _stEtaName;
        private static Texture2D _texShadow;
        private static bool _stylesReady;

        private static readonly Color EtaColour = new Color(0.85f, 0.92f, 1f, 0.95f);

        private static void EnsureOverlayStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;
            EnsureStyles();
            _stReclaim = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = AlloyColour } };
            _stReclaimEnergy = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = EnergyColour } };
            _stEta = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = EtaColour } };
            _stEtaName = new GUIStyle { fontSize = 10, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.75f, 0.82f, 0.9f, 0.85f) } };
            _texShadow = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _texShadow.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
            _texShadow.Apply();
            _texShadow.hideFlags = HideFlags.HideAndDontSave;
        }

        internal static void ApplyFont(Font font)
        {
            EnsureOverlayStyles();
            if (font == null) return;
            foreach (var s in new[] { _stReclaim, _stReclaimEnergy, _stEta, _stEtaName }) s.font = font;
            _stReclaim.fontSize = 14;
            _stReclaimEnergy.fontSize = 12;
            _stEta.fontSize = 14;
            _stEtaName.fontSize = 11;
        }

        /// Text with a dark backing so it reads over terrain of any colour.
        private static void Label(Rect rect, string text, GUIStyle style)
        {
            var size = style.CalcSize(new GUIContent(text));
            var box = new Rect(rect.center.x - size.x / 2f - 4f, rect.center.y - size.y / 2f - 1f, size.x + 8f, size.y + 2f);
            GUI.DrawTexture(box, _texShadow);
            GUI.Label(box, text, style);
        }

        private static string FmtValue(float v)
        {
            var a = Mathf.Round(v);
            if (a > 999_999f) return (a / 1_000_000f).ToString("0.##", Inv) + "M";
            if (a > 9_999f) return (a / 1_000f).ToString("0.#", Inv) + "K";
            return a.ToString("0", Inv);
        }

        private struct Cell
        {
            public float Alloys, Energy;
            public Vector2 Weighted;
            public float Weight;
        }

        /// Reclaim labels. Everything visible is bucketed into a screen-space
        /// grid and each cell drawn as one summed figure at its value-weighted
        /// centre — so at close zoom each wreck has its own number and zoomed
        /// out a field of wrecks reads as one total, the way FAF's overlay
        /// groups them, instead of a smear of overlapping digits.
        internal static void DrawReclaim(float scale, float logicalWidth, float logicalHeight, float cellPixels, float minValue)
        {
            var items = _reclaim;
            if (items.Count == 0) return;
            var camera = SceneCamera();
            if (camera == null) return;
            EnsureOverlayStyles();

            var cell = Mathf.Max(24f, cellPixels);
            var cells = new Dictionary<long, Cell>();
            foreach (var r in items)
            {
                if (!Project(camera, r.Position, scale, logicalWidth, logicalHeight, out var gui)) continue;
                var cx = (long)Mathf.Floor(gui.x / cell);
                var cy = (long)Mathf.Floor(gui.y / cell);
                var key = (cx << 32) ^ (cy & 0xffffffffL);
                cells.TryGetValue(key, out var c);
                var w = r.Alloys + r.Energy * 0.1f + 1f;
                c.Alloys += r.Alloys;
                c.Energy += r.Energy;
                c.Weighted += gui * w;
                c.Weight += w;
                cells[key] = c;
            }

            // Grid cells alone leave two figures side by side whenever a
            // wreck field straddles a cell edge ("181 125"), so clusters
            // whose centres sit closer than a cell are merged, largest first,
            // until none are.
            var clusters = new List<Cell>(cells.Values);
            var mergeDistance = cell * 0.95f;
            bool merged;
            do
            {
                merged = false;
                clusters.Sort((p, q) => q.Weight.CompareTo(p.Weight));
                for (var i = 0; i < clusters.Count && !merged; i++)
                {
                    var a = clusters[i];
                    var pa = a.Weighted / a.Weight;
                    for (var j = i + 1; j < clusters.Count; j++)
                    {
                        var b = clusters[j];
                        if ((b.Weighted / b.Weight - pa).sqrMagnitude > mergeDistance * mergeDistance) continue;
                        a.Alloys += b.Alloys;
                        a.Energy += b.Energy;
                        a.Weighted += b.Weighted;
                        a.Weight += b.Weight;
                        clusters[i] = a;
                        clusters.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            } while (merged);

            foreach (var c in clusters)
            {
                if (c.Alloys + c.Energy * 0.1f < minValue) continue;
                var at = c.Weighted / c.Weight;
                // Bigger totals get a bigger figure, gently: 13 px for a
                // single tree up to 20 px for a battlefield's worth.
                var magnitude = Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, c.Alloys)) / 4f);
                _stReclaim.fontSize = Mathf.RoundToInt(Mathf.Lerp(13f, 20f, magnitude));
                var y = at.y;
                if (c.Alloys >= 1f)
                {
                    Label(new Rect(at.x, y, 0f, 0f), FmtValue(c.Alloys), _stReclaim);
                    y += _stReclaim.fontSize + 2f;
                }
                // Energy only where it is the point: a pure-energy prop, or
                // a cluster holding a lot of it. A lone tree's hundred energy
                // beside its ten alloys would double every figure on screen.
                if (c.Energy >= 1f && (c.Alloys < 1f || c.Energy >= Mathf.Max(100f, 10f * minValue)))
                {
                    Label(new Rect(at.x, y, 0f, 0f), "E " + FmtValue(c.Energy), _stReclaimEnergy);
                }
            }
        }

        private static string FmtEta(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            if (seconds >= 3600f) return "59:59+";
            var s = Mathf.CeilToInt(seconds);
            return (s / 60).ToString(Inv) + ":" + (s % 60).ToString("00", Inv);
        }

        /// Time-to-finish under each site, plus a thin bar. The game already
        /// draws its own progress bar on the structure, so this sits just
        /// below that and adds only what it lacks: the minutes and seconds.
        ///
        /// A base with fifty things going up would be a wall of labels, so
        /// they are placed soonest-first, one that would overlap an earlier
        /// one is skipped, and no more than `maxLabels` are drawn. Zoomed
        /// out that leaves the few nearest completion; zoomed in on the base
        /// there is room for all of them.
        internal static void DrawBuildEtas(float scale, float logicalWidth, float logicalHeight, int maxLabels)
        {
            var builds = _buildList;
            if (builds.Count == 0) return;
            var camera = SceneCamera();
            if (camera == null) return;
            EnsureOverlayStyles();

            var now = Time.realtimeSinceStartup;
            var ordered = new List<Build>(builds);
            ordered.Sort((p, q) => q.Fraction.CompareTo(p.Fraction));
            var placed = new List<Rect>();
            var drawn = 0;
            foreach (var b in ordered)
            {
                if (drawn >= Math.Max(1, maxLabels)) break;
                if (!Project(camera, b.Position, scale, logicalWidth, logicalHeight, out var gui)) continue;

                var y = gui.y + 22f;
                var footprint = new Rect(gui.x - 30f, y - 10f, 60f, 30f);
                var overlaps = false;
                foreach (var r in placed) if (r.Overlaps(footprint)) { overlaps = true; break; }
                if (overlaps) continue;
                placed.Add(footprint);
                drawn++;

                // Nothing has moved for a while: the estimate stays (there is
                // no better number) and turns red. Before any rate has been
                // measured, the template's own build time stands in.
                var stalled = now - b.LastChange > StallAfter;
                var remaining = 1f - b.Fraction;
                var eta = b.Rate > 1e-4f ? remaining / b.Rate : remaining * Mathf.Max(1f, b.BuildTime);
                var text = FmtEta(eta);
                _stEta.normal.textColor = stalled ? DangerColour : EtaColour;

                Label(new Rect(gui.x, y, 0f, 0f), text, _stEta);

                // Bar: 54 px wide, in the upgrade blue, under the time.
                var barRect = new Rect(gui.x - 27f, y + 11f, 54f, 3f);
                GUI.DrawTexture(new Rect(barRect.x - 1f, barRect.y - 1f, barRect.width + 2f, barRect.height + 2f), _texShadow);
                var track = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.15f);
                GUI.DrawTexture(barRect, _texWhite);
                GUI.color = stalled ? DangerColour : UpgradeColour;
                GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(b.Fraction), barRect.height), _texWhite);
                GUI.color = track;
            }
        }
    }
}
