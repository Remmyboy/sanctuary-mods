using System;
using System.Text;
using EM.Network;
using EM.Network.Lobby;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace Sanctuary.ModApi
{
    // The hooks into the game's lobby and match lifecycle. Each is applied on
    // its own, so a game update that renames one target costs that feature,
    // logged, rather than the whole API.
    internal static class GamePatches
    {
        internal static void Apply(Harmony h)
        {
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.CreateLobby), prefix: nameof(CreateLobbyPrefix));
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.JoinLobby), prefix: nameof(JoinLobbyPrefix));
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.LeaveLobby), postfix: nameof(LeftPostfix));
            Patch(h, typeof(EM.DOTS.Engine.Loader.EngineLoader), nameof(EM.DOTS.Engine.Loader.EngineLoader.CleanUpGame), postfix: nameof(CleanUpPostfix));
            Patch(h, typeof(SteamManager), nameof(SteamManager.AdvertiseServer), prefix: nameof(AdvertisePrefix));
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.HandleHostMessage), prefix: nameof(HostMessagePrefix));
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.HandleClientMessage), prefix: nameof(ClientMessagePrefix));
            Patch(h, typeof(LobbyManager), nameof(LobbyManager.CanStartGame), postfix: nameof(CanStartPostfix));
            Patch(h, typeof(EM.UI.InterfaceManager), "OnLobbyStartGamePressed", prefix: nameof(StartPressedPrefix));
            Patch(h, typeof(EM.UI.LobbyInterface), nameof(EM.UI.LobbyInterface.UpdateData), postfix: nameof(LobbyUiPostfix));
        }

        private static void Patch(Harmony h, Type type, string method, string prefix = null, string postfix = null)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    ModApiPlugin.Log.LogWarning($"{type.Name}.{method} not found in this game version; that hook is off.");
                    return;
                }
                h.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(GamePatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(GamePatches), postfix));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Hook on {type.Name}.{method} failed: {e.Message}");
            }
        }

        // ---- vanilla outside lobbies -----------------------------------------

        private static void CreateLobbyPrefix(LobbyManager.LobbyProperties lobbyProperties)
        {
            try { Lobby.OnCreateLobby(lobbyProperties.name); }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Before creating a lobby: {e}"); }
        }

        private static void JoinLobbyPrefix()
        {
            try { Lobby.OnJoinLobby(); }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Before joining a lobby: {e}"); }
        }

        private static void LeftPostfix()
        {
            try { Lobby.OnLeftLobbyOrMatch(); }
            catch (Exception e) { ModApiPlugin.Log.LogError($"After leaving a lobby: {e}"); }
        }

        private static void CleanUpPostfix()
        {
            try
            {
                if (ModApiPlugin.MatchWasUnderway)
                {
                    ModApiPlugin.MatchWasUnderway = false;
                    ModEvents.RaiseMatchEnded();
                }
                Lobby.OnLeftLobbyOrMatch();
            }
            catch (Exception e) { ModApiPlugin.Log.LogError($"After a match: {e}"); }
        }

        // The browser greys out Join when the advertised version#hash differs
        // from its own. A player browsing is always vanilla, so the host
        // advertises the vanilla hash whatever it has picked: everyone can
        // get in and see what the lobby needs. "[mods]" on the name says so
        // up front.
        private static void AdvertisePrefix(ref string serverName, ref string gameTags)
        {
            try
            {
                if (Overlay.IsVanilla || string.IsNullOrEmpty(Overlay.VanillaHash)) return;
                var version = TrimUtf8(Application.version + "#" + Overlay.VanillaHash, 14);
                var parts = gameTags.Split(';');
                for (var i = 0; i < parts.Length; i++)
                    if (parts[i].StartsWith("v:", StringComparison.Ordinal)) parts[i] = "v:" + version;
                gameTags = string.Join(";", parts);
                if (Lobby.HostSelectionActive && !serverName.EndsWith(" [mods]", StringComparison.Ordinal))
                    serverName = serverName + " [mods]";
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"Advert rewrite failed: {e.Message}"); }
        }

        private static string TrimUtf8(string s, int maxBytes)
        {
            var sb = new StringBuilder();
            var used = 0;
            foreach (var c in s)
            {
                var n = Encoding.UTF8.GetByteCount(new[] { c });
                if (used + n > maxBytes) break;
                sb.Append(c);
                used += n;
            }
            return sb.ToString();
        }

        // ---- our messages ------------------------------------------------------

        private static bool HostMessagePrefix(PlayerID sender, NativeArray<byte> payload)
        {
            try
            {
                if (!LobbyManager.isHostRunning || !LobbyProtocol.TryPeekType(payload, out var type)) return true;
                if (LobbyProtocol.IsOurs(type))
                {
                    Lobby.HostReceived(sender, type, payload);
                    return false;
                }
                // The authoritative gate: whoever asked (the Start button, or
                // a mod calling RequestStartGame), the host's own StartGame
                // goes no further while anyone lacks the mods.
                if (type == (byte)LobbyMessageType.StartGame && LobbyManager.hostState != null && sender == LobbyManager.hostState.hostID)
                    return HostStartGate();
            }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Mod message on host: {e}"); }
            return true;
        }

        private static bool HostStartGate()
        {
            try
            {
                if (!Lobby.HostStartGame(out var reason))
                {
                    ModApiPlugin.Log.LogWarning($"Start refused: {reason}");
                    ShowError(reason);
                    return false;
                }
            }
            catch (Exception e)
            {
                if (!GateFailed(e, "the host's start check")) return true;
                Lobby.HostStartFailed();
                return false;
            }
            // The host's world loads next, and reads unit meshes. Art only:
            // a failure here costs looks, not sync, so the match still goes.
            try { Packs.Sync(Overlay.Applied); }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Mounting gameplay mods' art packs: {e}"); }
            return true;
        }

        // The gate's own code threw. With gameplay mods picked the match
        // could start with someone lacking them, so the start is refused and
        // the host told why; with none picked it is a vanilla lobby, which a
        // bug in this API must never stop. True when the start is refused.
        private static bool _gateErrorLogged;

        private static bool GateFailed(Exception e, string where)
        {
            var refuse = Lobby.HostHasPicks;
            if (!_gateErrorLogged)
            {
                _gateErrorLogged = true;
                ModApiPlugin.Log.LogError($"Mod API error in {where} ({(refuse ? "start refused: gameplay mods are picked" : "start allowed: no gameplay mods picked")}): {e}");
            }
            if (refuse) ShowError("Gameplay mods: the Mod API's start check failed (see the log), so the match can't start with mods. Untick the gameplay mods to play vanilla.");
            return refuse;
        }

        private static bool ClientMessagePrefix(NativeArray<byte> payload)
        {
            try
            {
                if (!LobbyManager.IsInLobby || !LobbyProtocol.TryPeekType(payload, out var type)) return true;
                if (LobbyProtocol.IsOurs(type))
                {
                    Lobby.ClientReceived(type, payload);
                    return false;
                }
                if (type == (byte)LobbyMessageType.FullState) Lobby.ClientSawFullState();
            }
            catch (Exception e) { ModApiPlugin.Log.LogError($"Mod message on client: {e}"); }
            return true;
        }

        // ---- the Start gate, where the game asks -----------------------------

        private static void CanStartPostfix(ref bool __result)
        {
            try
            {
                if (__result && LobbyManager.isHostRunning && !Lobby.GateOpen(out _)) __result = false;
            }
            catch (Exception e)
            {
                // The game asks this often; GateFailed logs once. No message
                // here: the press itself (StartPressedPrefix) says why.
                if (Lobby.HostHasPicks) __result = false;
                if (!_gateErrorLogged)
                {
                    _gateErrorLogged = true;
                    ModApiPlugin.Log.LogError($"Mod API error in the lobby's can-start check: {e}");
                }
            }
        }

        // Before the game's own check, whose message ("everyone must be
        // ready...") would be the wrong reason, and before it switches to
        // the loading screen.
        private static bool StartPressedPrefix()
        {
            try
            {
                if (Lobby.GateOpen(out var reason)) return true;
                // Only a change still reaching everyone: start once it has.
                if (Lobby.QueueStart()) return false;
                ShowError(reason);
                return false;
            }
            catch (Exception e) { return !GateFailed(e, "the Start button's check"); }
        }

        private static void LobbyUiPostfix()
        {
            Lobby.RefreshStartButton();
        }

        internal static void ShowError(string message)
        {
            try
            {
                var ui = EM.UI.LobbyInterface.Instance;
                if (ui != null) ui.AddErrorChatMessage(message);
            }
            // The lobby screen going as the message comes: there is nothing
            // left to show it on.
            catch { /* ignored, see above */ }
        }
    }
}
