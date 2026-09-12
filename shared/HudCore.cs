using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SanctuaryHud
{
    // Shared plumbing for the Sanctuary UI mods: the economy snapshot (which
    // doubles as the in-match signal), the ECS idle/commander poll, the Lua
    // bridge for selection and camera moves, and the IMGUI styles.
    //
    // This file is compiled INTO each mod that needs it (via a Compile include
    // of shared\**), so every mod DLL is fully standalone and distributable on
    // its own. Each assembly therefore has its own copy of this state: the
    // frame-stamped SharedTick dedupes within an assembly (a mod calling it
    // from several components), while two mods running side by side each feed
    // their own copy — slightly redundant, deliberately independent.
    //
    // Plugins that use it add `using static SanctuaryHud.HudCore;` so the
    // member names read the same as when this lived inside the HUD plugin.
    internal static class HudCore
    {
        internal static BepInEx.Logging.ManualLogSource _log;

        // ---- economy snapshot, written by the Harmony postfix ----
        internal static readonly object _ecoLock = new object();
        internal static Dictionary<string, float> _eco;
        private static FieldInfo[] _ecoFields;

        // ---- idle-builder polling ----
        private const int IdleIconIndex = 2;
        internal static int _idleCount;
        internal static string _pollStatus = "starting";
        private static float _pollAccum;

        /// One row per tech tier, plus the unit ids behind it for selection.
        internal class IdleGroup
        {
            public string Label;
            public int Tier;
            /// Factory rows only: an index into FactoryDomains.
            public int Domain;
            /// Units in this row. Counted even when the id lookup fails (in
            /// which case the row shows but can't select).
            public int Count;
            public readonly List<int> UnitIds = new List<int>();
            /// This row's build-menu art, as an AssetID index (0 = none).
            /// See ResolveSprite; filled from the Lua template data.
            public uint IconId;
        }

        internal static readonly object _groupLock = new object();
        internal static List<IdleGroup> _idleGroups = new List<IdleGroup>();

        // ---- idle factories (for the IdleEngineers mod) ----
        // Off unless the mod switches it on, so other assemblies' copies of
        // this file never collect factory ids in their army sweep.
        internal static bool _trackIdleFactories = false;
        /// Ordered by domain (land, air, naval), then tier.
        internal static List<IdleGroup> _idleFactoryGroups = new List<IdleGroup>();
        internal static int _idleFactoryCount;
        /// Headings by IdleGroup.Domain, in the order the rows are sorted.
        internal static readonly string[] FactoryDomains = { "", "LAND", "AIR", "NAVAL" };

        // ---- alloy structures, by tier (for the EcoManager mod) ----
        // Extractors are `structure1_t{1,2,3}_alloy` in the icon registry,
        // identically across all three factions. The Tier-3 Alloy Furnace
        // (ues3603/ucs3603/ugs3603, tagged ALLOYS_PRODUCTION rather than
        // ALLOYS_EXTRACTION) carries that same strategic icon, and nothing on
        // the render entity distinguishes the two — so the T3 row counts both.
        // Rows are labelled by tier rather than "extractor" for that reason.
        internal static List<IdleGroup> _alloyGroups = new List<IdleGroup>();
        /// The subset currently upgrading, same tier keys.
        internal static List<IdleGroup> _alloyUpgradingGroups = new List<IdleGroup>();
        internal static int _alloyCount;
        internal static int _alloyUpgradingCount;

        // ---- commander ----
        // "bot2_t1_direct" is the commander icon for all three factions and is
        // used by nothing else, so icon + own army colour pins ours exactly.
        private const string CommanderIconSuffix = "bot2_t1_direct";
        internal static int _commanderLocalIndex = -1;
        internal static int _commanderIconIndex = -1;
        internal static float _commanderHealth;
        internal static float _commanderMaxHealth;

        // Strategic icons are packed into one atlas at load time (the source
        // .dds files are unloaded), so drawing the real icon means sampling
        // the atlas with the icon's own rect.
        internal static Texture _iconAtlas;
        internal static List<Rect> _iconUvRects;
        private static bool _loggedAtlasWait;

        // ---- unit sprites (the build menu's own art) ----------------------
        //
        // Distinct from the strategic icon atlas above. Those are the map
        // symbols; these are the per-template `.sansprite` assets the build and
        // selection panels draw, loaded through the game's own pipeline into a
        // registry keyed by AssetID. SanctuaryUI.Utils.TryGetLoadedSprite is
        // the public way in, and EM.Core.AssetID is a struct wrapping exactly
        // the uint that Lua reports as `general.foregroundIconID.index` (or
        // `backgroundIconID` for the land/air/water plate behind it).

        private static MethodInfo _tryGetSprite;
        private static Type _assetIdType;
        private static bool _spriteBridgeTried;
        private static readonly Dictionary<uint, Sprite> _spriteCache = new Dictionary<uint, Sprite>();
        /// Ids the registry did not have. It only holds what the game has
        /// already loaded, so a miss early in a match must not be permanent:
        /// the ECS poll clears this once a second, which lets late art in
        /// while sparing a reflected call per row per frame.
        private static readonly HashSet<uint> _spriteMisses = new HashSet<uint>();
        /// Misses already logged, so each id is reported once a match.
        private static readonly HashSet<uint> _spriteMissReported = new HashSet<uint>();

        private static void ResolveSpriteBridge()
        {
            if (_spriteBridgeTried) return;
            _spriteBridgeTried = true;
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic).SelectMany(GetTypesSafe).ToList();
                _assetIdType = types.FirstOrDefault(t => t.FullName == "EM.Core.AssetID");
                _tryGetSprite = types.FirstOrDefault(t => t.FullName == "SanctuaryUI.Utils")
                    ?.GetMethod("TryGetLoadedSprite", BindingFlags.Public | BindingFlags.Static);
                if (_tryGetSprite == null || _assetIdType == null)
                    _log?.LogWarning("Unit sprite lookup unavailable; callers fall back to text.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Unit sprite lookup failed to resolve ({e.Message}).");
            }
        }

        /// Each match reloads the sprites through Engine.LoadSprite, so last
        /// match's AssetIDs are not safe to assume still valid.
        internal static void ClearSpriteCache()
        {
            _spriteCache.Clear();
            _spriteMisses.Clear();
            _spriteMissReported.Clear();
        }

        /// The game's own sprite for an AssetID index, or null while the game
        /// has not loaded it (or when the bridge is missing entirely).
        internal static Sprite ResolveSprite(uint index)
        {
            if (index == 0) return null;
            if (_spriteCache.TryGetValue(index, out var cached)) return cached;
            if (_spriteMisses.Contains(index)) return null;

            ResolveSpriteBridge();
            Sprite sprite = null;
            if (_tryGetSprite != null && _assetIdType != null)
            {
                try
                {
                    var args = new[] { Activator.CreateInstance(_assetIdType, index), null };
                    if (_tryGetSprite.Invoke(null, args) is bool ok && ok) sprite = args[1] as Sprite;
                }
                catch { /* one bad id must not take the caller down */ }
            }

            if (sprite != null)
            {
                _spriteCache[index] = sprite;
            }
            else
            {
                _spriteMisses.Add(index);
                // An id the registry has never heard of and one whose art has
                // not loaded yet look the same on screen; only the log can
                // tell them apart.
                if (_tryGetSprite != null && _spriteMissReported.Add(index))
                    _log?.LogInfo($"Build-menu art {index}: not in the loaded sprite registry (yet).");
            }
            return sprite;
        }

        /// Whether art for this id can be drawn right now. Panels ask so every
        /// row shares one indent, rather than rows jumping as their art loads.
        internal static bool HasSprite(uint index) => ResolveSprite(index) != null;

        /// Draws one in IMGUI. A Sprite's pixels are a window into a packed
        /// atlas, so the draw has to be told which corner of the texture.
        /// `fraction` clips it horizontally, for showing one running off an edge.
        internal static void DrawSprite(Rect rect, uint index, float fraction = 1f)
        {
            var sprite = ResolveSprite(index);
            var tex = sprite == null ? null : sprite.texture;
            if (tex == null) return;
            var tr = sprite.textureRect;
            GUI.DrawTextureWithTexCoords(rect, tex,
                new Rect(tr.x / tex.width, tr.y / tex.height, tr.width * fraction / tex.width, tr.height / tex.height));
        }

        private static void ResolveIconAtlas()
        {
            try
            {
                if (_iconLoaderType == null) return;
                _iconAtlas = Shader.GetGlobalTexture(Shader.PropertyToID("_StrategicIconAtlas"));
                if (_iconAtlas == null)
                {
                    if (!_loggedAtlasWait)
                    {
                        _loggedAtlasWait = true;
                        _log.LogInfo("Strategic icon atlas not bound yet; will keep retrying each poll.");
                    }
                    return;
                }

                var rectsMember = _iconLoaderType.GetField("iconRects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    as System.Collections.IEnumerable;
                if (rectsMember == null) return;

                // IconRect.rect is a float4 of atlas *pixels*; convert to UVs.
                var uvs = new List<Rect>();
                float aw = _iconAtlas.width, ah = _iconAtlas.height;
                foreach (var entry in rectsMember)
                {
                    var f4 = entry.GetType().GetField("rect", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                    if (f4 == null) { uvs.Add(new Rect(0, 0, 1, 1)); continue; }
                    var t = f4.GetType();
                    float px = Convert.ToSingle(t.GetField("x").GetValue(f4));
                    float py = Convert.ToSingle(t.GetField("y").GetValue(f4));
                    float pw = Convert.ToSingle(t.GetField("z").GetValue(f4));
                    float ph = Convert.ToSingle(t.GetField("w").GetValue(f4));
                    // The source .dds icons are stored top-down, so sample with
                    // a negative height to flip them the right way up for GUI.
                    uvs.Add(new Rect(px / aw, (py + ph) / ah, pw / aw, -ph / ah));
                }
                _iconUvRects = uvs;
                _log.LogInfo($"Strategic icon atlas: {_iconAtlas.width}x{_iconAtlas.height}, {uvs.Count} rects.");
            }
            catch (Exception e)
            {
                if (!_loggedAtlasWait)
                {
                    _loggedAtlasWait = true;
                    _log.LogWarning($"Icon atlas unavailable ({e.Message}); commander icon falls back to a glyph.");
                }
            }
        }

        // The game's own selection system also sees our click (IMGUI doesn't
        // block it) and clears the selection when it lands on open ground. So
        // queue the selection and apply it a couple of frames after the mouse
        // is released, once the game has finished processing the click.
        internal static List<int> _pendingSelection;
        internal static bool _pendingCommander;
        internal static int _applyOnFrame = -1;

        /// Bound by the HUD plugin (which owns the commander widget); other
        /// assemblies' copies stay null and fall back to the default factor.
        internal static ConfigEntry<float> _cfgCommanderZoom = null;

        // ---- economy capture ----------------------------------------------

        // Harmony-parameterised: several mods need the economy stream (it is
        // also the in-match signal), and each must survive the others being
        // unloaded. Double-patching is harmless — the postfix just rewrites
        // the same snapshot — and each mod unpatches only its own instance.
        internal static void ApplyEconomyPatch(Harmony harmony)
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(GetTypesSafe)
                .Where(t => t.Name == "EconomyPanelUI");

            var postfix = new HarmonyMethod(typeof(HudCore), nameof(EconomyValuesPostfix));
            var patched = 0;

            foreach (var type in types)
            {
                foreach (var method in type
                             .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                             .Where(m => !m.IsAbstract && !m.ContainsGenericParameters)
                             .Where(m => m.GetParameters().Any(p => p.ParameterType.Name.Contains("UIEconomyValues"))))
                {
                    harmony.Patch(method, postfix: postfix);
                    patched++;
                }
            }
            _log?.LogInfo($"Economy hook: patched {patched} method(s).");
        }

        internal static void EconomyValuesPostfix(object __instance, object[] __args)
        {
            var box = __args?.FirstOrDefault(a => a != null && a.GetType().Name.Contains("UIEconomyValues"));
            if (box == null) return;

            _ecoFields ??= box.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var snapshot = new Dictionary<string, float>(_ecoFields.Length);
            foreach (var f in _ecoFields)
            {
                var v = f.GetValue(box);
                snapshot[f.Name] = v is IConvertible c ? Convert.ToSingle(c) : 0f;
            }
            lock (_ecoLock)
            {
                // The patch lands on both SetAlloyValues and SetEnergyValues,
                // which the game calls back to back with the same struct, so
                // every Lua update arrives here twice. Count it once, so a
                // consumer smoothing per update runs at the real data rate.
                if (!SameSnapshot(_eco, snapshot)) _ecoSequence++;
                _eco = snapshot;
            }
            if (__instance is Component panel) _ecoPanel = panel;
            // The host streams economy continuously during a match and never
            // outside one, so this doubles as the "am I in a game?" signal.
            _lastEcoRealtime = Time.realtimeSinceStartup;
        }

        private static bool SameSnapshot(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            }
            return true;
        }

        private static float _lastEcoRealtime = -999f;

        /// Bumps whenever the economy snapshot changes, so a consumer can
        /// tell "first data of a match" and "new data" from a re-read. It
        /// does not tick on every update: a steady economy sends identical
        /// snapshots ten times a second, so never step a filter on this.
        internal static int _ecoSequence;

        /// The game's own EconomyPanelUI, caught from the postfix. Its
        /// visibility is the game's idea of "in a match", which unlike the
        /// stream itself survives a pause of any length. Also what the HUD
        /// hides when asked to replace the built-in readouts.
        internal static Component _ecoPanel;
        private static PropertyInfo _ecoPanelVisible;
        private static bool _ecoPanelVisibleResolved;

        private static bool EcoPanelVisible()
        {
            var panel = _ecoPanel;
            if (panel == null) return false;   // Unity null: destroyed with the scene
            try
            {
                if (!_ecoPanelVisibleResolved)
                {
                    _ecoPanelVisibleResolved = true;
                    _ecoPanelVisible = panel.GetType().GetProperty("IsVisible", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_ecoPanelVisible == null) return false;
                return panel.gameObject.activeInHierarchy && _ecoPanelVisible.GetValue(panel) is bool b && b;
            }
            catch { return false; }
        }

        /// True while the game is in a match. The economy stream is the
        /// primary signal, with a grace period for loading hitches; the
        /// game's economy panel staying visible carries it through a pause,
        /// where the stream stops for as long as the pause lasts (the HUD
        /// used to drop out five seconds into every pause while the game's
        /// own readouts stayed put). Lua hides that panel when the match
        /// ends, and the scene change destroys it.
        internal static bool InMatch =>
            Time.realtimeSinceStartup - _lastEcoRealtime < 5f ||
            (Time.realtimeSinceStartup - _lastEcoRealtime < 3600f && EcoPanelVisible());

        /// True while the host's economy stream has gone quiet mid-match,
        /// which is what a pause looks like from here: the postfix fires
        /// every tick otherwise, identical values or not. Anything that
        /// measures progress against real time should freeze while this is
        /// set rather than read the silence as a stall.
        internal static bool Paused => InMatch && Time.realtimeSinceStartup - _lastEcoRealtime > 0.75f;

        // ---- idle-builder polling (reflection over Unity.Entities) --------

        private static Type _iconElemType;
        private static Type _entityType;
        private static FieldInfo _allWorldsField;
        private static PropertyInfo _entityManagerProp;
        private static MethodInfo _componentTypeReadOnly;
        private static MethodInfo _createQueryMi;
        private static MethodInfo _getBufferMi;
        private static FieldInfo _iconEnabledField;
        private static FieldInfo _iconIndexField;
        private static int _idleImageIndex = -1;
        private static int _upgradeImageIndex = -1;
        private static bool _loggedUpgradeIndexWait;
        private static object _allocatorTemp;
        private static bool _ecsResolved;
        private static bool _ecsResolveFailed;
        private static float _nextResolveRetry;
        private static bool _loggedResolveFail;
        private static bool _loggedIdleIndexWait;
        // Cached for the late-resolve retries (see PollIdleBuilders).
        private static Type _cliType;
        private static Type _iconLoaderType;
        private static int _idleAllCount;
        private static int _idleBuilderCount;

        // ---- ownership filter: local army colour matching ----
        // Render entities carry no army id, but every unit's renderer tint is
        // ArmyColors[armyID] from common/colors.lua (players cannot pick
        // custom colours in the demo lobby). So: local clientID -> armyID via
        // the lobby statics, armyID -> colour via the parsed Lua table, then
        // match each entity's RenderInstanceData.instanceData0 against it.
        private static MethodInfo _getRendererMi;
        private static FieldInfo _renderInstanceDataField;
        private static FieldInfo _instanceData0Field;
        private static FieldInfo _f4x, _f4y, _f4z;
        private static Vector4[] _armyColours;
        private static MethodInfo _getClientIdMi;
        private static Type _lobbyInfoType;
        /// Our army's colour, for tinting the commander icon like the game does.
        internal static Color? _ownArmyColourUi;

        private static void ResolveOwnership(List<Assembly> assemblies, Type emType)
        {
            var rendererType = assemblies.SelectMany(GetTypesSafe).First(t => t.FullName == "EM.Components.RendererComponent");
            _getRendererMi = emType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                .MakeGenericMethod(rendererType);
            _renderInstanceDataField = rendererType.GetField("RenderInstanceData");
            _instanceData0Field = _renderInstanceDataField.FieldType.GetField("instanceData0");
            _f4x = _instanceData0Field.FieldType.GetField("x");
            _f4y = _instanceData0Field.FieldType.GetField("y");
            _f4z = _instanceData0Field.FieldType.GetField("z");

            // ownClientID lives in a Burst SharedStatic, but the Lua-facing
            // getter is a plain managed method we can just call.
            _getClientIdMi = assemblies.SelectMany(GetTypesSafe)
                .FirstOrDefault(t => t.FullName == "EM.Lua.Client.ClientLuaInterface")
                ?.GetMethod("GetClientID", BindingFlags.Public | BindingFlags.Static);
            _lobbyInfoType = assemblies.SelectMany(GetTypesSafe).FirstOrDefault(t => t.Name == "LobbyInformationManaged");

            var colorsPath = System.IO.Path.Combine(Paths.GameRootPath, "LJ", "lua", "common", "colors.lua");
            _armyColours = ParseArmyColours(colorsPath);
            _log.LogInfo($"Ownership filter: {_armyColours?.Length ?? 0} army colours, GetClientID {(_getClientIdMi != null ? "found" : "missing")}, lobbyInfo {(_lobbyInfoType != null ? "found" : "missing")}.");
        }

        /// A Lua numeric literal, or a division of two (`216/255`).
        private static float LuaNumber(string s)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var slash = s.IndexOf('/');
            if (slash < 0) return float.Parse(s.Trim(), inv);
            var a = float.Parse(s.Substring(0, slash).Trim(), inv);
            var b = float.Parse(s.Substring(slash + 1).Trim(), inv);
            return b == 0f ? 0f : a / b;
        }

        private static Vector4[] ParseArmyColours(string path)
        {
            try
            {
                var text = System.IO.File.ReadAllText(path);
                var colours = new Dictionary<string, Vector4>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             text, "\\[\"(\\w+)\"\\]\\s*=\\s*EngineClasses\\.float4\\(([^)]+)\\)"))
                {
                    // Components are literals or, since the 2026-09-04 update,
                    // byte fractions such as `216/255`.
                    var parts = m.Groups[2].Value.Split(',').Select(LuaNumber).ToArray();
                    if (parts.Length >= 3) colours[m.Groups[1].Value] = new Vector4(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : 1f);
                }

                var armyBlock = System.Text.RegularExpressions.Regex.Match(text, "ArmyColors\\s*=\\s*\\{([^}]+)\\}");
                if (!armyBlock.Success) return null;
                var list = new List<Vector4>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(armyBlock.Groups[1].Value, "Colors\\.(\\w+)"))
                {
                    if (colours.TryGetValue(m.Groups[1].Value, out var c)) list.Add(c);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Army colour parse failed: {e.Message}");
                return null;
            }
        }

        /// The authoritative ownership signal: the client marks exactly one
        /// army as focused (the one you are playing), and that army object
        /// carries the very colour the renderer tints its units with. Army
        /// colours are assigned by registration-order `colorId`, NOT by lobby
        /// armyID, so deriving the colour from the lobby can land on another
        /// player's colour — which is how enemy engineers leaked into the
        /// idle list. Ask the game instead of inferring.
        private static Vector4? FocusedArmyColourFromLua()
        {
            if (_getLuaGlobal == null) return null;
            // Guards the read-back below too: _getLuaGlobal goes straight into
            // LuaJIT with the same null-able state handle that RunLua checks.
            if (_luaStateReady == null || !_luaStateReady()) return null;
            try
            {
                // Exactly one focused army is a player; a replay's all-armies
                // view marks every army focused, and an observer with no seat
                // has none. Neither owns anything, so neither gets a colour,
                // and everything downstream (idle rows, commander, alerts)
                // stays quiet rather than reporting for both sides.
                RunLua(
                    "__SdbOwn = '' " +
                    "local own, n = nil, 0 " +
                    "for id, a in pairs(Armies or {}) do " +
                    "  if a.focused and not a.civilian then n = n + 1 own = a end " +
                    "end " +
                    "if n == 1 and own.color then " +
                    "  __SdbOwn = string.format('%f,%f,%f', own.color.x, own.color.y, own.color.z) " +
                    "end");

                var raw = _getLuaGlobal("__SdbOwn");
                // The client answered (possibly "nobody"): that answer stands,
                // and the lobby-derived guess below must not overrule it.
                _luaOwnerAnswered = raw != null;
                if (string.IsNullOrEmpty(raw)) return null;

                var parts = raw.Split(',');
                if (parts.Length < 3) return null;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var colour = new Vector4(
                    float.Parse(parts[0], ci), float.Parse(parts[1], ci), float.Parse(parts[2], ci), 1f);

                if (!_loggedOwnColour)
                {
                    _loggedOwnColour = true;
                    _log.LogInfo($"Ownership: focused army colour {colour.x:0.###},{colour.y:0.###},{colour.z:0.###} (from client Lua).");
                }

                var lift = Mathf.Max(0.35f, Mathf.Max(colour.x, Mathf.Max(colour.y, colour.z)));
                var normalised = new Color(colour.x / lift, colour.y / lift, colour.z / lift, 1f);
                _ownArmyColourUi = Color.Lerp(normalised, Color.white, 0.7f);
                return colour;
            }
            catch (Exception e)
            {
                if (!_loggedOwnColour)
                {
                    _loggedOwnColour = true;
                    _log.LogWarning($"Could not read focused army colour from Lua: {e.Message}");
                }
                return null;
            }
        }

        private static bool _loggedOwnColour;
        private static bool _luaOwnerAnswered;

        private static Vector4? LocalArmyColour()
        {
            // Prefer the game's own answer; the lobby-derived guess is only a
            // fallback for when the Lua bridge isn't available. When the
            // client did answer and named nobody (an observer, or a replay's
            // all-armies view) that is final: the lobby would otherwise hand
            // back the recording player's seat and alert for them.
            var fromLua = FocusedArmyColourFromLua();
            if (fromLua != null) return fromLua;
            if (_luaOwnerAnswered) return null;

            try
            {
                if (_armyColours == null || _getClientIdMi == null || _lobbyInfoType == null) return null;

                var ownClientId = Convert.ToInt32(_getClientIdMi.Invoke(null, null));
                if (ownClientId == 254) return null; // UnknownClientID

                var lobby = (object)_lobbyInfoType.GetField("currentLobbyInformation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? _lobbyInfoType.GetProperty("currentLobbyInformation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                var players = lobby?.GetType().GetField("playersInformation")?.GetValue(lobby) as System.Collections.IEnumerable;
                if (players == null) return null;

                foreach (var p in players)
                {
                    var t = p.GetType();
                    if (Convert.ToInt32(t.GetField("clientID").GetValue(p)) == ownClientId)
                    {
                        var armyId = Convert.ToInt32(t.GetField("armyID").GetValue(p));
                        if (armyId >= 1 && armyId <= _armyColours.Length)
                        {
                            var c = _armyColours[armyId - 1];
                            // Team colours are dark by design — normalise, then
                            // pull most of the way to white so the icon reads
                            // clearly against the dark panel.
                            var lift = Mathf.Max(0.35f, Mathf.Max(c.x, Mathf.Max(c.y, c.z)));
                            var normalised = new Color(c.x / lift, c.y / lift, c.z / lift, 1f);
                            _ownArmyColourUi = Color.Lerp(normalised, Color.white, 0.7f);
                            return c;
                        }
                    }
                }
            }
            catch
            {
                // fall through to unfiltered counting
            }
            return null;
        }

        private static bool ColourMatches(object em, object entity, Vector4 target)
        {
            try
            {
                var renderer = _getRendererMi.Invoke(em, new[] { entity });
                var instance = _instanceData0Field.GetValue(_renderInstanceDataField.GetValue(renderer));
                var dx = Convert.ToSingle(_f4x.GetValue(instance)) - target.x;
                var dy = Convert.ToSingle(_f4y.GetValue(instance)) - target.y;
                var dz = Convert.ToSingle(_f4z.GetValue(instance)) - target.z;
                return dx * dx + dy * dy + dz * dz < 0.003f;
            }
            catch
            {
                // Fail closed: an entity we cannot attribute is not counted.
                return false;
            }
        }

        // The strategic icon (slot 0) image name encodes the unit's tier and
        // role, e.g. "bot1_t2_engineer_normal" — so the icon registry gives us
        // a tech breakdown without touching unit templates.
        private static FieldInfo _localIdField;
        private static MethodInfo _getLocalIdMi;
        private static Dictionary<int, string> _iconNamesByIndex;

        private static string IconName(int registryIndex)
        {
            return _iconNamesByIndex != null && _iconNamesByIndex.TryGetValue(registryIndex, out var name) ? name : null;
        }

        // IconLoader.iconLookup maps name -> registry index; invert it so we
        // can read a unit's tier off its strategic icon. Icons are registered
        // by Lua during match load, so the table can legitimately be empty on
        // the first polls of a session — only a non-empty result is kept, and
        // callers retry until then.
        private static bool _loggedIconNamesWait;

        private static void ResolveIconNames()
        {
            try
            {
                if (_iconLoaderType == null) return;
                var lookupMember = (object)_iconLoaderType.GetField("iconLookup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? _iconLoaderType.GetProperty("iconLookup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (lookupMember == null) return;

                var map = new Dictionary<int, string>();
                foreach (var entry in (System.Collections.IEnumerable)lookupMember)
                {
                    var t = entry.GetType();
                    var k = t.GetProperty("Key")?.GetValue(entry)?.ToString();
                    var v = t.GetProperty("Value")?.GetValue(entry);
                    if (k != null && v != null) map[Convert.ToInt32(v)] = k;
                }
                if (map.Count == 0)
                {
                    if (!_loggedIconNamesWait)
                    {
                        _loggedIconNamesWait = true;
                        _log.LogInfo("Icon name table empty (icons not registered yet); will keep retrying each poll.");
                    }
                    return;
                }
                _iconNamesByIndex = map;
                _log.LogInfo($"Icon names: {map.Count} entries (idle = {IconName(_idleImageIndex)}).");
            }
            catch (Exception e)
            {
                if (!_loggedIconNamesWait)
                {
                    _loggedIconNamesWait = true;
                    _log.LogWarning($"Icon name table unavailable (no tech split yet): {e.Message}");
                }
            }
        }

        // Adornment images are registered by Lua during match load, often
        // after the economy stream (our in-match signal) has started — so
        // these can fail on the first polls and are retried until they stick.
        private static int ResolveAdornmentIndex(string iconName)
        {
            try
            {
                if (_cliType == null) return -1;
                var checkValidIcon = _cliType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CheckValidIcon");
                if (checkValidIcon == null) return -1;

                var ps = checkValidIcon.GetParameters();
                var args = new object[ps.Length];
                var stringSeen = 0;
                var outPos = -1;
                for (var i = 0; i < ps.Length; i++)
                {
                    if (ps[i].IsOut) { outPos = i; continue; }
                    if (ps[i].ParameterType == typeof(string))
                    {
                        args[i] = stringSeen++ == 0 ? "SanctuaryHud" : iconName;
                    }
                }
                // (functionName, iconName, out index) — if there is only
                // one string param it is the icon name.
                if (stringSeen == 1) args[Array.FindIndex(ps, p => p.ParameterType == typeof(string))] = iconName;
                var ok = checkValidIcon.Invoke(null, args);
                if (ok is bool b && b && outPos >= 0) return Convert.ToInt32(args[outPos]);
                return -1;
            }
            catch
            {
                return -1;
            }
        }

        private static void TryResolveIdleImageIndex()
        {
            _idleImageIndex = ResolveAdornmentIndex("strategic_icon_adornment_idle");
            if (_idleImageIndex >= 0)
            {
                _log.LogInfo($"ECS idle poll: idle image registry index = {_idleImageIndex}.");
            }
            else if (!_loggedIdleIndexWait)
            {
                _loggedIdleIndexWait = true;
                _log.LogInfo("ECS idle poll: idle image not registered yet; will keep retrying each poll.");
            }
        }

        // The upgrade adornment is what ClientUnit:CheckShowUpgradingAdornment
        // enables (`icons.Upgrade:SetEnabled(self:IsUpgradeQueued())`), so it
        // is the game's own "this building is upgrading" signal.
        private static void TryResolveUpgradeImageIndex()
        {
            _upgradeImageIndex = ResolveAdornmentIndex("strategic_icon_adornment_upgrade");
            if (_upgradeImageIndex >= 0)
            {
                _log.LogInfo($"ECS poll: upgrade image registry index = {_upgradeImageIndex}.");
            }
            else if (!_loggedUpgradeIndexWait)
            {
                _loggedUpgradeIndexWait = true;
                _log.LogInfo("ECS poll: upgrade image not registered yet; will keep retrying each poll.");
            }
        }

        // ---- Lua bridge: run a snippet in the client's own VM ----
        // Selection lives in client Lua and needs live entity objects, so
        // rather than marshalling them we ask the client VM to do the work.
        // Client-side only: no sim state, no hashed files touched.
        private static Func<string, int> _runLuaChunk;
        private static Func<string, string> _getLuaGlobal;

        /// True once the client VM exists and can be called into.
        internal static bool LuaReady => _luaStateReady != null && _luaStateReady();

        /// Resolves the Lua bridge on demand, outside the in-match poll that
        /// normally does it. Replay playback needs to talk to the client VM
        /// before the economy stream (and so InMatch) has started, and a mod
        /// that never polls the ECS still wants RunLua to work.
        internal static void EnsureLuaBridge()
        {
            if (_runLuaChunk != null) return;
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
                ResolveLuaBridge(assemblies);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Lua bridge resolve failed: {e.Message}");
            }
        }

        /// Reads a global out of the client VM as a string, or null.
        internal static string GetLuaGlobal(string name)
        {
            if (_getLuaGlobal == null || !LuaReady) return null;
            try { return _getLuaGlobal(name); }
            catch { return null; }
        }

        /// True once the client VM actually exists. Outside a match
        /// ClientLuaInterface.Data.luaState is a null handle, and handing that
        /// to luaL_dostring dereferences null inside LuaJIT — a native access
        /// violation that no managed try/catch can stop, so the process dies.
        /// Everything that reaches into Lua has to check this first.
        private static Func<bool> _luaStateReady;

        // ClientLuaInterface.Data is `ref Unmanaged` over a Burst SharedStatic,
        // and reflection refuses to invoke ByRef-returning getters. Emit a tiny
        // method that does it in IL instead: get the ref, load .luaState off it,
        // and call luaL_dostring.
        private static void ResolveLuaBridge(List<Assembly> assemblies)
        {
            var luaJit = assemblies.SelectMany(GetTypesSafe).FirstOrDefault(t => t.Name == "LuaJIT");
            var doString = luaJit?.GetMethod("luaL_dostring", BindingFlags.Public | BindingFlags.Static);

            var cli = assemblies.SelectMany(GetTypesSafe).FirstOrDefault(t => t.FullName == "EM.Lua.Client.ClientLuaInterface");
            var dataGetter = cli?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();

            var unmanagedType = dataGetter?.ReturnType;
            if (unmanagedType != null && unmanagedType.IsByRef) unmanagedType = unmanagedType.GetElementType();
            var stateField = unmanagedType?.GetField("luaState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (doString == null || dataGetter == null || stateField == null)
            {
                _log.LogWarning($"Lua bridge unavailable: dostring {(doString != null ? "ok" : "missing")}, " +
                                $"getter {(dataGetter != null ? "ok" : "missing")}, state {(stateField != null ? "ok" : "missing")}.");
                return;
            }

            var dm = new System.Reflection.Emit.DynamicMethod(
                "SanctuaryHud_RunLua", typeof(int), new[] { typeof(string) }, typeof(HudCore), skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, dataGetter);      // ref Unmanaged
            il.Emit(System.Reflection.Emit.OpCodes.Ldfld, stateField);     // lua_State
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);               // chunk
            il.Emit(System.Reflection.Emit.OpCodes.Call, doString);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            _runLuaChunk = (Func<string, int>)dm.CreateDelegate(typeof(Func<string, int>));

            // lua_State is a struct wrapping a single nuint Handle, so the
            // readiness check is Data.luaState.Handle != 0.
            var handleField = stateField.FieldType.GetField("Handle", BindingFlags.Public | BindingFlags.Instance);
            if (handleField != null)
            {
                var rm = new System.Reflection.Emit.DynamicMethod(
                    "SanctuaryHud_LuaStateReady", typeof(bool), Type.EmptyTypes, typeof(HudCore), skipVisibility: true);
                var ril = rm.GetILGenerator();
                ril.Emit(System.Reflection.Emit.OpCodes.Call, dataGetter);   // ref Unmanaged
                ril.Emit(System.Reflection.Emit.OpCodes.Ldfld, stateField);  // lua_State
                ril.Emit(System.Reflection.Emit.OpCodes.Ldfld, handleField); // nuint
                ril.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                ril.Emit(System.Reflection.Emit.OpCodes.Conv_U);
                ril.Emit(System.Reflection.Emit.OpCodes.Cgt_Un);             // handle != 0
                ril.Emit(System.Reflection.Emit.OpCodes.Ret);
                _luaStateReady = (Func<bool>)rm.CreateDelegate(typeof(Func<bool>));
            }
            else
            {
                // Without a way to test the handle, calling in is a coin flip
                // between working and killing the process. Stay out.
                _log.LogWarning("Lua bridge disabled: lua_State.Handle not found, so the null-state guard " +
                                "can't be emitted. Selection and camera jumps will be inert.");
                _runLuaChunk = null;
                return;
            }

            // Reading back out of Lua: push a global, convert to string, pop.
            var getGlobal = luaJit.GetMethod("lua_getglobal", BindingFlags.Public | BindingFlags.Static);
            var toString = luaJit.GetMethod("lua_tostring", BindingFlags.Public | BindingFlags.Static);
            var setTop = luaJit.GetMethod("lua_settop", BindingFlags.Public | BindingFlags.Static);
            if (getGlobal != null && toString != null && setTop != null)
            {
                var gm = new System.Reflection.Emit.DynamicMethod(
                    "SanctuaryHud_GetLuaGlobal", typeof(string), new[] { typeof(string) }, typeof(HudCore), skipVisibility: true);
                var gil = gm.GetILGenerator();
                var stateLocal = gil.DeclareLocal(stateField.FieldType);
                var resultLocal = gil.DeclareLocal(typeof(string));

                gil.Emit(System.Reflection.Emit.OpCodes.Call, dataGetter);
                gil.Emit(System.Reflection.Emit.OpCodes.Ldfld, stateField);
                gil.Emit(System.Reflection.Emit.OpCodes.Stloc, stateLocal);

                gil.Emit(System.Reflection.Emit.OpCodes.Ldloc, stateLocal);
                gil.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                gil.Emit(System.Reflection.Emit.OpCodes.Call, getGlobal);

                gil.Emit(System.Reflection.Emit.OpCodes.Ldloc, stateLocal);
                gil.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_M1);
                gil.Emit(System.Reflection.Emit.OpCodes.Call, toString);
                gil.Emit(System.Reflection.Emit.OpCodes.Stloc, resultLocal);

                gil.Emit(System.Reflection.Emit.OpCodes.Ldloc, stateLocal);
                gil.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_S, (sbyte)-2);
                gil.Emit(System.Reflection.Emit.OpCodes.Call, setTop);

                gil.Emit(System.Reflection.Emit.OpCodes.Ldloc, resultLocal);
                gil.Emit(System.Reflection.Emit.OpCodes.Ret);

                _getLuaGlobal = (Func<string, string>)gm.CreateDelegate(typeof(Func<string, string>));
            }

            _log.LogInfo($"Lua bridge: ready (emitted), read-back {(_getLuaGlobal != null ? "ok" : "missing")}.");
        }

        /// Runs a chunk in the client's own VM. Returns false (rather than
        /// throwing) when there is no VM yet, so callers can simply retry.
        internal static bool RunLua(string chunk)
        {
            try
            {
                if (_runLuaChunk == null) return false;
                // No VM yet (menu, loading, or after a match) — calling in
                // would segfault the process rather than throw.
                if (_luaStateReady == null || !_luaStateReady()) return false;
                var code = _runLuaChunk(chunk);
                if (code != 0) _log.LogWarning($"Lua chunk failed (code {code}): {chunk}");
                return code == 0;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Lua bridge call failed: {e.Message}");
                return false;
            }
        }

        /// Selects the given units via the client's own selection system.
        /// SetSelectedEntities wants a table keyed by LocalID index holding the
        /// unit objects, but __Entities.Units is keyed by GlobalID index — so
        /// the chunk walks the unit table and matches on each unit's localId.
        internal static void SelectUnits(List<int> localIdIndices)
        {
            if (localIdIndices == null || localIdIndices.Count == 0) return;
            var ids = string.Join(",", localIdIndices.Distinct().Take(200).Select(i => $"[{i}]=true"));
            var chunk =
                "local ok, err = pcall(function() " +
                "local sel = Import('client/input/selectionSystem.lua') " +
                $"local want = {{{ids}}} " +
                "local out = {} " +
                "for _, u in pairs(__Entities.Units) do " +
                "  local li = u.localId and u.localId.index " +
                "  if li and want[li] then out[li] = u end " +
                "end " +
                "sel.SetSelectedEntities(out) " +
                "end) " +
                "if not ok then Warn('SanctuaryHud select: ' .. tostring(err)) end";
            RunLua(chunk);
        }

        private static MethodInfo _getPairedGlobalMi;
        private static FieldInfo _pairedGlobalField;
        private static MethodInfo _getHealthMi;
        private static MethodInfo _getMaxHealthMi;

        // Health lives on the sim-side entity, not the render entity, so hop
        // via LocalPairedGlobalIDComponent and use the engine's own accessors.
        private static void RecordCommander(object em, object entity)
        {
            try
            {
                if (_localIdField != null && _getLocalIdMi != null)
                {
                    var localComponent = _getLocalIdMi.Invoke(em, new[] { entity });
                    var localId = _localIdField.GetValue(localComponent);
                    var indexField = localId.GetType().GetField("index", BindingFlags.Public | BindingFlags.Instance);
                    if (indexField != null) _commanderLocalIndex = Convert.ToInt32(indexField.GetValue(localId));
                }

                if (_getPairedGlobalMi == null || _getHealthMi == null) return;
                var pairedComponent = _getPairedGlobalMi.Invoke(em, new[] { entity });
                _commanderGlobalId = _pairedGlobalField.GetValue(pairedComponent);
                ReadCommanderHealth(_commanderGlobalId);
            }
            catch
            {
                // Keep the last known values rather than flickering to zero.
            }
        }

        /// The commander's sim-side id, kept so the health can be re-read
        /// between the once-a-second ECS polls (the alerts want damage
        /// noticed within a quarter second, not a second later).
        private static object _commanderGlobalId;

        /// Re-reads the commander's health off its cached global id. False
        /// when there is no commander on record or the engine refuses the
        /// id (the entity is gone); the last values are kept either way.
        internal static bool RefreshCommanderHealth()
        {
            if (_commanderLocalIndex < 0 || _commanderGlobalId == null || _getHealthMi == null) return false;
            try { return ReadCommanderHealth(_commanderGlobalId); }
            catch { return false; }
        }

        private static bool ReadCommanderHealth(object globalId)
        {
            var args = new[] { globalId, null };
            var code = _getHealthMi.Invoke(null, args);
            // EngineErrorCode.Success is 0; anything else means the id no
            // longer names a live entity and the value is meaningless.
            if (code != null && Convert.ToInt32(code) != 0) return false;
            _commanderHealth = Convert.ToSingle(args[1] ?? 0f);

            if (_getMaxHealthMi != null)
            {
                var maxArgs = new[] { globalId, null };
                _getMaxHealthMi.Invoke(null, maxArgs);
                _commanderMaxHealth = Convert.ToSingle(maxArgs[1] ?? 0f);
            }
            return true;
        }

        /// Selects the commander and flies the camera to it, reusing the same
        /// camera fit the game's own control-group focus uses.
        internal static void GoToCommander()
        {
            if (_commanderLocalIndex < 0) return;

            // FitCameraToPositions zooms to the bounding box of what it is
            // given, so a single point collapses to the minimum height. Hand it
            // a square sized from the current camera height instead, which
            // keeps roughly the zoom the player is already using.
            var camera = Camera.main;
            if (camera == null)
            {
                var all = Camera.allCameras;
                camera = all != null && all.Length > 0 ? all[0] : null;
            }
            var camHeight = camera != null ? Mathf.Abs(camera.transform.position.y) : 400f;
            var zoomFactor = _cfgCommanderZoom?.Value ?? 0.5f;
            var radius = Mathf.Clamp(camHeight * zoomFactor, 40f, 4000f);

            var chunk =
                "local ok, err = pcall(function() " +
                "local sel = Import('client/input/selectionSystem.lua') " +
                "local cam = Import('client/input/cameraController.lua') " +
                $"local want = {_commanderLocalIndex} " +
                $"local r = {radius.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} " +
                "for _, u in pairs(__Entities.Units) do " +
                "  local li = u.localId and u.localId.index " +
                "  if li == want then " +
                "    sel.SetSelectedEntities({[li] = u}) " +
                "    local p = u:GetPosition() " +
                "    cam.FitCameraToPositions({ " +
                "      EngineClasses.float3(p.x - r, p.y, p.z - r), " +
                "      EngineClasses.float3(p.x + r, p.y, p.z + r) }) " +
                "    return " +
                "  end " +
                "end " +
                "end) " +
                "if not ok then Warn('SanctuaryHud commander: ' .. tostring(err)) end";
            RunLua(chunk);
        }

        private static void RecordIdle(object em, object entity, object buffer, PropertyInfo itemGetter, int bufLength,
            Dictionary<int, IdleGroup> groups)
        {
            try
            {
                // The strategic icon (slot 0) is named shape_tier_role, e.g.
                // "land1_t2_engineer_normal". Engineers carry the "engineer"
                // role; factories carry what they build (land/air/naval), and
                // commanders carry "direct" — so this is what separates them.
                if (bufLength == 0) return;
                var strategic = itemGetter.GetValue(buffer, new object[] { 0 });
                var name = IconName(Convert.ToInt32(_iconIndexField.GetValue(strategic)));
                if (string.IsNullOrEmpty(name)) return;

                var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\w+?)_t(\d)_(\w+?)(_normal)?$");
                if (!m.Success) return;

                var shape = m.Groups[1].Value;
                var tier = int.Parse(m.Groups[2].Value);
                var role = m.Groups[3].Value;

                // The commander gets its own row (sorted first). Otherwise:
                // mobile engineers only — skip factories and other builders,
                // and skip structure-shaped "engineer" units (build stations).
                var isCommander = name.StartsWith(CommanderIconSuffix);
                if (!isCommander && (role != "engineer" || shape.StartsWith("structure"))) return;

                var key = isCommander ? 0 : tier;
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new IdleGroup
                    {
                        Tier = key,
                        // "COM", not "COMMANDER": the row carries the
                        // commander's own art, so the label only confirms it,
                        // and the long word would set the panel's width alone.
                        Label = isCommander ? "COM" : $"T{tier}",
                        IconId = RowIcon(isCommander ? "cmd" : "eng" + tier),
                    };
                    groups[key] = group;
                }
                group.Count++;

                // LocalID index is the key the selection system uses.
                if (_localIdField != null && _getLocalIdMi != null)
                {
                    var component = _getLocalIdMi.Invoke(em, new[] { entity });
                    var localId = _localIdField.GetValue(component);
                    var indexField = localId.GetType().GetField("index", BindingFlags.Public | BindingFlags.Instance);
                    if (indexField != null) group.UnitIds.Add(Convert.ToInt32(indexField.GetValue(localId)));
                }
            }
            catch
            {
                // A row without ids still counts; it just won't be clickable.
            }
        }

        // ---- extractor identity, from the client's own tag tables ----
        // The strategic icon cannot answer this: alloy extractors, alloy
        // storages (ues1602 &c.) and the T3 alloy furnace (ues3603) all carry
        // the same `structure1_t{n}_alloy` icon, and the render entity holds
        // no template id. The client Lua does know — every template is filed
        // into Tags[tag][tpId] as it loads, so Tags.ALLOYS_EXTRACTION is
        // exactly the set of extractor template ids, and Armies[focused].units
        // is exactly our own units. Ask once per poll and match on LocalID.
        //
        // Only completed extractors count. An upgrading extractor builds its
        // replacement as a second entity that exists from the moment the
        // upgrade starts, already carrying the higher tier's icon — so without
        // the IsCompleted() test a T1 mid-upgrade reads as a finished T2. The
        // T1 itself stays until the upgrade lands, and it is the one wearing
        // the upgrade adornment, so it is what fills the UPGRADING row.
        private static readonly HashSet<int> _extractorLocalIds = new HashSet<int>();
        private static bool _extractorIdsValid;
        private static bool _loggedArmyLookupFail;

        // ---- factory identity, from the same tag tables ----
        // The strategic icon cannot give a factory's type: the T3 naval
        // factories (ues3513, ucs3513, ugs3513) ship with the air symbol. Every
        // factory template carries exactly one of LAND_FACTORY, AIR_FACTORY and
        // NAVAL_FACTORY, so the tag tables answer it, and the template loader
        // fills in `general.techNumber` for the tier. Idle itself still comes
        // off the game's adornment, which ClientFactory lights by the same rule
        // as ClientEngineer: completed, no order, nothing queued.
        // Keyed by LocalID; the value is domain * 10 + tier.
        private static readonly Dictionary<int, int> _factoryKinds = new Dictionary<int, int>();
        private static bool _factoryKindsValid;

        // ---- row art, from the same sweep ----
        // The rows are grouped by tier off the strategic icon, but that icon is
        // the abstract map symbol, shared across factions and tiers, so it can
        // only ever be labelled. The build-menu sprite lives on the template,
        // which the ECS entity does not carry; Lua has both, so the sweep keeps
        // one representative template per row and hands back its
        // `foregroundIconID`, which ResolveSprite turns into the real Sprite.
        // Keys are "cmd", "eng{tier}", "alloy{tier}" and "fac{domain}{tier}",
        // matching how RecordIdle, RecordAlloy and RecordIdleFactory key rows.
        private static readonly Dictionary<string, uint> _rowIcons = new Dictionary<string, uint>();
        private static string _loggedRowIcons;

        /// The build-menu art for a row, or 0 when Lua hasn't answered yet.
        internal static uint RowIcon(string key) => _rowIcons.TryGetValue(key, out var id) ? id : 0u;

        /// One sweep over our own army's units per poll: completed extractors,
        /// factories by type and tier (only while a mod wants them), and a
        /// representative template per panel row for its art.
        private static void RefreshArmyLookups()
        {
            // A failed refresh shows no factory rows rather than last poll's.
            _factoryKindsValid = false;
            if (_getLuaGlobal == null || _luaStateReady == null || !_luaStateReady()) return;
            try
            {
                if (!RunLua(
                        "__SdbExtractors = '' " +
                        "__SdbFactories = '' " +
                        "__SdbRowIcons = '' " +
                        // Tags fills in as templates load, so early in a match
                        // any of these can still be missing.
                        "local T = Tags or {} " +
                        "local ext, cmd = T.ALLOYS_EXTRACTION or {}, T.COMMAND or {} " +
                        "local eng, stn = T.ENGINEER or {}, T.ENGINEERING_STATION or {} " +
                        "local fac = { T.LAND_FACTORY or {}, T.AIR_FACTORY or {}, T.NAVAL_FACTORY or {} } " +
                        "local tps = __Templates and __Templates.Units or {} " +
                        "local function techOf(tp) " +
                        "  local g = tps[tp] and tps[tp].general " +
                        "  return tonumber(g and g.techNumber) or 1 " +
                        "end " +
                        $"local wantFactories = {(_trackIdleFactories ? "true" : "false")} " +
                        "local out, facs, reps = {}, {}, {} " +
                        "for _, a in pairs(Armies or {}) do " +
                        "  if a.focused then " +
                        "    for _, u in pairs(a.units or {}) do " +
                        "      local li = u.localId and u.localId.index " +
                        "      local tp = u.tpId " +
                        "      if li and tp then " +
                        "        local d = (fac[1][tp] and 1) or (fac[2][tp] and 2) or (fac[3][tp] and 3) " +
                        "        if ext[tp] then " +
                        "          if u.IsCompleted and u:IsCompleted() then " +
                        "            out[#out+1] = li " +
                        "            reps['alloy' .. techOf(tp)] = tp " +
                        "          end " +
                        "        elseif cmd[tp] then " +
                        "          reps['cmd'] = tp " +
                        // Factories before engineers, so a factory that also
                        // carries an engineer tag can never lend its art to
                        // the engineer rows.
                        "        elseif d then " +
                        "          if wantFactories then " +
                        "            local kind = d * 10 + techOf(tp) " +
                        "            facs[#facs+1] = string.format('%d:%d', li, kind) " +
                        "            reps['fac' .. kind] = tp " +
                        "          end " +
                        // Engineering stations are structures; the idle rows
                        // list mobile engineers only, so the art must too.
                        "        elseif eng[tp] and not stn[tp] then " +
                        "          reps['eng' .. techOf(tp)] = tp " +
                        "        end " +
                        "      end " +
                        "    end " +
                        "  end " +
                        "end " +
                        "__SdbExtractors = table.concat(out, ',') " +
                        "__SdbFactories = table.concat(facs, ',') " +
                        // The art is a nicety and the ids are not, so an
                        // unexpected template shape must not take the ids down.
                        "pcall(function() " +
                        "  local icons = {} " +
                        "  for k, tp in pairs(reps) do " +
                        "    local g = tps[tp] and tps[tp].general " +
                        // foregroundIconID is the build-menu button art. The
                        // FFI hands back uint32 cdata, which would concatenate
                        // as '1234ULL', hence tonumber and string.format.
                        "    local id = g and g.foregroundIconID and tonumber(g.foregroundIconID.index) or 0 " +
                        "    if id ~= 0 then icons[#icons+1] = k .. '=' .. string.format('%d', id) end " +
                        "  end " +
                        // pairs() order changes between polls; sorted, the
                        // string only changes when the answer does.
                        "  table.sort(icons) " +
                        "  __SdbRowIcons = table.concat(icons, '|') " +
                        "end)"))
                    return;

                var raw = _getLuaGlobal("__SdbExtractors");
                if (raw == null) return;

                _extractorLocalIds.Clear();
                // An empty string is a valid answer: no extractors yet.
                foreach (var part in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part, out var id)) _extractorLocalIds.Add(id);
                }
                _extractorIdsValid = true;

                if (_trackIdleFactories)
                {
                    var factoriesRaw = _getLuaGlobal("__SdbFactories");
                    if (factoriesRaw != null)
                    {
                        _factoryKinds.Clear();
                        // Empty is valid here too: no factories yet.
                        foreach (var part in factoriesRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var colon = part.IndexOf(':');
                            if (colon > 0 && int.TryParse(part.Substring(0, colon), out var id) &&
                                int.TryParse(part.Substring(colon + 1), out var kind))
                            {
                                _factoryKinds[id] = kind;
                            }
                        }
                        _factoryKindsValid = true;
                    }
                }

                // Art is a nicety: without it the rows stay labelled.
                var iconsRaw = _getLuaGlobal("__SdbRowIcons");
                if (iconsRaw == null) return;
                // What Lua found. The per-id sprite line says what the registry
                // made of it, so a blank row can be pinned on one or the other.
                if (iconsRaw != _loggedRowIcons)
                {
                    _loggedRowIcons = iconsRaw;
                    _log.LogInfo($"Row art ids: {(iconsRaw.Length == 0 ? "(none)" : iconsRaw)}");
                }
                _rowIcons.Clear();
                foreach (var part in iconsRaw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var split = part.IndexOf('=');
                    if (split > 0 && uint.TryParse(part.Substring(split + 1), out var icon))
                        _rowIcons[part.Substring(0, split)] = icon;
                }
            }
            catch (Exception e)
            {
                if (!_loggedArmyLookupFail)
                {
                    _loggedArmyLookupFail = true;
                    _log.LogWarning($"Army lookup failed (the alloy panel and idle factories will stay hidden): {e.Message}");
                }
            }
        }

        /// Files an idle entity under its factory row, if it is one of our
        /// factories. The kind table only holds our own army's units, so this
        /// is the ownership check as well.
        private static void RecordIdleFactory(object em, object entity, Dictionary<int, IdleGroup> groups)
        {
            try
            {
                if (_localIdField == null || _getLocalIdMi == null) return;
                var component = _getLocalIdMi.Invoke(em, new[] { entity });
                var localId = _localIdField.GetValue(component);
                var indexField = localId.GetType().GetField("index", BindingFlags.Public | BindingFlags.Instance);
                if (indexField == null) return;
                var localIndex = Convert.ToInt32(indexField.GetValue(localId));
                if (!_factoryKinds.TryGetValue(localIndex, out var kind)) return;

                if (!groups.TryGetValue(kind, out var group))
                {
                    group = new IdleGroup
                    {
                        Domain = kind / 10,
                        Tier = kind % 10,
                        Label = $"T{kind % 10}",
                        IconId = RowIcon("fac" + kind),
                    };
                    groups[kind] = group;
                }
                group.Count++;
                group.UnitIds.Add(localIndex);
            }
            catch
            {
                // One we cannot attribute is left out rather than guessed at.
            }
        }

        /// True for an alloy structure by icon — extractors are
        /// `structure1_t{n}_alloy` across every faction, but so are alloy
        /// storages and the T3 furnace, so callers must also check
        /// `_extractorLocalIds`. This only supplies the tier.
        private static bool IsAlloyStructure(string iconName, out int tier)
        {
            tier = 0;
            if (string.IsNullOrEmpty(iconName)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(iconName, @"^structure\d*_t(\d)_alloy(_\w+)?$");
            if (!m.Success) return false;
            tier = int.Parse(m.Groups[1].Value);
            return true;
        }

        /// Files an alloy structure into its tier row (and the upgrading row
        /// when the game's upgrade adornment is lit). `name` is the already
        /// resolved strategic icon name, `tier` its parsed tech level.
        private static void RecordAlloy(object em, object entity, int tier, bool upgrading,
            Dictionary<int, IdleGroup> groups, Dictionary<int, IdleGroup> upgradingGroups)
        {
            try
            {
                var localIndex = -1;
                if (_localIdField != null && _getLocalIdMi != null)
                {
                    var component = _getLocalIdMi.Invoke(em, new[] { entity });
                    var localId = _localIdField.GetValue(component);
                    var indexField = localId.GetType().GetField("index", BindingFlags.Public | BindingFlags.Instance);
                    if (indexField != null) localIndex = Convert.ToInt32(indexField.GetValue(localId));
                }

                // Extractors only — the icon also matches storages and the
                // furnace, and Lua's tag set is what separates them. It is
                // already restricted to our own army, so this doubles as the
                // ownership check.
                if (localIndex < 0 || !_extractorLocalIds.Contains(localIndex)) return;

                Add(groups);
                // An upgrading extractor is still one of its current tier, so
                // it is counted in both rows rather than moved.
                if (upgrading) Add(upgradingGroups);

                void Add(Dictionary<int, IdleGroup> into)
                {
                    if (!into.TryGetValue(tier, out var group))
                    {
                        group = new IdleGroup { Tier = tier, Label = $"T{tier}", IconId = RowIcon("alloy" + tier) };
                        into[tier] = group;
                    }
                    group.Count++;
                    if (localIndex >= 0) group.UnitIds.Add(localIndex);
                }
            }
            catch
            {
                // A row without ids still counts; it just won't be clickable.
            }
        }

        private static bool ResolveEcs()
        {
            if (_ecsResolved) return true;
            if (_ecsResolveFailed)
            {
                // The first poll can land during the loading screen (economy
                // streams before the match world is fully up), so a failure
                // must not disable the mod for the session — retry on a delay.
                if (Time.realtimeSinceStartup < _nextResolveRetry) return false;
                _ecsResolveFailed = false;
            }

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
                _iconElemType = assemblies.SelectMany(GetTypesSafe).First(t => t.Name == "IconEntityElementComponent");
                _iconEnabledField = _iconElemType.GetField("Enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException("IconEntityElementComponent.Enabled");
                _iconIndexField = _iconElemType.GetField("IconIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // Resolve the registry index of the idle-adornment image so we
                // can match the idle icon exactly — entities like resource
                // deposits and wrecks carry icon buffers too, so slot position
                // alone overcounts badly. Registration happens during match
                // load, so this (and the other icon lookups below) may not
                // succeed yet — PollIdleBuilders keeps retrying them.
                _cliType = assemblies.SelectMany(GetTypesSafe).FirstOrDefault(t => t.FullName == "EM.Lua.Client.ClientLuaInterface");
                _iconLoaderType = assemblies.SelectMany(GetTypesSafe).FirstOrDefault(t => t.Name == "IconLoader");
                TryResolveIdleImageIndex();
                TryResolveUpgradeImageIndex();

                var entities = assemblies.First(a => a.GetName().Name == "Unity.Entities");
                var worldType = entities.GetType("Unity.Entities.World", true);
                // World.All's NoAllocReadOnlyCollection throws on IEnumerable
                // casts, so read the internal backing list instead.
                _allWorldsField = worldType.GetField("s_AllWorlds", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingFieldException("World.s_AllWorlds");
                _entityManagerProp = worldType.GetProperty("EntityManager");
                _entityType = entities.GetType("Unity.Entities.Entity", true);

                var componentTypeType = entities.GetType("Unity.Entities.ComponentType", true);
                _componentTypeReadOnly = componentTypeType.GetMethod("ReadOnly", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);

                var emType = entities.GetType("Unity.Entities.EntityManager", true);
                var ctArray = Array.CreateInstance(componentTypeType, 0).GetType();
                _createQueryMi = emType.GetMethod("CreateEntityQuery", new[] { ctArray });

                try
                {
                    ResolveOwnership(assemblies, emType);
                }
                catch (Exception e)
                {
                    _log.LogWarning($"Ownership filter unavailable (counts will include allies): {e.Message}");
                }

                ResolveIconNames();
                ResolveIconAtlas();

                try
                {
                    var localIdComponent = assemblies.SelectMany(GetTypesSafe).First(t => t.FullName == "EM.Components.LocalIDComponent");
                    _getLocalIdMi = emType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                        .MakeGenericMethod(localIdComponent);
                    _localIdField = localIdComponent.GetFields(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();

                    var pairedType = assemblies.SelectMany(GetTypesSafe).First(t => t.FullName == "EM.Components.LocalPairedGlobalIDComponent");
                    _getPairedGlobalMi = emType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                        .MakeGenericMethod(pairedType);
                    _pairedGlobalField = pairedType.GetField("Value");

                    var cli = assemblies.SelectMany(GetTypesSafe).First(t => t.FullName == "EM.Lua.Client.ClientLuaInterface");
                    _getHealthMi = cli.GetMethod("GetHealth", BindingFlags.Public | BindingFlags.Static);
                    _getMaxHealthMi = cli.GetMethod("GetMaxHealth", BindingFlags.Public | BindingFlags.Static);
                    _log.LogInfo($"Commander tracking: health {(_getHealthMi != null ? "ok" : "missing")}, paired-id {(_pairedGlobalField != null ? "ok" : "missing")}.");

                    ResolveLuaBridge(assemblies);
                }
                catch (Exception e)
                {
                    _log.LogWarning($"Selection support unavailable (rows won't be clickable): {e.Message}");
                }
                _getBufferMi = emType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                    .MakeGenericMethod(_iconElemType);

                var allocatorType = assemblies.Select(a => a.GetType("Unity.Collections.Allocator")).First(t => t != null);
                _allocatorTemp = Enum.ToObject(allocatorType, 2); // Allocator.Temp

                // ToEntityArray takes AllocatorManager.AllocatorHandle here;
                // apply the implicit Allocator->handle conversion ourselves,
                // since reflection Invoke won't.
                var handleType = assemblies.Select(a => a.GetType("Unity.Collections.AllocatorManager+AllocatorHandle")).FirstOrDefault(t => t != null);
                var opImplicit = handleType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType == allocatorType && m.ReturnType == handleType);
                if (opImplicit != null)
                {
                    _allocatorTemp = opImplicit.Invoke(null, new[] { _allocatorTemp });
                }
                else if (handleType != null)
                {
                    // Build the handle by hand: for built-in allocators the
                    // handle is just { Index = (ushort)allocator, Version = 0 }.
                    var handle = Activator.CreateInstance(handleType);
                    var indexField = handleType.GetField("Index", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    indexField?.SetValue(handle, (ushort)2);
                    _allocatorTemp = handle;
                }
                _log.LogInfo($"ECS idle poll: allocator arg is {_allocatorTemp.GetType().Name} (handleType {(handleType == null ? "missing" : "found")}, implicit {(opImplicit == null ? "missing" : "found")}).");

                _ecsResolved = true;
                _log.LogInfo("ECS idle poll: types resolved.");
                return true;
            }
            catch (Exception e)
            {
                _ecsResolveFailed = true;
                _nextResolveRetry = Time.realtimeSinceStartup + 5f;
                _pollStatus = "resolve failed";
                if (!_loggedResolveFail)
                {
                    _loggedResolveFail = true;
                    _log.LogError($"ECS idle poll: type resolution failed (will keep retrying): {e}");
                }
                return false;
            }
        }

        private static void PollIdleBuilders()
        {
            if (!ResolveEcs()) return;

            // Icon data is registered by the game during match load, often
            // after the economy stream (our in-match signal) has started — so
            // the one-shot resolve above can run too early for these. Without
            // them the idle rows and the commander stay invisible all session,
            // so retry until they exist.
            if (_idleImageIndex < 0) TryResolveIdleImageIndex();
            if (_upgradeImageIndex < 0) TryResolveUpgradeImageIndex();
            if (_iconNamesByIndex == null) ResolveIconNames();
            if (_iconAtlas == null || _iconUvRects == null) ResolveIconAtlas();
            // Give art that hadn't loaded a second ago another chance.
            _spriteMisses.Clear();

            try
            {
                var count = 0;
                var allCount = 0;
                var ownColour = LocalArmyColour();
                RefreshArmyLookups();
                var groups = new Dictionary<int, IdleGroup>();
                var alloyGroups = new Dictionary<int, IdleGroup>();
                var alloyUpgrading = new Dictionary<int, IdleGroup>();
                var factoryGroups = new Dictionary<int, IdleGroup>();

                // Without a trustworthy owner, show nothing rather than
                // everything: listing an enemy's idle engineers as if they were
                // yours is worse than an empty panel.
                if (ownColour == null)
                {
                    _idleCount = 0;
                    _idleFactoryCount = 0;
                    _alloyCount = 0;
                    _alloyUpgradingCount = 0;
                    _commanderLocalIndex = -1;
                    lock (_groupLock)
                    {
                        _idleGroups = new List<IdleGroup>();
                        _idleFactoryGroups = new List<IdleGroup>();
                        _alloyGroups = new List<IdleGroup>();
                        _alloyUpgradingGroups = new List<IdleGroup>();
                    }
                    _pollStatus = "no owner";
                    return;
                }
                var componentType = _componentTypeReadOnly.Invoke(null, new object[] { _iconElemType });
                var ctArray = Array.CreateInstance(componentType.GetType(), 1);
                ctArray.SetValue(componentType, 0);

                var worlds = ((System.Collections.IEnumerable)_allWorldsField.GetValue(null)).Cast<object>().ToList();
                foreach (var world in worlds)
                {
                    var em = _entityManagerProp.GetValue(world);
                    var query = _createQueryMi.Invoke(em, new object[] { ctArray });
                    try
                    {
                        var entitiesArray = query.GetType().GetMethod("ToEntityArray").Invoke(query, new[] { _allocatorTemp });
                        try
                        {
                            var lengthProp = entitiesArray.GetType().GetProperty("Length");
                            var itemProp = entitiesArray.GetType().GetProperty("Item");
                            var length = (int)lengthProp.GetValue(entitiesArray);
                            for (var i = 0; i < length; i++)
                            {
                                var entity = itemProp.GetValue(entitiesArray, new object[] { i });
                                var buffer = _getBufferMi.Invoke(em, new[] { entity, (object)true });
                                var bufferType = buffer.GetType();
                                var bufLength = (int)bufferType.GetProperty("Length").GetValue(buffer);
                                var itemGetter = bufferType.GetProperty("Item");

                                // The strategic icon (slot 0) identifies what
                                // the unit is; the rest of the buffer carries
                                // adornments. Read the name once, then scan
                                // the buffer once for every adornment we care
                                // about, rather than a pass per feature.
                                string iconName = null;
                                if (bufLength > 0 && _iconIndexField != null)
                                {
                                    var strategic = itemGetter.GetValue(buffer, new object[] { 0 });
                                    var strategicIndex = Convert.ToInt32(_iconIndexField.GetValue(strategic));
                                    iconName = IconName(strategicIndex);

                                    // The commander is tracked whether or not
                                    // it is idle, for the always-on health
                                    // readout.
                                    if (iconName != null && iconName.StartsWith(CommanderIconSuffix) &&
                                        ColourMatches(em, entity, ownColour.Value))
                                    {
                                        _commanderIconIndex = strategicIndex;
                                        RecordCommander(em, entity);
                                    }
                                }

                                if (_iconIndexField != null && (_idleImageIndex >= 0 || _upgradeImageIndex >= 0))
                                {
                                    var idle = false;
                                    var upgrading = false;
                                    for (var e = 0; e < bufLength; e++)
                                    {
                                        var element = itemGetter.GetValue(buffer, new object[] { e });
                                        var index = Convert.ToInt32(_iconIndexField.GetValue(element));
                                        if (index != _idleImageIndex && index != _upgradeImageIndex) continue;
                                        if (!(bool)_iconEnabledField.GetValue(element)) continue;
                                        if (index == _idleImageIndex) idle = true;
                                        else upgrading = true;
                                    }

                                    if (idle)
                                    {
                                        allCount++;
                                        if (ColourMatches(em, entity, ownColour.Value))
                                        {
                                            count++;
                                            RecordIdle(em, entity, buffer, itemGetter, bufLength, groups);
                                        }

                                        // RecordIdleFactory does its own
                                        // ownership check, like RecordAlloy.
                                        if (_factoryKindsValid)
                                        {
                                            RecordIdleFactory(em, entity, factoryGroups);
                                        }
                                    }

                                    // RecordAlloy does its own ownership check,
                                    // via the army-scoped extractor id set.
                                    if (_extractorIdsValid && IsAlloyStructure(iconName, out var alloyTier))
                                    {
                                        RecordAlloy(em, entity, alloyTier, upgrading, alloyGroups, alloyUpgrading);
                                    }
                                }
                                else if (bufLength > IdleIconIndex)
                                {
                                    var element = itemGetter.GetValue(buffer, new object[] { IdleIconIndex });
                                    if ((bool)_iconEnabledField.GetValue(element)) count++;
                                }
                            }
                        }
                        finally
                        {
                            entitiesArray.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(entitiesArray, null);
                        }
                    }
                    finally
                    {
                        query.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(query, null);
                    }
                }

                var ordered = groups.Values.OrderBy(g => g.Tier).ToList();
                // The headline number is idle *engineers*; `count` also covers
                // factories and the commander, which the rows deliberately skip.
                _idleCount = ordered.Sum(g => g.Count);
                _idleBuilderCount = count;
                _idleAllCount = allCount;

                var alloyOrdered = alloyGroups.Values.OrderBy(g => g.Tier).ToList();
                var alloyUpgradingOrdered = alloyUpgrading.Values.OrderBy(g => g.Tier).ToList();
                _alloyCount = alloyOrdered.Sum(g => g.Count);
                _alloyUpgradingCount = alloyUpgradingOrdered.Sum(g => g.Count);

                // The key is domain * 10 + tier, so it sorts land, air, naval.
                var factoryOrdered = factoryGroups.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                _idleFactoryCount = factoryOrdered.Sum(g => g.Count);

                lock (_groupLock)
                {
                    _idleGroups = ordered;
                    _idleFactoryGroups = factoryOrdered;
                    _alloyGroups = alloyOrdered;
                    _alloyUpgradingGroups = alloyUpgradingOrdered;
                }
                _pollStatus = "ok";
            }
            catch (Exception e)
            {
                if (_pollStatus != "poll error") _log.LogError($"ECS idle poll failed: {e}");
                _pollStatus = "poll error";
            }
        }

        // ---- shared per-frame upkeep --------------------------------------

        // Upkeep for everything fed by the ECS poll (idle groups, commander)
        // plus the deferred selection. Every consumer calls this from Update;
        // the frame stamp keeps it from running twice within one assembly.
        private static int _lastSharedTickFrame = -1;

        internal static void SharedTick()
        {
            if (Time.frameCount == _lastSharedTickFrame) return;
            _lastSharedTickFrame = Time.frameCount;

            if (!InMatch)
            {
                // Leaving a match: drop everything so the next one starts clean
                // rather than flashing the previous game's units.
                if (_commanderLocalIndex >= 0 || _idleCount > 0 || _idleFactoryCount > 0 || _alloyCount > 0 ||
                    _rowIcons.Count > 0)
                {
                    _commanderLocalIndex = -1;
                    _commanderIconIndex = -1;
                    _idleCount = 0;
                    _idleFactoryCount = 0;
                    _alloyCount = 0;
                    _alloyUpgradingCount = 0;
                    // Each match reloads its sprites through Engine.LoadSprite,
                    // so a reused id could otherwise draw last game's art.
                    ClearSpriteCache();
                    _rowIcons.Clear();
                    _loggedRowIcons = null;
                    lock (_groupLock)
                    {
                        _idleGroups = new List<IdleGroup>();
                        _idleFactoryGroups = new List<IdleGroup>();
                        _alloyGroups = new List<IdleGroup>();
                        _alloyUpgradingGroups = new List<IdleGroup>();
                    }
                    lock (_ecoLock) _eco = null;
                }
                return;
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum >= 1f)
            {
                _pollAccum = 0f;
                PollIdleBuilders();
            }

            // Deferred selection: wait for the mouse to come up, then let two
            // frames pass so the game's click handling completes first.
            if (_pendingSelection != null || _pendingCommander)
            {
                if (Input.GetMouseButton(0))
                {
                    _applyOnFrame = -1;
                }
                else
                {
                    if (_applyOnFrame < 0) _applyOnFrame = Time.frameCount + 2;
                    else if (Time.frameCount >= _applyOnFrame)
                    {
                        var ids = _pendingSelection;
                        var commander = _pendingCommander;
                        _pendingSelection = null;
                        _pendingCommander = false;
                        _applyOnFrame = -1;
                        if (commander) GoToCommander();
                        else SelectUnits(ids);
                    }
                }
            }
        }

        // ---- shared IMGUI styles ------------------------------------------

        private static bool _stylesReady;
        internal static GUIStyle _stWindow, _stName, _stSub, _stBarText, _stChevron, _stRowLabel, _stRowCount, _stIdleNone;
        internal static GUIStyle _stStripLabel, _stStripValue, _stStripMax, _stStripIn, _stStripOut, _stStripNet, _stStripChip;
        internal static GUIStyle _stCmdLabel, _stCmdGlyph, _stSubHeading;
        internal static Texture2D _texPanel, _texBarBack, _texWhite, _texRowHover;

        // Row geometry for the list panels, shared so they stack alike. The
        // row is sized around its build-menu art rather than its 12px label.
        internal const float RowHeight = 20f;
        internal const float IconSize = 18f;

        /// Where a row's three columns sit, and how wide that makes the panel.
        internal struct RowLayout
        {
            /// Label x, from the row's left edge. Leaves room for the art when
            /// any row has some, decided once for the whole panel: a row that
            /// slid over when its own sprite loaded would read as a glitch.
            public float Indent;
            /// Count x, likewise: one column, so the numbers stack.
            public float CountX;
            public float Width;
        }

        /// Measures a panel from the rows it is about to draw. A fixed width is
        /// always wrong one way or the other — dead space right of "T1 2", or
        /// one long label from clipping — so ask the styles how wide the text is.
        internal static RowLayout MeasureRows(List<IdleGroup> rows, bool withAllRow, int widestCount, bool roomForStatus)
        {
            var indent = 5f;
            foreach (var row in rows)
            {
                if (!HasSprite(row.IconId)) continue;
                indent = IconSize + 6f;
                break;
            }

            var label = 0f;
            foreach (var row in rows) label = Mathf.Max(label, _stRowLabel.CalcSize(new GUIContent(row.Label)).x);
            if (withAllRow) label = Mathf.Max(label, _stRowLabel.CalcSize(new GUIContent("ALL")).x);

            var countX = indent + label + 10f;
            var count = _stRowCount.CalcSize(new GUIContent(widestCount.ToString())).x;
            // 4px of window margin each side of the row, 8px past the count.
            var width = countX + count + 16f;
            // The poll-status note in the header is wider than any row, and only
            // shows when something is wrong, so only then does it widen things.
            if (roomForStatus) width = Mathf.Max(width, 152f);
            return new RowLayout { Indent = indent, CountX = countX, Width = width };
        }

        internal static readonly Color AlloyColour = new Color(0.16f, 0.75f, 0.72f, 0.92f);  // teal
        internal static readonly Color EnergyColour = new Color(0.9f, 0.68f, 0.16f, 0.92f);  // amber
        internal static readonly Color DangerColour = new Color(0.88f, 0.16f, 0.12f, 0.95f);
        internal static readonly Color GainColour = new Color(0.42f, 0.88f, 0.5f);
        internal static readonly Color LossColour = new Color(1f, 0.42f, 0.36f);
        /// In-progress work (upgrading structures) — distinct from both the
        /// alloy teal and the idle orange so the two panels never read alike.
        internal static readonly Color UpgradeColour = new Color(0.55f, 0.78f, 1f);

        internal static void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _texPanel = MakeTex(new Color(0.04f, 0.06f, 0.08f, 0.82f));
            _texBarBack = MakeTex(new Color(1f, 1f, 1f, 0.08f));
            _texWhite = MakeTex(Color.white);
            _texRowHover = MakeTex(new Color(1f, 1f, 1f, 0.12f));

            _stWindow = new GUIStyle { normal = { background = _texPanel } };
            _stRowLabel = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.72f, 0.79f, 0.9f) } };
            _stRowCount = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(1f, 0.68f, 0.25f) } };
            _stIdleNone = new GUIStyle { fontSize = 12, normal = { textColor = new Color(1, 1, 1, 0.35f) } };
            _stName = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _stSub = new GUIStyle { fontSize = 12, normal = { textColor = new Color(1, 1, 1, 0.6f) }, alignment = TextAnchor.UpperRight };
            _stBarText = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _stChevron = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };

            _stStripLabel = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _stStripValue = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _stStripMax = new GUIStyle { fontSize = 13, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(1, 1, 1, 0.45f) } };
            _stStripIn = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = GainColour } };
            _stStripOut = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(1f, 0.55f, 0.5f, 0.95f) } };
            _stStripNet = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            _stStripChip = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _stSubHeading = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = UpgradeColour } };
            _stCmdLabel = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _stCmdGlyph = new GUIStyle { fontSize = 17, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        internal static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        // ---- strategic icons, by name ---------------------------------------
        //
        // The idle poll reaches the icon registry the long way round, as a
        // side effect of resolving the whole ECS sweep, and reads it index to
        // name. A mod that wants to *draw* icons for units it identified
        // elsewhere needs the opposite direction and none of the sweep — so
        // this resolves the same two tables on their own.

        private static Dictionary<string, int> _iconIndexByName;
        private static float _nextIconRegistryRetry;

        /// Resolves the strategic icon registry and atlas without running the
        /// ECS poll. Icons are registered by Lua during match load, so this
        /// legitimately finds nothing on the first calls of a match and is
        /// retried, at most once a second, until it sticks.
        internal static void EnsureIconRegistry()
        {
            if (_iconIndexByName != null && _iconAtlas != null && _iconUvRects != null) return;
            if (Time.realtimeSinceStartup < _nextIconRegistryRetry) return;
            _nextIconRegistryRetry = Time.realtimeSinceStartup + 1f;

            try
            {
                if (_iconLoaderType == null)
                {
                    _iconLoaderType = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                        .SelectMany(GetTypesSafe).FirstOrDefault(t => t.Name == "IconLoader");
                }
                if (_iconNamesByIndex == null) ResolveIconNames();
                if (_iconAtlas == null || _iconUvRects == null) ResolveIconAtlas();

                if (_iconNamesByIndex != null && _iconIndexByName == null)
                {
                    var byName = new Dictionary<string, int>(_iconNamesByIndex.Count, StringComparer.OrdinalIgnoreCase);
                    // Names are the registry's own keys, so this inversion is
                    // exact; a duplicate would only mean two indices drawing
                    // the same art.
                    foreach (var pair in _iconNamesByIndex) byName[pair.Value] = pair.Key;
                    _iconIndexByName = byName;
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Strategic icon registry unavailable ({e.Message}); callers fall back to plain marks.");
            }
        }

        /// The atlas index for a strategic icon's image name, or -1.
        ///
        /// Both spellings are in circulation: a unit template records the name
        /// with its "_normal" suffix, while StrategicIcon strips that suffix on
        /// the way in, so a caller may hold either. Try what it was given, then
        /// the other spelling.
        internal static int IconIndexByName(string name)
        {
            if (_iconIndexByName == null || string.IsNullOrEmpty(name)) return -1;
            if (_iconIndexByName.TryGetValue(name, out var index)) return index;
            var swapped = name.EndsWith("_normal", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - "_normal".Length)
                : name + "_normal";
            return _iconIndexByName.TryGetValue(swapped, out index) ? index : -1;
        }

        /// Draws one from the atlas. False when the atlas or the index isn't
        /// there, so the caller can fall back to something plainer.
        internal static bool DrawStrategicIcon(Rect rect, int index)
        {
            if (_iconAtlas == null || _iconUvRects == null || index < 0 || index >= _iconUvRects.Count) return false;
            GUI.DrawTextureWithTexCoords(rect, _iconAtlas, _iconUvRects[index]);
            return true;
        }

        /// Dropped when a match ends: icons are registered per match, so last
        /// game's indices are not safe to carry into the next one.
        internal static void ClearIconRegistry()
        {
            _iconIndexByName = null;
            _iconNamesByIndex = null;
            _iconAtlas = null;
            _iconUvRects = null;
            _nextIconRegistryRetry = 0f;
        }

        // ---- the game's menus over a match ------------------------------------

        /// Whether one of the game's menus is up over the match: the pause
        /// menu, or a front-end screen such as settings or the Mods page. The
        /// game's own HUD and map labels sit beneath those; IMGUI draws over
        /// everything, so a mod's panels have to step aside instead.
        internal static bool MenuOpen()
        {
            try
            {
                var ui = SanctuaryUI.SanctuaryUIManager.Instance;
                if (ui != null && ui.TryGetPanel(SanctuaryUI.UIPanelType.PauseMenu, out var pause) && pause.IsVisible) return true;
                // InterfaceManager.TransitionTo turns this backdrop on for
                // every screen except None, and None is what a match runs
                // under.
                var screens = EM.UI.InterfaceManager.Instance;
                return screens != null && screens.background != null && screens.background.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        // ---- keeping IMGUI clicks off the battlefield -------------------------
        //
        // IMGUI sits outside Unity's event system, so on its own a click on an
        // IMGUI widget also reaches the map beneath it — clearing the
        // selection, or starting a box drag. The game's input ignores anything
        // over uGUI (Engine.IsMouseOverUI is EventSystem.IsPointerOverGameObject),
        // so an invisible raycast target under the widget is enough.
        //
        // Several rects per assembly, because one mod can have more than one
        // thing to protect at once — the HUD's economy strip and its mini-map
        // are both up together. Each Shield call in a frame claims the next
        // target from a pool; anything not claimed comes down again.

        private static GameObject _shield;
        private static readonly List<RectTransform> _shieldTargets = new List<RectTransform>();
        private static int _shieldUsed;
        private static int _shieldFrame = -1;
        private static bool _shieldFailed;

        /// Stands a shield over rect (in the caller's own GUI coordinates) for
        /// this frame. Call from OnGUI, once per area, as many times as
        /// needed; TickShield takes down whatever stopped being asked for.
        internal static void Shield(Rect rect, float scale)
        {
            if (_shieldFailed || Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                if (_shield == null) CreateShield();

                // First call of a frame starts the claims over.
                if (_shieldFrame != Time.frameCount)
                {
                    _shieldUsed = 0;
                    _shieldFrame = Time.frameCount;
                }

                while (_shieldTargets.Count <= _shieldUsed) _shieldTargets.Add(CreateShieldTarget());
                var target = _shieldTargets[_shieldUsed++];
                target.anchoredPosition = new Vector2(rect.x * scale, -rect.y * scale);
                target.sizeDelta = new Vector2(rect.width * scale, rect.height * scale);
                if (!target.gameObject.activeSelf) target.gameObject.SetActive(true);
                if (!_shield.activeSelf) _shield.SetActive(true);
            }
            catch
            {
                // Without it the widget still works; the click just also
                // reaches the map.
                _shieldFailed = true;
            }
        }

        /// From Update: down once the caller has stopped drawing. Runs before
        /// the frame's OnGUI, so the count claimed on the previous frame is
        /// what is still wanted.
        internal static void TickShield()
        {
            if (_shield == null) return;
            var stale = Time.frameCount - _shieldFrame > 1;
            var keep = stale ? 0 : _shieldUsed;
            for (var i = keep; i < _shieldTargets.Count; i++)
                if (_shieldTargets[i] != null && _shieldTargets[i].gameObject.activeSelf)
                    _shieldTargets[i].gameObject.SetActive(false);
            if (stale && _shield.activeSelf) _shield.SetActive(false);
        }

        /// From OnDestroy: a hot reload leaves the old assembly in memory for
        /// the rest of the session, so a shield left standing would keep
        /// swallowing clicks with nothing drawing under it.
        internal static void DestroyShield()
        {
            if (_shield != null) UnityEngine.Object.Destroy(_shield);
            _shield = null;
            _shieldTargets.Clear();
            _shieldUsed = 0;
            _shieldFrame = -1;
        }

        private static void CreateShield()
        {
            _shield = new GameObject("SanctuaryHud click shield", typeof(RectTransform));
            _shield.hideFlags = HideFlags.HideAndDontSave;
            var canvas = _shield.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            _shield.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            _shieldTargets.Clear();
            _shieldUsed = 0;
        }

        private static RectTransform CreateShieldTarget()
        {
            var target = new GameObject("Target", typeof(RectTransform));
            target.hideFlags = HideFlags.HideAndDontSave;
            target.transform.SetParent(_shield.transform, false);
            var rect = (RectTransform)target.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            var image = target.AddComponent<UnityEngine.UI.Image>();
            image.color = Color.clear;
            // The raycaster skips a graphic with no draw depth; don't let the
            // canvas cull this one for being fully transparent.
            image.canvasRenderer.cullTransparentMesh = false;
            return rect;
        }
    }
}
