using System;
using System.Collections.Generic;
using System.Linq;
using EM.Core;
using EM.Network;
using EM.UI;
using HarmonyLib;
using BeamDropdown = Michsky.UI.Beam.Dropdown;
using LobbyPlayer = EM.Network.Lobby.LobbyPlayer;

namespace Sanctuary.ModApi
{
    /// The lobby rows' Player/AI dropdown, with the installed mods' AIs
    /// after "AI": the host gives a seat one by picking it there, on an AI
    /// row or an empty one. With no AI mods installed the dropdown is the
    /// game's own. Other players see the host's pick in the same place.
    internal static class AiLobby
    {
        // The game's two entries: Player (0) and AI (1).
        private const int StockCount = 2;

        private static readonly AccessTools.FieldRef<PlayerListItem, BeamDropdown> DropdownOf =
            AccessTools.FieldRefAccess<PlayerListItem, BeamDropdown>("typeDropdown");

        // The row's own event, which the lobby screen listens to.
        private static readonly AccessTools.FieldRef<PlayerListItem, PlayerListItem.TypeChangeEventHandler> TypeChangedOf =
            AccessTools.FieldRefAccess<PlayerListItem, PlayerListItem.TypeChangeEventHandler>("OnPlayerTypeChanged");

        internal static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PlayerListItem), "UpdatePlayerTypeDropdown"),
                    postfix: new HarmonyMethod(typeof(AiLobby), nameof(RowUpdatePostfix)));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Lobby AI dropdown hook failed, so seats can't be given a mod's AI: {e.Message}");
            }
        }

        private struct Entry
        {
            public string Label;
            public string Mod;
            public string Key;
        }

        /// What follows "AI" in a row's dropdown. The host: every AI its mods
        /// bring. Anyone else: the host's pick for this row, if any (the
        /// dropdown is the host's alone).
        private static List<Entry> EntriesFor(PlayerListItem row, bool host)
        {
            if (!Lobby.InLobby || Lobby.IsLadderLobby) return new List<Entry>();
            if (host)
            {
                if (!Lobby.CanChangeSelection && Lobby.SeatAi(row.slot) == null) return new List<Entry>();
                return Ais.Available.Select(a => new Entry { Label = a.Label, Mod = a.Mod.Id, Key = a.Ai.Key }).ToList();
            }
            var seat = Lobby.SeatAi(row.slot);
            return seat == null
                ? new List<Entry>()
                : new List<Entry> { new Entry { Label = seat.Value.label, Mod = seat.Value.modId, Key = seat.Value.key } };
        }

        private static void RowUpdatePostfix(PlayerListItem __instance, bool isCurrentUserHost)
        {
            try { Sync(__instance, isCurrentUserHost); }
            catch (Exception e) { ModApiPlugin.Log.LogWarning($"AI seat dropdown: {e.Message}"); }
        }

        private static void Sync(PlayerListItem row, bool host)
        {
            var dropdown = row == null ? null : DropdownOf(row);
            if (dropdown == null || dropdown.items == null || dropdown.items.Count < StockCount) return;
            if (row.player.type == PlayerType.Player) return; // the game hides it

            var entries = EntriesFor(row, host);
            var items = dropdown.items;
            var same = items.Count == StockCount + entries.Count &&
                       entries.Select((e, i) => items[StockCount + i].itemName == e.Label).All(x => x);
            if (!same)
            {
                items.RemoveRange(StockCount, items.Count - StockCount);
                foreach (var e in entries) dropdown.CreateNewItem(e.Label, false);
                dropdown.selectedItemIndex = Math.Min(dropdown.selectedItemIndex, items.Count - 1);
                dropdown.Initialize();
                FactionLobby.FitHeader(dropdown);
            }
            if (entries.Count == 0 && same) return; // the game's own dropdown, untouched

            // Which entry the row shows: Player for an empty row (as the game
            // has it), else AI or the seat's mod AI.
            var index = row.player.type == PlayerType.AI ? 1 : 0;
            var seat = row.player.type == PlayerType.AI ? Lobby.SeatAi(row.slot) : null;
            if (seat != null)
            {
                var at = entries.FindIndex(e => e.Mod == seat.Value.modId && e.Key == seat.Value.key);
                if (at >= 0) index = StockCount + at;
            }
            if (dropdown.selectedItemIndex != index) dropdown.SetDropdownIndex(index);

            // Our listener in place of the game's: its handler turns the
            // index into a PlayerType, and 2 would empty the seat.
            dropdown.onValueChanged.RemoveAllListeners();
            var slot = row.slot;
            var player = row.player;
            dropdown.onValueChanged.AddListener(value => Picked(row, player, slot, value, entries));
        }

        private static void Picked(PlayerListItem row, LobbyPlayer player, byte slot, int value, List<Entry> entries)
        {
            try
            {
                var wasAi = player.type == PlayerType.AI;
                if (value < StockCount)
                {
                    if (value == 1) Lobby.SetSeatAi(slot, null, null);
                    if (value == 0 || !wasAi) TypeChangedOf(row)?.Invoke(player, slot, (PlayerType)value);
                    return;
                }
                var i = value - StockCount;
                if (i >= entries.Count) return;
                // An empty row becomes an AI seat first; the pick waits for it.
                if (!Lobby.SetSeatAi(slot, entries[i].Mod, entries[i].Key))
                {
                    ModApiPlugin.Log.LogWarning($"Couldn't give seat {slot} the AI {entries[i].Label}.");
                    Redraw();
                    return;
                }
                if (!wasAi) TypeChangedOf(row)?.Invoke(player, slot, PlayerType.AI);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"AI seat pick: {e.Message}");
            }
        }

        /// Draws every row again, for a change of pick the game doesn't know
        /// about. Never while the lobby screen isn't up.
        internal static void Redraw()
        {
            try
            {
                var ui = LobbyInterface.Instance;
                if (ui != null && LobbyManager.CurrentState != null && LobbyManager.IsInLobby) ui.UpdateData(LobbyManager.CurrentState);
            }
            catch { }
        }
    }
}
