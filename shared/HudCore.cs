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
            /// Units in this row. Counted even when the id lookup fails (in
            /// which case the row shows but can't select).
            public int Count;
            public readonly List<int> UnitIds = new List<int>();
        }

        internal static readonly object _groupLock = new object();
        internal static List<IdleGroup> _idleGroups = new List<IdleGroup>();

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

        internal static void EconomyValuesPostfix(object[] __args)
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
            lock (_ecoLock) _eco = snapshot;
            // The host streams economy continuously during a match and never
            // outside one, so this doubles as the "am I in a game?" signal.
            _lastEcoRealtime = Time.realtimeSinceStartup;
        }

        private static float _lastEcoRealtime = -999f;

        /// True while economy updates are still arriving. The grace period
        /// covers pauses and loading hitches without leaving the HUD stranded
        /// on the menu after a match ends.
        internal static bool InMatch => Time.realtimeSinceStartup - _lastEcoRealtime < 5f;

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

        private static Vector4[] ParseArmyColours(string path)
        {
            try
            {
                var text = System.IO.File.ReadAllText(path);
                var colours = new Dictionary<string, Vector4>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             text, "\\[\"(\\w+)\"\\]\\s*=\\s*EngineClasses\\.float4\\(([^)]+)\\)"))
                {
                    var parts = m.Groups[2].Value.Split(',').Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
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
                RunLua(
                    "__SdbOwn = '' " +
                    "for id, a in pairs(Armies or {}) do " +
                    "  if a.focused and a.color then " +
                    "    __SdbOwn = string.format('%f,%f,%f', a.color.x, a.color.y, a.color.z) " +
                    "  end " +
                    "end");

                var raw = _getLuaGlobal("__SdbOwn");
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

        private static Vector4? LocalArmyColour()
        {
            // Prefer the game's own answer; the lobby-derived guess is only a
            // fallback for when the Lua bridge isn't available.
            var fromLua = FocusedArmyColourFromLua();
            if (fromLua != null) return fromLua;

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

        private static bool RunLua(string chunk)
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
                var globalId = _pairedGlobalField.GetValue(pairedComponent);

                var args = new[] { globalId, null };
                _getHealthMi.Invoke(null, args);
                _commanderHealth = Convert.ToSingle(args[1] ?? 0f);

                if (_getMaxHealthMi != null)
                {
                    var maxArgs = new[] { globalId, null };
                    _getMaxHealthMi.Invoke(null, maxArgs);
                    _commanderMaxHealth = Convert.ToSingle(maxArgs[1] ?? 0f);
                }
            }
            catch
            {
                // Keep the last known values rather than flickering to zero.
            }
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
                    group = new IdleGroup { Tier = key, Label = isCommander ? "COMMANDER" : $"T{tier}" };
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
        private static bool _loggedExtractorQueryFail;

        private static void RefreshExtractorIds()
        {
            if (_getLuaGlobal == null || _luaStateReady == null || !_luaStateReady()) return;
            try
            {
                if (!RunLua(
                        "__SdbExtractors = '' " +
                        "local out = {} " +
                        "for _, a in pairs(Armies or {}) do " +
                        "  if a.focused then " +
                        "    for _, u in pairs(a.units or {}) do " +
                        "      local li = u.localId and u.localId.index " +
                        "      if li and u.tpId and Tags and Tags.ALLOYS_EXTRACTION and Tags.ALLOYS_EXTRACTION[u.tpId] " +
                        "         and u.IsCompleted and u:IsCompleted() then " +
                        "        out[#out+1] = li " +
                        "      end " +
                        "    end " +
                        "  end " +
                        "end " +
                        "__SdbExtractors = table.concat(out, ',')"))
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
            }
            catch (Exception e)
            {
                if (!_loggedExtractorQueryFail)
                {
                    _loggedExtractorQueryFail = true;
                    _log.LogWarning($"Extractor lookup failed (the alloy panel will stay hidden): {e.Message}");
                }
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
                        group = new IdleGroup { Tier = tier, Label = $"T{tier}" };
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

            try
            {
                var count = 0;
                var allCount = 0;
                var ownColour = LocalArmyColour();
                RefreshExtractorIds();
                var groups = new Dictionary<int, IdleGroup>();
                var alloyGroups = new Dictionary<int, IdleGroup>();
                var alloyUpgrading = new Dictionary<int, IdleGroup>();

                // Without a trustworthy owner, show nothing rather than
                // everything: listing an enemy's idle engineers as if they were
                // yours is worse than an empty panel.
                if (ownColour == null)
                {
                    _idleCount = 0;
                    _alloyCount = 0;
                    _alloyUpgradingCount = 0;
                    _commanderLocalIndex = -1;
                    lock (_groupLock)
                    {
                        _idleGroups = new List<IdleGroup>();
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

                lock (_groupLock)
                {
                    _idleGroups = ordered;
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
                if (_commanderLocalIndex >= 0 || _idleCount > 0 || _alloyCount > 0)
                {
                    _commanderLocalIndex = -1;
                    _commanderIconIndex = -1;
                    _idleCount = 0;
                    _alloyCount = 0;
                    _alloyUpgradingCount = 0;
                    lock (_groupLock)
                    {
                        _idleGroups = new List<IdleGroup>();
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
    }
}
