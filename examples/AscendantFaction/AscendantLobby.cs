using System;
using System.Linq;
using BepInEx;
using EM.Core;
using EM.Network;
using EM.UI;
using HarmonyLib;
using UnityEngine;
using BeamDropdown = Michsky.UI.Beam.Dropdown;

namespace AscendantFaction
{
    // The C# half of the Ascendant faction: the lobby's faction dropdown.
    //
    // The game's C# knows exactly three factions (EM.Core.Faction), and the
    // lobby's player rows fill their faction dropdown with those three by name.
    // Everything past the dropdown already copes with a fourth: the choice
    // travels as a byte nobody range-checks, and Lua turns it into an index
    // into FactionsData, which the Lua half of this mod extends.
    //
    // This is a gameplay mod's DLL, so the mod loader only starts it while the
    // lobby host has picked the mod (and destroys it when the pick goes, or
    // the lobby ends): the fourth option exists exactly when every player's
    // game can make sense of it.
    [BepInPlugin("sanctuarymods.example.ascendant", "Ascendant faction", "0.1.0")]
    public class AscendantLobby : BaseUnityPlugin
    {
        private const string FactionName = "Ascendant";
        private const Faction Ascendant = (Faction)3;

        private static readonly AccessTools.FieldRef<PlayerListItem, BeamDropdown> FactionDropdown =
            AccessTools.FieldRefAccess<PlayerListItem, BeamDropdown>("factionDropdown");

        private Harmony _harmony;

        private void Awake()
        {
            _harmony = new Harmony("sanctuarymods.example.ascendant." + Guid.NewGuid().ToString("N"));
            // Rows the lobby creates later (it pools them) get the option too.
            _harmony.Patch(AccessTools.Method(typeof(PlayerListItem), nameof(PlayerListItem.Awake)),
                postfix: new HarmonyMethod(typeof(AscendantLobby), nameof(RowAwakePostfix)));
            foreach (var row in Rows()) SetOptions(row, true);
            Redraw();
            Logger.LogInfo("Ascendant is on the lobby's faction list.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            foreach (var row in Rows()) SetOptions(row, false);
            ReleaseAscendantSeats();
            Redraw();
            Logger.LogInfo("Ascendant is off the lobby's faction list.");
        }

        private static void RowAwakePostfix(PlayerListItem __instance) => SetOptions(__instance, true);

        private static PlayerListItem[] Rows() =>
            Resources.FindObjectsOfTypeAll<PlayerListItem>().Where(r => r != null && r.gameObject.scene.IsValid()).ToArray();

        /// Four entries or the game's three, in the game's order, so the
        /// dropdown index stays the faction number.
        private static void SetOptions(PlayerListItem row, bool withAscendant)
        {
            try
            {
                var dropdown = FactionDropdown(row);
                if (dropdown == null) return;
                var has = dropdown.items.Count > 3;
                if (has == withAscendant) return;
                if (withAscendant) dropdown.CreateNewItem(FactionName, false);
                else dropdown.items.RemoveRange(3, dropdown.items.Count - 3);
                dropdown.Initialize();
            }
            catch (Exception e)
            {
                BepInEx.Logging.Logger.CreateLogSource("Ascendant").LogWarning($"Faction dropdown: {e.Message}");
            }
        }

        /// The mod was dropped from the lobby while someone was on Ascendant:
        /// without the mod there's no fourth faction, and a match started like
        /// that would find no faction 4. Each modded client moves its own
        /// player back to EDA; the host also moves its AI players.
        private static void ReleaseAscendantSeats()
        {
            try
            {
                var state = LobbyManager.CurrentState;
                if (state == null || !LobbyManager.IsInLobby || LobbyManager.lobbyGameStatus != LobbyManager.LobbyGameStatus.lobby) return;
                foreach (var p in state.players.Where(p => p.faction == Ascendant))
                {
                    if (LobbyManager.IsLocalPlayer(p) || p.type == PlayerType.AI && LobbyManager.IsCurrentUserHost())
                        LobbyManager.SetMemberFaction(p, Faction.EDA);
                }
            }
            catch { }
        }

        private static void Redraw()
        {
            try
            {
                var ui = LobbyInterface.Instance;
                if (ui != null && LobbyManager.CurrentState != null) ui.UpdateData(LobbyManager.CurrentState);
            }
            catch { }
        }
    }
}
