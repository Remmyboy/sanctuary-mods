using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SanctuaryHud
{
    // Client-side HUD mod. Presentation-only: reads state the game already
    // sends to the render side and draws an IMGUI overlay. Never touches the
    // lobby-hashed Lua tree or the simulation.
    //
    // Data sources:
    //  - Economy: Harmony postfix on SanctuaryUI.EconomyPanelUI (the managed
    //    receiver downstream of Lua's Engine.UI_SetEconomyValues).
    //  - Idle builders: the icon FFI receivers are Burst-compiled (Harmony
    //    can't intercept them), so we poll the DOTS world instead: every unit
    //    entity carries a DynamicBuffer<IconEntityElementComponent>, and on
    //    construction-capable units element 2 is the Idle adornment.
    // The local-LAN lobby unlock lives in LocalLanLobby.cs.
    [BepInPlugin("com.sanctuarydb.hud", "SanctuaryDB HUD", "0.6.0")]
    public partial class SanctuaryHudPlugin : BaseUnityPlugin
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private Harmony _harmony;

        // ---- economy snapshot, written by the Harmony postfix ----
        private static readonly object _ecoLock = new object();
        private static Dictionary<string, float> _eco;
        private static FieldInfo[] _ecoFields;

        // ---- idle-builder polling ----
        private const int IdleIconIndex = 2;
        private static int _idleCount;
        private static string _pollStatus = "starting";
        private float _pollAccum;

        /// One row per tech tier, plus the unit ids behind it for selection.
        private class IdleGroup
        {
            public string Label;
            public int Tier;
            /// Units in this row. Counted even when the id lookup fails (in
            /// which case the row shows but can't select).
            public int Count;
            public readonly List<int> UnitIds = new List<int>();
        }

        private static readonly object _groupLock = new object();
        private static List<IdleGroup> _idleGroups = new List<IdleGroup>();

        // ---- commander ----
        // "bot2_t1_direct" is the commander icon for all three factions and is
        // used by nothing else, so icon + own army colour pins ours exactly.
        private const string CommanderIconSuffix = "bot2_t1_direct";
        private static int _commanderLocalIndex = -1;
        private static int _commanderIconIndex = -1;
        private static float _commanderHealth;
        private static float _commanderMaxHealth;

        // Strategic icons are packed into one atlas at load time (the source
        // .dds files are unloaded), so drawing the real icon means sampling
        // the atlas with the icon's own rect.
        private static Texture _iconAtlas;
        private static List<Rect> _iconUvRects;

        private static void ResolveIconAtlas(List<Assembly> assemblies)
        {
            try
            {
                var loader = assemblies.SelectMany(GetTypesSafe).First(t => t.Name == "IconLoader");
                _iconAtlas = Shader.GetGlobalTexture(Shader.PropertyToID("_StrategicIconAtlas"));
                if (_iconAtlas == null)
                {
                    _log.LogWarning("Strategic icon atlas not bound yet; commander icon falls back to a glyph.");
                    return;
                }

                var rectsMember = loader.GetField("iconRects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
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
                _log.LogWarning($"Icon atlas unavailable ({e.Message}); commander icon falls back to a glyph.");
            }
        }

        // The game's own selection system also sees our click (IMGUI doesn't
        // block it) and clears the selection when it lands on open ground. So
        // queue the selection and apply it a couple of frames after the mouse
        // is released, once the game has finished processing the click.
        private static List<int> _pendingSelection;
        private static bool _pendingCommander;
        private static int _applyOnFrame = -1;

        // ---- config ----
        private ConfigEntry<bool> _cfgVisible;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;
        private ConfigEntry<KeyCode> _cfgToggleKey;
        private static ConfigEntry<float> _cfgCommanderZoom;
        private ConfigEntry<bool> _cfgUnlockLanLobby;

        // Geometry in 1080p-logical pixels (GUI.matrix rescales per resolution).
        private Rect _idleRect = new Rect(12, 250, 132, 44);
        private bool _visible = true;

        // Smoothed net rates so colours don't flicker with per-tick noise.
        private static float _netSmoothAlloy;
        private static float _netSmoothEnergy;

        private void Awake()
        {
            _log = Logger;

            _cfgVisible = Config.Bind("Overlay", "Visible", true, "Show the overlay.");
            _cfgPosX = Config.Bind("Overlay", "PosX", 12f, "Idle panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("Overlay", "PosY", 250f, "Idle panel Y in 1080p-logical pixels.");
            _cfgToggleKey = Config.Bind("Overlay", "ToggleKey", KeyCode.F10, "Key that shows/hides the overlay.");
            _cfgCommanderZoom = Config.Bind("Commander", "JumpZoomFactor", 0.5f,
                "How wide the camera sits after jumping to the commander, as a fraction of the current camera height. " +
                "Higher = further out. 0.5 keeps roughly your current zoom.");
            _cfgUnlockLanLobby = Config.Bind("LocalTesting", "UnlockLanLobby", true,
                "Let the main menu open when the entitlement API is unreachable, so Multiplayer LAN can host a " +
                "local game against AI. Affects this client's menu only - it grants no server access. Set false " +
                "if you share this build.");

            _visible = _cfgVisible.Value;
            _idleRect.x = _cfgPosX.Value;
            _idleRect.y = _cfgPosY.Value;

            _log.LogInfo($"SanctuaryDB HUD loaded (assembly {typeof(SanctuaryHudPlugin).Assembly.GetName().Version}). Unity {Application.unityVersion}.");
            try
            {
                PatchEconomyReceiver();
            }
            catch (Exception e)
            {
                _log.LogError($"Economy patch failed (strip will stay empty): {e}");
            }
            try
            {
                PatchMapLocalFiles();
            }
            catch (Exception e)
            {
                _log.LogError($"Map-local file fallback failed (map-carried decals will not load): {e}");
            }
            try
            {
                if (_cfgUnlockLanLobby.Value) PatchPermissionGate();
            }
            catch (Exception e)
            {
                _log.LogError($"LAN lobby unlock failed (menu will still gate on the API): {e}");
            }
            _log.LogInfo($"Hotkeys: {_cfgToggleKey.Value} = toggle overlay, F9 = dump UI hierarchy to log.");
        }

        // ScriptEngine-style hot reload destroys and recreates the plugin;
        // drop our patches so the reloaded copy doesn't stack a second postfix.
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // One Harmony instance for every patch we apply. The id carries a fresh
        // GUID per load so a hot reload can't collide with the previous copy's
        // registration before OnDestroy has unpatched it.
        private void EnsureHarmony()
        {
            _harmony ??= new Harmony("com.sanctuarydb.hud." + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // ---- economy capture ----------------------------------------------

        private void PatchEconomyReceiver()
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(GetTypesSafe)
                .Where(t => t.Name == "EconomyPanelUI");

            EnsureHarmony();
            var postfix = new HarmonyMethod(typeof(SanctuaryHudPlugin), nameof(EconomyValuesPostfix));
            var patched = 0;

            foreach (var type in types)
            {
                foreach (var method in type
                             .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                             .Where(m => !m.IsAbstract && !m.ContainsGenericParameters)
                             .Where(m => m.GetParameters().Any(p => p.ParameterType.Name.Contains("UIEconomyValues"))))
                {
                    _harmony.Patch(method, postfix: postfix);
                    patched++;
                }
            }
            _log.LogInfo($"Economy hook: patched {patched} method(s).");
        }

        private static void EconomyValuesPostfix(object[] __args)
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
        private static bool InMatch => Time.realtimeSinceStartup - _lastEcoRealtime < 5f;

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
        private static object _allocatorTemp;
        private static bool _ecsResolved;
        private static bool _ecsResolveFailed;
        private static bool _dumpedComponents;
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
        private static Color? _ownArmyColourUi;

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

        // IconLoader.iconLookup maps name -> registry index; invert it once so
        // we can read a unit's tier off its strategic icon.
        private static void ResolveIconNames(List<Assembly> assemblies)
        {
            try
            {
                var loader = assemblies.SelectMany(GetTypesSafe).First(t => t.Name == "IconLoader");
                var lookupMember = (object)loader.GetField("iconLookup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? loader.GetProperty("iconLookup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (lookupMember == null) return;

                var map = new Dictionary<int, string>();
                foreach (var entry in (System.Collections.IEnumerable)lookupMember)
                {
                    var t = entry.GetType();
                    var k = t.GetProperty("Key")?.GetValue(entry)?.ToString();
                    var v = t.GetProperty("Value")?.GetValue(entry);
                    if (k != null && v != null) map[Convert.ToInt32(v)] = k;
                }
                _iconNamesByIndex = map;
                _log.LogInfo($"Icon names: {map.Count} entries (idle = {IconName(_idleImageIndex)}).");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Icon name table unavailable (no tech split): {e.Message}");
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
                "SanctuaryHud_RunLua", typeof(int), new[] { typeof(string) }, typeof(SanctuaryHudPlugin), skipVisibility: true);
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
                    "SanctuaryHud_LuaStateReady", typeof(bool), Type.EmptyTypes, typeof(SanctuaryHudPlugin), skipVisibility: true);
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
                    "SanctuaryHud_GetLuaGlobal", typeof(string), new[] { typeof(string) }, typeof(SanctuaryHudPlugin), skipVisibility: true);
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
        private static void SelectUnits(List<int> localIdIndices)
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
        private static void GoToCommander()
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
            var radius = Mathf.Clamp(camHeight * _cfgCommanderZoom.Value, 40f, 4000f);

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

        // One-shot diagnostic: list every component on an idle-marked entity,
        // and dump the fields of anything that smells like ownership — the
        // path to filtering the count down to the local player's units.
        private static void DumpEntityComponents(object em, object entity)
        {
            try
            {
                var getTypes = em.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "GetComponentTypes" && m.GetParameters().Length <= 2);
                var args = getTypes.GetParameters().Length == 2
                    ? new[] { entity, _allocatorTemp is Enum ? _allocatorTemp : Enum.ToObject(getTypes.GetParameters()[1].ParameterType, 2) }
                    : new[] { entity };
                var typesArray = getTypes.Invoke(em, args);
                var lengthProp = typesArray.GetType().GetProperty("Length");
                var itemProp = typesArray.GetType().GetProperty("Item");
                var n = (int)lengthProp.GetValue(typesArray);
                var names = new List<string>();
                for (var i = 0; i < n; i++)
                {
                    var componentType = itemProp.GetValue(typesArray, new object[] { i });
                    var managed = componentType.GetType().GetMethod("GetManagedType")?.Invoke(componentType, null) as Type;
                    names.Add(managed?.FullName ?? componentType.ToString());
                }
                typesArray.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(typesArray, null);
                _log.LogInfo($"Idle entity components ({n}): {string.Join(" | ", names)}");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Component dump failed: {e.Message}");
            }
        }

        private static bool ResolveEcs()
        {
            if (_ecsResolved) return true;
            if (_ecsResolveFailed) return false;

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
                // alone overcounts badly.
                try
                {
                    var cli = assemblies.SelectMany(GetTypesSafe).First(t => t.FullName == "EM.Lua.Client.ClientLuaInterface");
                    var checkValidIcon = cli.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .First(m => m.Name == "CheckValidIcon");
                    var ps = checkValidIcon.GetParameters();
                    var args = new object[ps.Length];
                    var stringSeen = 0;
                    var outPos = -1;
                    for (var i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].IsOut) { outPos = i; continue; }
                        if (ps[i].ParameterType == typeof(string))
                        {
                            args[i] = stringSeen++ == 0 ? "SanctuaryHud" : "strategic_icon_adornment_idle";
                        }
                    }
                    // (functionName, iconName, out index) — if there is only
                    // one string param it is the icon name.
                    if (stringSeen == 1) args[Array.FindIndex(ps, p => p.ParameterType == typeof(string))] = "strategic_icon_adornment_idle";
                    var ok = checkValidIcon.Invoke(null, args);
                    if (ok is bool b && b && outPos >= 0)
                    {
                        _idleImageIndex = Convert.ToInt32(args[outPos]);
                        _log.LogInfo($"ECS idle poll: idle image registry index = {_idleImageIndex}.");
                    }
                    else
                    {
                        _log.LogWarning("ECS idle poll: CheckValidIcon rejected the idle image name; falling back to slot-2 heuristic.");
                    }
                }
                catch (Exception e)
                {
                    _log.LogWarning($"ECS idle poll: could not resolve idle image index ({e.Message}); falling back to slot-2 heuristic.");
                }

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

                ResolveIconNames(assemblies);
                ResolveIconAtlas(assemblies);

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
                _pollStatus = "resolve failed";
                _log.LogError($"ECS idle poll: type resolution failed: {e}");
                return false;
            }
        }

        private void PollIdleBuilders()
        {
            if (!ResolveEcs()) return;

            try
            {
                var count = 0;
                var allCount = 0;
                var ownColour = LocalArmyColour();
                var groups = new Dictionary<int, IdleGroup>();

                // Without a trustworthy owner, show nothing rather than
                // everything: listing an enemy's idle engineers as if they were
                // yours is worse than an empty panel.
                if (ownColour == null)
                {
                    _idleCount = 0;
                    _commanderLocalIndex = -1;
                    lock (_groupLock) _idleGroups = new List<IdleGroup>();
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

                                // The commander is tracked whether or not it is
                                // idle, for the always-on health readout.
                                if (bufLength > 0 && _iconIndexField != null)
                                {
                                    var strategic = itemGetter.GetValue(buffer, new object[] { 0 });
                                    var iconName = IconName(Convert.ToInt32(_iconIndexField.GetValue(strategic)));
                                    if (iconName != null && iconName.StartsWith(CommanderIconSuffix) &&
                                        ColourMatches(em, entity, ownColour.Value))
                                    {
                                        _commanderIconIndex = Convert.ToInt32(_iconIndexField.GetValue(strategic));
                                        RecordCommander(em, entity);
                                    }
                                }

                                if (_idleImageIndex >= 0 && _iconIndexField != null)
                                {
                                    // Exact match on the idle-adornment image.
                                    for (var e = 0; e < bufLength; e++)
                                    {
                                        var element = itemGetter.GetValue(buffer, new object[] { e });
                                        if (Convert.ToInt32(_iconIndexField.GetValue(element)) == _idleImageIndex &&
                                            (bool)_iconEnabledField.GetValue(element))
                                        {
                                            allCount++;
                                            if (ColourMatches(em, entity, ownColour.Value))
                                            {
                                                count++;
                                                RecordIdle(em, entity, buffer, itemGetter, bufLength, groups);
                                            }
                                            break;
                                        }
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
                lock (_groupLock) _idleGroups = ordered;
                _pollStatus = "ok";
            }
            catch (Exception e)
            {
                if (_pollStatus != "poll error") _log.LogError($"ECS idle poll failed: {e}");
                _pollStatus = "poll error";
            }
        }

        // ---- input --------------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                _visible = !_visible;
                _cfgVisible.Value = _visible;
            }
            if (Input.GetKeyDown(KeyCode.F9)) DumpHierarchy();

            if (!InMatch)
            {
                // Leaving a match: drop everything so the next one starts clean
                // rather than flashing the previous game's units.
                if (_commanderLocalIndex >= 0 || _idleCount > 0)
                {
                    _commanderLocalIndex = -1;
                    _commanderIconIndex = -1;
                    _idleCount = 0;
                    lock (_groupLock) _idleGroups = new List<IdleGroup>();
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

            // Persist the idle panel position once the drag is over.
            if (!Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _idleRect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _idleRect.y) > 0.5f))
            {
                _cfgPosX.Value = _idleRect.x;
                _cfgPosY.Value = _idleRect.y;
            }
        }

        // ---- drawing ------------------------------------------------------

        private static bool _stylesReady;
        private static GUIStyle _stWindow, _stName, _stSub, _stBarText, _stChevron, _stRowLabel, _stRowCount, _stIdleNone;
        private static GUIStyle _stStripLabel, _stStripValue, _stStripMax, _stStripIn, _stStripOut, _stStripNet, _stStripChip;
        private static GUIStyle _stCmdLabel, _stCmdGlyph;
        private static Texture2D _texPanel, _texBarBack, _texWhite, _texRowHover;

        private static readonly Color AlloyColour = new Color(0.16f, 0.75f, 0.72f, 0.92f);  // teal
        private static readonly Color EnergyColour = new Color(0.9f, 0.68f, 0.16f, 0.92f);  // amber
        private static readonly Color DangerColour = new Color(0.88f, 0.16f, 0.12f, 0.95f);
        private static readonly Color GainColour = new Color(0.42f, 0.88f, 0.5f);
        private static readonly Color LossColour = new Color(1f, 0.42f, 0.36f);

        private void OnGUI()
        {
            if (!_visible || !InMatch) return;
            EnsureStyles();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;

            DrawEconomyStrip(logicalWidth);
            DrawCommanderWidget(logicalWidth);

            // The idle panel only exists when there is something to act on.
            if (_idleCount > 0)
            {
                _idleRect.x = Mathf.Clamp(_idleRect.x, -_idleRect.width + 40, logicalWidth - 40);
                _idleRect.y = Mathf.Clamp(_idleRect.y, 0, Screen.height / scale - 30);
                _idleRect = GUI.Window(0x5DC, _idleRect, DrawIdleWindow, GUIContent.none, _stWindow);
            }

            GUI.matrix = previousMatrix;
        }

        private const float StripHeight = 48f;

        /// Smoothed [income, spend, net] per resource, so the readout doesn't
        /// jitter with per-tick noise.
        private static readonly Dictionary<string, float[]> _smooth = new Dictionary<string, float[]>();

        /// Compact number formatting — these values run to millions.
        private static string Fmt(float v)
        {
            var a = Mathf.Abs(v);
            if (a >= 1_000_000f) return (v / 1_000_000f).ToString("0.##") + "M";
            if (a >= 10_000f) return (v / 1_000f).ToString("0.#") + "K";
            return v.ToString("#,0");
        }

        // Full-width strip: alloy on the left, energy on the right. Each half
        // shows storage on top with gross in / gross out / net beside it, and a
        // capacity bar underneath whose length scales gently with storage size
        // and whose colour warns as the store heads for empty.
        private void DrawEconomyStrip(float width)
        {
            Dictionary<string, float> eco;
            lock (_ecoLock) eco = _eco;
            if (eco == null) return;

            var centre = width / 2f;
            GUI.DrawTexture(new Rect(0, 0, width, StripHeight), _texPanel);

            DrawStripHalf(eco, "alloy", "ALLOY", AlloyColour, 0f, centre);
            DrawStripHalf(eco, "energy", "ENERGY", EnergyColour, centre, centre);

            // Hairline separators.
            GUI.DrawTexture(new Rect(centre - 1, 6, 2, StripHeight - 12), _texBarBack);
            GUI.DrawTexture(new Rect(0, StripHeight - 1, width, 1), _texBarBack);
        }

        private void DrawStripHalf(Dictionary<string, float> eco, string key, string label, Color baseColour, float x, float w)
        {
            float V(string name) => eco.TryGetValue(key + name, out var v) ? v : 0f;

            var current = V("StorageCurrent");
            var limit = Mathf.Max(1f, V("StorageLimit"));
            var incomeRaw = V("GeneratedIncome") + V("HarvestIncome");
            // Lua sends these negated: Total = what builds asked for,
            // Stalled = what the economy actually paid out.
            var wantedRaw = -V("RequestedTotal");
            var spendRaw = -V("RequestedStalled");
            var netRaw = incomeRaw - spendRaw;
            var stalling = wantedRaw - spendRaw > 0.5f;

            if (!_smooth.TryGetValue(key, out var s)) _smooth[key] = s = new float[3];
            s[0] += (incomeRaw - s[0]) * 0.2f;
            s[1] += (spendRaw - s[1]) * 0.2f;
            s[2] += (netRaw - s[2]) * 0.15f;
            var income = s[0];
            var spend = s[1];
            var net = s[2];

            const float pad = 16f;
            var inner = w - pad * 2f;

            // --- row 1: label + storage on the left, flows on the right ---
            _stStripLabel.normal.textColor = baseColour;
            GUI.Label(new Rect(x + pad, 7f, 70f, 20f), label, _stStripLabel);

            var storageText = Fmt(current);
            var storageWidth = _stStripValue.CalcSize(new GUIContent(storageText)).x;
            GUI.Label(new Rect(x + pad + 58f, 3f, storageWidth + 8f, 24f), storageText, _stStripValue);
            GUI.Label(new Rect(x + pad + 58f + storageWidth + 8f, 8f, 90f, 18f), "/ " + Fmt(limit), _stStripMax);

            // Right cluster: +in  −out  net.
            var netText = (net >= 0f ? "+" : "−") + Fmt(Mathf.Abs(net)) + "/s";
            _stStripNet.normal.textColor = stalling ? DangerColour : net >= 0f ? GainColour : LossColour;
            GUI.Label(new Rect(x + w - pad - 108f, 4f, 108f, 22f), netText, _stStripNet);

            GUI.Label(new Rect(x + w - pad - 108f - 150f, 7f, 70f, 18f), "+" + Fmt(income), _stStripIn);
            GUI.Label(new Rect(x + w - pad - 108f - 76f, 7f, 70f, 18f), "−" + Fmt(spend), _stStripOut);

            // --- row 2: capacity bar ---
            var lengthFactor = Mathf.Clamp(0.45f + 0.15f * Mathf.Log10(limit / 400f), 0.45f, 1f);
            var barRect = new Rect(x + pad, 32f, inner * lengthFactor, 9f);
            GUI.DrawTexture(barRect, _texBarBack);

            var colour = FillColour(baseColour, current, net, stalling);
            var previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(current / limit), barRect.height), _texWhite);
            GUI.color = previous;

            // Warning chip rides at the end of the bar row.
            string chip = null;
            if (stalling) chip = "STALL −" + Fmt(wantedRaw - spendRaw) + "/s";
            else if (net < -0.5f)
            {
                var tte = current / -net;
                if (tte < 120f) chip = "empty in " + tte.ToString("0") + "s";
            }
            if (chip != null)
            {
                var chipWidth = _stStripChip.CalcSize(new GUIContent(chip)).x + 14f;
                var chipRect = new Rect(x + w - pad - chipWidth, 30f, chipWidth, 14f);
                GUI.color = stalling ? DangerColour : new Color(0.75f, 0.45f, 0.15f, 0.9f);
                GUI.DrawTexture(chipRect, _texWhite);
                GUI.color = previous;
                GUI.Label(chipRect, chip, _stStripChip);
            }
        }

        private static Color FillColour(Color baseColour, float stored, float net, bool stalling)
        {
            if (stalling) return DangerColour;
            if (net >= -0.5f) return baseColour;

            var timeToEmpty = stored / -net;
            var urgency = Mathf.Clamp01(1f - timeToEmpty / 45f);
            var colour = Color.Lerp(baseColour, DangerColour, urgency * 0.9f);
            if (timeToEmpty < 10f)
            {
                colour = Color.Lerp(colour, DangerColour, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f));
            }
            return colour;
        }

        // Commander button, top-right under the economy strip: click to select
        // the commander and fly the camera to it; the bar underneath is its
        // health, always visible so a commander under fire is obvious.
        private void DrawCommanderWidget(float width)
        {
            if (_commanderLocalIndex < 0) return;

            const float w = 108f;
            const float h = 66f;
            var rect = new Rect(width - w - 14f, StripHeight + 10f, w, h);
            var hover = rect.Contains(Event.current.mousePosition);

            GUI.DrawTexture(rect, _texPanel);
            if (hover) GUI.DrawTexture(rect, _texRowHover);

            var frac = _commanderMaxHealth > 0f ? Mathf.Clamp01(_commanderHealth / _commanderMaxHealth) : 1f;
            var hurt = frac < 0.999f;

            _stCmdLabel.normal.textColor = hurt && frac < 0.35f ? DangerColour : new Color(0.85f, 0.9f, 0.97f);
            GUI.Label(new Rect(rect.x, rect.y + 6f, rect.width, 20f), "COMMANDER", _stCmdLabel);

            // The game's own strategic icon, sampled out of its atlas.
            var previous = GUI.color;
            var iconRect = new Rect(rect.center.x - 13f, rect.y + 20f, 26f, 26f);
            if (_iconAtlas != null && _iconUvRects != null &&
                _commanderIconIndex >= 0 && _commanderIconIndex < _iconUvRects.Count)
            {
                GUI.color = _ownArmyColourUi ?? Color.white;
                GUI.DrawTextureWithTexCoords(iconRect, _iconAtlas, _iconUvRects[_commanderIconIndex]);
                GUI.color = previous;
            }
            else
            {
                GUI.color = hurt && frac < 0.35f ? DangerColour : new Color(0.55f, 0.78f, 1f, 0.95f);
                GUI.Label(new Rect(rect.x, rect.y + 20f, rect.width, 18f), "◆", _stCmdGlyph);
                GUI.color = previous;
            }

            // Health bar.
            var barRect = new Rect(rect.x + 10f, rect.yMax - 12f, rect.width - 20f, 6f);
            GUI.DrawTexture(barRect, _texBarBack);
            GUI.color = frac > 0.6f ? new Color(0.42f, 0.85f, 0.5f) : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour;
            GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * frac, barRect.height), _texWhite);
            GUI.color = previous;

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover)
            {
                _pendingCommander = true;
                _applyOnFrame = -1;
                Event.current.Use();
            }
        }

        private void DrawIdleWindow(int id)
        {
            List<IdleGroup> groups;
            lock (_groupLock) groups = _idleGroups;

            _stName.normal.textColor = new Color(1f, 0.62f, 0.2f);
            GUI.Label(new Rect(8, 4, 150, 18), "IDLE", _stName);
            if (_pollStatus != "ok")
            {
                GUI.Label(new Rect(84, 6, 68, 14), _pollStatus, _stSub);
            }

            // One clickable row per tech tier; clicking selects that group,
            // and the ALL row selects every idle engineer.
            var y = 23f;
            foreach (var group in groups)
            {
                // There is only ever one commander, so its row needs no count.
                y = DrawIdleRow(group.Label, group.Tier == 0 ? -1 : group.Count, group.UnitIds, y);
            }

            if (groups.Count > 1)
            {
                GUI.DrawTexture(new Rect(8, y, _idleRect.width - 16, 1), _texBarBack);
                y += 3;
                y = DrawIdleRow("ALL", _idleCount, groups.SelectMany(g => g.UnitIds).ToList(), y);
            }

            _idleRect.height = y + 5f;
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private float DrawIdleRow(string label, int count, List<int> ids, float y)
        {
            var row = new Rect(4, y, _idleRect.width - 8, 18);
            var hover = row.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(row, _texRowHover);

            GUI.Label(new Rect(row.x + 5, row.y, 90, 18), label, _stRowLabel);
            if (count >= 0)
            {
                GUI.Label(new Rect(row.x + 42, row.y, 30, 18), count.ToString(), _stRowCount);
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover && ids.Count > 0)
            {
                _pendingSelection = new List<int>(ids);
                _applyOnFrame = -1;
                Event.current.Use();
            }
            return y + 19f;
        }

        private static void EnsureStyles()
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

        // ---- diagnostics (F9) ---------------------------------------------

        private static void DumpHierarchy()
        {
            _log.LogInfo("=== UI hierarchy dump ===");
            var lines = 0;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                _log.LogInfo($"--- scene '{scene.name}' ---");
                foreach (var root in scene.GetRootGameObjects())
                {
                    DumpNode(root.transform, 0, ref lines);
                }
            }
            _log.LogInfo($"=== dump complete ({lines} nodes) ===");
        }

        private static void DumpNode(Transform node, int depth, ref int lines)
        {
            if (depth > 12 || lines > 6000) return;

            var rectInfo = "";
            if (node is RectTransform rect)
            {
                rectInfo = $" [rect {rect.rect.width:F0}x{rect.rect.height:F0} @ {rect.anchoredPosition.x:F0},{rect.anchoredPosition.y:F0}]";
            }
            var components = string.Join(",", node.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .Where(n => n != "Transform" && n != "RectTransform" && n != "CanvasRenderer"));

            _log.LogInfo($"{new string(' ', depth * 2)}{node.name}{(node.gameObject.activeInHierarchy ? "" : " (inactive)")}{rectInfo}{(components.Length > 0 ? " {" + components + "}" : "")}");
            lines++;

            for (var i = 0; i < node.childCount; i++)
            {
                DumpNode(node.GetChild(i), depth + 1, ref lines);
            }
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
