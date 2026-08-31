using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SanctuaryHud
{
    // Lets the main menu open when the entitlement API can't be reached, so a
    // local LAN game against AI can be hosted for testing maps.
    //
    // Why this is needed: EM.UI.InterfaceManager.Start() fires
    //   SssApiClient.GetPermissions(SteamUser.GetSteamID().m_SteamID, ...)
    // at the developers' PermissionCheck endpoint. Both a request error and a
    // HasMulti == false response land on MainMenuInterface.OnPermissionDenied(),
    // which raises a full-screen canvas whose only button calls
    // Application.Quit(). With the demo's multiplayer backend closed the
    // request simply errors, so every route out of the menu is shut - including
    // Multiplayer LAN, which TcpLobbyBackend hosts on a local port and which
    // needs no servers whatsoever.
    //
    // There is no config or launch flag that helps here. InterfaceManager has a
    // TryAutoStart() that would host over LAN with no menu at all, fed by
    // Build.json's `autoStart` and a -autoplay= command line override, but
    // nothing in this build ever calls it. Singleplayer is a stub that logs
    // "Not implemented yet!". So the menu is the only way in.
    //
    // Scope: this flips HasMulti, which gates the lobby UI on this client, and
    // nothing else. HasCampaign and HasDev are left exactly as the API returned
    // them - those gate unreleased content rather than a dead server check.
    // No server access is granted or attempted. On builds where the
    // entitlement check passes (e.g. the playtest), the patch simply never
    // fires. Unload the mod (F8 manager) or set Enabled=false before sharing
    // a build.
    [BepInPlugin("com.sanctuarydb.lanlobbyunlock", "LAN Lobby Unlock", "0.1.0")]
    public class LanLobbyUnlockPlugin : BaseUnityPlugin
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgEnabled;

        private static MethodInfo _onPermissionsPassed;
        private static FieldInfo _hasMultiField;

        private void Awake()
        {
            _log = Logger;
            _cfgEnabled = Config.Bind("LocalTesting", "Enabled", true,
                "Let the main menu open when the entitlement API is unreachable, so Multiplayer LAN can host a " +
                "local game against AI. Affects this client's menu only - it grants no server access.");
            if (!_cfgEnabled.Value)
            {
                _log.LogInfo("LAN lobby unlock disabled by config; leaving the gate alone.");
                return;
            }
            try
            {
                PatchPermissionGate();
            }
            catch (Exception e)
            {
                _log.LogError($"LAN lobby unlock failed (menu will still gate on the API): {e}");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void PatchPermissionGate()
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(GetTypesSafe)
                .ToList();

            var menuType = types.FirstOrDefault(t => t.FullName == "EM.UI.MainMenuInterface");
            if (menuType == null)
            {
                _log.LogWarning("LAN lobby unlock: EM.UI.MainMenuInterface not found; leaving the gate alone.");
                return;
            }

            const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var denied = menuType.GetMethod("OnPermissionDenied", Inst);
            _onPermissionsPassed = menuType.GetMethod("OnPermissionsPassed", Inst);
            if (denied == null || _onPermissionsPassed == null)
            {
                _log.LogWarning("LAN lobby unlock: OnPermissionDenied/OnPermissionsPassed missing; leaving the gate alone.");
                return;
            }

            // ApiPermissions is an internal static holder, so reflect it out.
            var apiPermissions = types.FirstOrDefault(t => t.FullName == "EM.Permissions.ApiPermissions");
            _hasMultiField = apiPermissions?.GetField("HasMulti",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (_hasMultiField == null)
                _log.LogWarning("LAN lobby unlock: ApiPermissions.HasMulti not found; the menu will open but " +
                                "anything else reading that flag stays false.");

            _harmony = new Harmony("com.sanctuarydb.lanlobbyunlock." + Guid.NewGuid().ToString("N").Substring(0, 8));
            _harmony.Patch(denied, prefix: new HarmonyMethod(typeof(LanLobbyUnlockPlugin), nameof(PermissionDeniedPrefix)));
            _log.LogInfo("LAN lobby unlock active: the menu will open even if the entitlement API is unreachable. " +
                         "Use Multiplayer LAN, set the second slot to AI, and pick your map.");
        }

        // Replaces the deny canvas with the pass path. Returning false skips the
        // original, so permissionDeniedCanvas is never enabled.
        private static bool PermissionDeniedPrefix(object __instance)
        {
            try { _hasMultiField?.SetValue(null, true); }
            catch (Exception e) { _log.LogWarning($"LAN lobby unlock: could not set HasMulti: {e.Message}"); }

            try
            {
                _onPermissionsPassed?.Invoke(__instance, null);
                _log.LogInfo("Entitlement check unavailable - opening the menu for local LAN play.");
            }
            catch (Exception e)
            {
                // Fall through to the original so the player sees the real state
                // rather than a menu wedged half-open.
                _log.LogError($"LAN lobby unlock: OnPermissionsPassed threw, showing the original screen: {e}");
                return true;
            }
            return false;
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
