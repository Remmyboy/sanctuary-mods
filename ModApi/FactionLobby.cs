using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EM.Core;
using EM.Network;
using EM.UI;
using HarmonyLib;
using UnityEngine;
using BeamDropdown = Michsky.UI.Beam.Dropdown;
using LobbyPlayer = EM.Network.Lobby.LobbyPlayer;

namespace Sanctuary.ModApi
{
    /// The lobby's faction dropdowns: the game's three factions, then the
    /// picked mods' (<see cref="Factions.Current"/>), so a faction mod needs
    /// no C# of its own. The game's C# knows three factions, but past the
    /// dropdown the choice is a byte nobody range-checks, and the match's Lua
    /// reads it through FactionsData, which the framework extends to match.
    internal static class FactionLobby
    {
        private const int StockCount = 3;

        private static readonly AccessTools.FieldRef<PlayerListItem, BeamDropdown> DropdownOf =
            AccessTools.FieldRefAccess<PlayerListItem, BeamDropdown>("factionDropdown");

        internal static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PlayerListItem), nameof(PlayerListItem.Awake)),
                    postfix: new HarmonyMethod(typeof(FactionLobby), nameof(RowAwakePostfix)));
                // Before the game selects the player's value in it: a value
                // with no entry would throw and stop the row updating.
                h.Patch(AccessTools.Method(typeof(PlayerListItem), "UpdatePlayerFactionDropdown"),
                    prefix: new HarmonyMethod(typeof(FactionLobby), nameof(RowUpdatePrefix)));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Lobby faction dropdown hooks failed, so modded factions can't be picked: {e.Message}");
            }
        }

        private static void RowAwakePostfix(PlayerListItem __instance) => Sync(__instance);

        private static void RowUpdatePrefix(PlayerListItem __instance) => Sync(__instance);

        /// Makes a row's dropdown the game's three factions, then the modded
        /// ones by value. A value this machine doesn't know (a faction from a
        /// mod it hasn't got) gets a hidden entry, so it still shows a name.
        private static void Sync(PlayerListItem row)
        {
            try
            {
                var dropdown = row == null ? null : DropdownOf(row);
                if (dropdown == null || dropdown.items == null || dropdown.items.Count < StockCount) return;
                var wanted = Math.Max((int)(row.player?.faction ?? Faction.EDA), 0);
                FitHeader(dropdown);

                var entries = Factions.Current.Select(f => (label: f.Label, icon: IconFor(f), hidden: false)).ToList();
                for (var v = StockCount + entries.Count; v <= wanted && v <= 255; v++)
                    entries.Add((label: Factions.NameOf(v), icon: (Sprite)null, hidden: true));

                var items = dropdown.items;
                var same = items.Count == StockCount + entries.Count &&
                           entries.Select((e, i) => (e, item: items[StockCount + i]))
                               .All(x => x.item.itemName == x.e.label && x.item.isInvisible == x.e.hidden && x.item.itemIcon == x.e.icon);
                if (same) return;

                items.RemoveRange(StockCount, items.Count - StockCount);
                foreach (var e in entries)
                {
                    dropdown.CreateNewItem(e.label, e.icon, false);
                    items[items.Count - 1].isInvisible = e.hidden;
                }
                // Initialize shows the selected entry, which may be one just
                // removed; the game selects the player's own right after.
                dropdown.selectedItemIndex = wanted < items.Count ? wanted : 0;
                dropdown.Initialize();
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Faction dropdown: {e.Message}");
            }
        }

        /// The faction column is narrow, sized for "Chosen": a longer name
        /// ("Ascendant (Warden)") shrinks to fit instead of running into the
        /// next column.
        private static void FitHeader(BeamDropdown dropdown)
        {
            var text = dropdown.headerText;
            if (text == null || text.enableAutoSizing) return;
            // The game's text box is wider than the header it sits in (185
            // against 105 at 0.0.1.20), so text never needs shrinking to
            // fit it: fit the box to the header first.
            var box = text.rectTransform;
            if (box.parent is RectTransform header && Mathf.Approximately(box.anchorMin.x, box.anchorMax.x))
            {
                var width = header.rect.width - box.offsetMin.x - 4f;
                if (width > 20f && width < box.rect.width) box.sizeDelta = new Vector2(width, box.sizeDelta.y);
            }
            text.fontSizeMax = text.fontSize;
            text.fontSizeMin = Math.Max(6f, text.fontSize * 0.55f);
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            text.enableAutoSizing = true;
        }

        /// The pick changed which factions exist, or renumbered them. Each
        /// machine moves its own player, and the host its AI players, to the
        /// same faction's new value, or to EDA when it's gone; then every row
        /// is drawn again.
        internal static void OnFactionsChanged(IReadOnlyList<LobbyFaction> previous, IReadOnlyList<LobbyFaction> next)
        {
            var state = LobbyManager.CurrentState;
            if (state != null && LobbyManager.IsInLobby && LobbyManager.lobbyGameStatus == LobbyManager.LobbyGameStatus.lobby)
            {
                var was = previous.ToDictionary(f => f.Value, f => f.Identity);
                var now = new Dictionary<string, int>();
                foreach (var f in next) if (!now.ContainsKey(f.Identity)) now[f.Identity] = f.Value;
                foreach (var p in state.players.ToList())
                {
                    var value = (int)p.faction;
                    if (value < StockCount || !was.TryGetValue(value, out var identity)) continue;
                    var to = now.TryGetValue(identity, out var v) ? v : (int)Faction.EDA;
                    if (to == value || !Mine(p)) continue;
                    try { LobbyManager.SetMemberFaction(p, (Faction)to); }
                    catch (Exception e) { ModApiPlugin.Log.LogWarning($"Moving a lobby seat to faction {to}: {e.Message}"); }
                }
            }
            try
            {
                var ui = LobbyInterface.Instance;
                if (ui != null && LobbyManager.CurrentState != null) ui.UpdateData(LobbyManager.CurrentState);
            }
            catch { }
        }

        private static bool Mine(LobbyPlayer p) =>
            LobbyManager.IsLocalPlayer(p) || p.type == PlayerType.AI && LobbyManager.IsCurrentUserHost();

        // ---- icons -------------------------------------------------------------

        private static readonly Dictionary<string, (DateTime stamp, Sprite sprite)> Icons =
            new Dictionary<string, (DateTime, Sprite)>(StringComparer.OrdinalIgnoreCase);

        private static System.Reflection.MethodInfo _loadImage;

        /// The faction's PNG as a sprite, loaded once per version of the file;
        /// null for none, or one that won't load.
        private static Sprite IconFor(LobbyFaction f)
        {
            if (f.Faction.Icon.Length == 0) return null;
            var path = Path.Combine(f.Mod.Folder, f.Faction.Icon);
            try
            {
                if (!File.Exists(path)) return null;
                var stamp = File.GetLastWriteTimeUtc(path);
                if (Icons.TryGetValue(path, out var cached) && cached.stamp == stamp) return cached.sprite;
                // ImageConversion lives in its own Unity module; looked up so
                // the API needn't reference it.
                _loadImage = _loadImage ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
                    ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (_loadImage == null) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Faction icon " + f.Faction.Key };
                if (!(bool)_loadImage.Invoke(null, new object[] { tex, File.ReadAllBytes(path) }))
                {
                    ModApiPlugin.Log.LogWarning($"Faction icon {f.Faction.Icon} of '{f.Mod.Name}' isn't a PNG Unity can read.");
                    Icons[path] = (stamp, null);
                    return null;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                sprite.name = tex.name;
                Icons[path] = (stamp, sprite);
                return sprite;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning($"Faction icon {f.Faction.Icon} of '{f.Mod.Name}': {e.Message}");
                return null;
            }
        }
    }
}
