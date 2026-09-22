using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The idle panel: one clickable tile per tech tier of idle engineers
    // (plus the commander), then idle factories by type and tier; clicking
    // selects that group. A tile is the unit's own build-menu art with the
    // count in the corner and the tier under it — the same shape as the
    // eco panels, so the two read as one set. It stands on the game's own
    // HUD canvas (HudCanvas, HudPanel): dragged as uGUI, in the game's
    // font, at its UI Scale.
    // Standalone mod — the ECS poll, Lua selection bridge and canvas
    // helpers come from shared\, compiled into this assembly, so it works
    // with or without the HUD mod loaded.
    [BepInPlugin("com.sanctuarydb.idleengineers", "Idle Engineers", "0.5.1")]
    public class IdleEngineersPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgFactories;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;
        private ConfigEntry<float> _cfgScale;
        private ConfigEntry<bool> _cfgLocked;

        private static readonly Color IdleColour = new Color(1f, 0.62f, 0.2f);

        private void Awake()
        {
            _log ??= Logger;

            _cfgFactories = Config.Bind("Factories", "Enabled", true,
                "List idle factories under the engineers, by type (land, air, naval) and tier.");
            _cfgPosX = Config.Bind("Panel", "PosX", 12f, "Idle panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("Panel", "PosY", 250f, "Idle panel Y in 1080p-logical pixels.");
            _cfgLocked = Config.Bind("Panel", "Locked", false, "Keep the panel where it is: no dragging during a game.");
            _cfgScale = Config.Bind("Panel", "Scale", 1f,
                new ConfigDescription("Size of the panel, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));

            try
            {
                // The economy stream doubles as the in-match signal, so this
                // mod carries its own copy of the patch (see ApplyEconomyPatch).
                _harmony = new Harmony("com.sanctuarydb.idleengineers." + Guid.NewGuid().ToString("N").Substring(0, 8));
                ApplyEconomyPatch(_harmony);
            }
            catch (Exception e)
            {
                Logger.LogError($"Idle engineers: economy patch failed (the panel will stay hidden): {e}");
            }
            Logger.LogInfo("Idle Engineers panel loaded (toggle it from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _panel?.Destroy();
            HudCanvas.Destroy();
        }

        private void Update()
        {
            // Read every frame, so the switch on the Mods page takes effect
            // at the next poll.
            _trackIdleFactories = _cfgFactories.Value;
            SharedTick();

            // Under the game's pause menu, a settings screen or the result
            // screen nothing of the game's own HUD shows, so nothing of this
            // should either — as the HUD's strip steps aside.
            HudCanvas.SetShowing(InMatch && !MenuOpen());
            try
            {
                SyncPanel();
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    Logger.LogWarning($"Idle engineers: the panel could not be laid out (logged once): {e}");
                }
            }
        }

        // ---- the panel --------------------------------------------------------------

        private HudPanel _panel;
        private bool _syncLogged;
        private TMP_Text _title, _status;
        private RectTransform _engineerColumn, _factoryRow;
        private TilePool _engineerTiles, _factoryTiles;
        private Image _rule;
        private PanelHeading _factoryHeading;

        private void SyncPanel()
        {
            // The panel only exists when there is something to act on.
            var factoriesIdle = _cfgFactories.Value && _idleFactoryCount > 0;
            var show = InMatch && (_idleCount > 0 || factoriesIdle);
            if (!show)
            {
                _panel?.Show(false);
                return;
            }
            var root = HudCanvas.Ensure();
            if (root == null) return;
            if (_panel == null || !_panel.Alive) BuildPanel(root);

            _panel.Show(true);
            Fill();
            _panel.SetScale(Mathf.Clamp(_cfgScale.Value, 0.7f, 1.6f));
            var at = _panel.Place(new Vector2(_cfgPosX.Value, _cfgPosY.Value));
            // Persist the panel position once the drag is over.
            if (!_panel.Dragging && (Math.Abs(_cfgPosX.Value - at.x) > 0.5f || Math.Abs(_cfgPosY.Value - at.y) > 0.5f))
            {
                _cfgPosX.Value = at.x;
                _cfgPosY.Value = at.y;
            }
        }

        private void BuildPanel(RectTransform root)
        {
            _panel?.Destroy();
            _panel = HudPanel.Create(root, "Idle engineers", () => _cfgLocked.Value);

            var head = _panel.Row("Heading", 8f, TextAnchor.MiddleLeft);
            _title = HudCanvas.Text(head, "Title", 26f, IdleColour, TextAlignmentOptions.MidlineLeft);
            HudCanvas.SetText(_title, "IDLE");
            _status = HudCanvas.Text(head, "Status", 18f, new Color(1f, 1f, 1f, 0.6f), TextAlignmentOptions.MidlineLeft);

            // The commander on a line of its own, then the engineers, a tile
            // per tier, down one column.
            var column = new GameObject("Engineers", typeof(RectTransform));
            column.transform.SetParent(_panel.Rect, false);
            var group = column.AddComponent<VerticalLayoutGroup>();
            group.spacing = 6f;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            _engineerColumn = (RectTransform)column.transform;
            _engineerTiles = new TilePool(_engineerColumn, "Engineers");

            // Factories: a rule, one heading which selects every idle
            // factory, then a tile per type and tier along a row.
            var rule = AccentColour;
            rule.a = 0.35f;
            _rule = HudCanvas.Fill(_panel.Rect, "Rule", rule);
            var ruleLayout = _rule.gameObject.AddComponent<LayoutElement>();
            ruleLayout.preferredHeight = 2f;
            ruleLayout.minHeight = 2f;
            ruleLayout.flexibleWidth = 1f;
            _factoryHeading = PanelHeading.Create(_panel.Rect, "Factories", 20f, IdleColour, TextAlignmentOptions.MidlineLeft);
            HudCanvas.SetText(_factoryHeading.Text, "FACTORIES");
            _factoryRow = _panel.Row("Factory tiles", 6f);
            _factoryTiles = new TilePool(_factoryRow, "Factory");
        }

        private void Fill()
        {
            List<IdleGroup> groups, factories;
            lock (_groupLock)
            {
                groups = _idleGroups;
                factories = _idleFactoryGroups;
            }
            // Switched off mid-match: hide at once rather than at the next poll.
            if (!_cfgFactories.Value) factories = new List<IdleGroup>();

            // On the right half of the screen the panel lays itself out from
            // its right edge; the headings follow.
            var right = _panel.RightAligned;
            _title.alignment = right ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft;
            _factoryHeading.Text.alignment = right ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft;

            var status = _pollStatus != "ok" ? _pollStatus : "";
            HudCanvas.SetText(_status, status);
            if (_status.gameObject.activeSelf != status.Length > 0) _status.gameObject.SetActive(status.Length > 0);

            _engineerTiles.Begin();
            var commander = groups.FirstOrDefault(g => g.Tier == 0);
            // There is only ever one commander, so its tile needs no count.
            if (commander != null) Tile(_engineerTiles.Take(), commander, -1);
            foreach (var group in groups.Where(g => g.Tier != 0)) Tile(_engineerTiles.Take(), group, group.Count);
            _engineerTiles.End();
            if (_engineerColumn.gameObject.activeSelf != groups.Count > 0) _engineerColumn.gameObject.SetActive(groups.Count > 0);

            var showFactories = factories.Count > 0;
            var showRule = showFactories && groups.Count > 0;
            if (_rule.gameObject.activeSelf != showRule) _rule.gameObject.SetActive(showRule);
            if (_factoryHeading.gameObject.activeSelf != showFactories) _factoryHeading.gameObject.SetActive(showFactories);
            if (_factoryRow.gameObject.activeSelf != showFactories) _factoryRow.gameObject.SetActive(showFactories);
            if (showFactories)
            {
                var all = factories.SelectMany(g => g.UnitIds).ToList();
                _factoryHeading.OnClick = all.Count > 0 ? () => Select(all) : (Action)null;
                _factoryTiles.Begin();
                foreach (var group in factories) Tile(_factoryTiles.Take(), group, group.Count);
                _factoryTiles.End();
            }
        }

        /// One tile: the unit's own build-menu art where the game has it
        /// loaded (else the label, so a tile is never blank), the count in
        /// the bottom-right corner, the tier tag bottom-left. Click selects.
        private static void Tile(PanelTile tile, IdleGroup group, int count)
        {
            var ids = group.UnitIds;
            tile.Set(group.PlateId, group.IconId, group.Label, 1f, -1f, null, false, count, false,
                HasSprite(group.IconId) ? group.Label : null, false, null, null);
            tile.OnClick = button =>
            {
                if (button == PointerEventData.InputButton.Left) Select(ids);
            };
        }

        private static void Select(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return;
            _pendingSelection = new List<int>(ids);
            _applyOnFrame = -1;
        }
    }
}
