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
    // Two panels, in the shape of FA's UI-Party eco manager.
    //
    // The BUILD panel is two columns of tiles, alloy on the left and energy
    // on the right, one tile per template your army has under construction:
    // the unit's build-menu art, its build progress along the top, the spend
    // that column's resource is being asked for in the corner, and how many
    // there are. Each column is sorted by its own resource, so the top of
    // the left column is what is eating the alloy and the top of the right
    // what is eating the energy. Left-click on a tile selects the builders
    // working on it; right-click pauses them (and again to resume), which is
    // the point of an eco manager: seeing what is spending, and stopping it
    // in one click. Where the spend comes from is in Construction.cs.
    //
    // The ALLOY panel carries the extractor tiles over from the old rows:
    // a row per tier, the ones sitting at that tier on the left and, in the
    // same art, the ones upgrading away from it on the right.
    // Assisting an extractor to start its upgrade lives in AssistUpgrade.cs.
    //
    // Both panels have their own position, size and switch, so either can
    // be dropped or shrunk without the other. They stand on the game's own
    // HUD canvas (HudCanvas, HudPanel): dragged as uGUI, so nothing of a
    // drag or a click reaches the map, with the game's font, its UI Scale
    // and its tooltip.
    [BepInPlugin("com.sanctuarydb.ecomanager", "Eco Manager", "0.5.0")]
    public partial class EcoManagerPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgTooltips;
        private ConfigEntry<bool> _cfgBuildEnabled;
        private ConfigEntry<float> _cfgBuildScale;
        private ConfigEntry<float> _cfgBuildPosX;
        private ConfigEntry<float> _cfgBuildPosY;
        private ConfigEntry<bool> _cfgRightClickPauses;
        private ConfigEntry<bool> _cfgExtractorsEnabled;
        private ConfigEntry<float> _cfgExtractorsScale;
        private ConfigEntry<float> _cfgExtractorsPosX;
        private ConfigEntry<float> _cfgExtractorsPosY;
        private ConfigEntry<bool> _cfgLocked;

        private const float MarkSize = 36f;

        private void Awake()
        {
            _log ??= Logger;

            _cfgTooltips = Config.Bind("Panel", "Tooltips", true,
                "Show the game's tooltip while a tile is hovered: name, both rates, progress, " +
                "builders, and what the clicks do.");
            _cfgLocked = Config.Bind("Panel", "Locked", false, "Keep both panels where they are: no dragging during a game.");

            _cfgBuildEnabled = Config.Bind("Build", "Enabled", true,
                "The BUILD panel: everything under construction as tiles, alloy spend down the left column and " +
                "energy down the right. Left-click selects the builders, right-click pauses them.");
            _cfgBuildScale = Config.Bind("Build", "Scale", 1f,
                new ConfigDescription("Size of the BUILD panel.", new AcceptableValueRange<float>(0.5f, 2.5f)));
            _cfgBuildPosX = Config.Bind("Build", "PosX", 12f, "BUILD panel X in 1080p-logical pixels.");
            _cfgBuildPosY = Config.Bind("Build", "PosY", 560f, "BUILD panel Y in 1080p-logical pixels.");
            _cfgRightClickPauses = Config.Bind("Build", "RightClickPauses", true,
                "Right-clicking a build tile pauses every builder working on that template, and again resumes " +
                "them. Sends the game's own Pause toggle. Off, right-click does nothing.");

            _cfgExtractorsEnabled = Config.Bind("Extractors", "Enabled", true,
                "The ALLOY panel: a row per tier: the extractors at that tier on the left, the ones " +
                "upgrading away from it on the right. Clicking selects the group.");
            _cfgExtractorsScale = Config.Bind("Extractors", "Scale", 1f,
                new ConfigDescription("Size of the ALLOY panel.", new AcceptableValueRange<float>(0.5f, 2.5f)));
            // The same keys the old single panel used, so a saved position carries over.
            _cfgExtractorsPosX = Config.Bind("Panel", "PosX", 12f, "ALLOY panel X in 1080p-logical pixels.");
            _cfgExtractorsPosY = Config.Bind("Panel", "PosY", 420f, "ALLOY panel Y in 1080p-logical pixels.");

            AwakeAssistUpgrade();

            try
            {
                // The economy stream doubles as the in-match signal, so this
                // mod carries its own copy of the patch (see ApplyEconomyPatch).
                _harmony = new Harmony("com.sanctuarydb.ecomanager." + Guid.NewGuid().ToString("N").Substring(0, 8));
                ApplyEconomyPatch(_harmony);
            }
            catch (Exception e)
            {
                Logger.LogError($"Eco manager: economy patch failed (the panels will stay hidden): {e}");
            }
            Logger.LogInfo("Eco Manager panels loaded (toggle them from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            // Unloading via the mod manager must also undo the Lua-side hook,
            // and let go of anything a tile is holding paused.
            RemoveAssistHook();
            ReleaseAllPaused();
            _build?.Destroy();
            _extractors?.Destroy();
            HudCanvas.Destroy();
        }

        private void Update()
        {
            SharedTick();
            UpdateAssistUpgrade(Time.unscaledDeltaTime);
            UpdateConstruction(Time.unscaledDeltaTime);

            // Under the game's pause menu, the Mods page (F8), a settings
            // screen or the result screen nothing of the game's own HUD
            // shows, so nothing of this should either.
            HudCanvas.SetShowing(InMatch && !MenuOpen());
            try
            {
                SyncPanels();
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    Logger.LogWarning($"Eco manager: the panels could not be laid out (logged once): {e}");
                }
            }
        }

        private static void Persist(ConfigEntry<float> x, ConfigEntry<float> y, Vector2 at)
        {
            if (Math.Abs(x.Value - at.x) > 0.5f || Math.Abs(y.Value - at.y) > 0.5f)
            {
                x.Value = at.x;
                y.Value = at.y;
            }
        }

        // ---- the panels -----------------------------------------------------------

        private HudPanel _build, _extractors;
        private bool _syncLogged;

        // The BUILD panel: two column headings, then a row of two tiles per
        // template — the alloy column's on the left, the energy column's on
        // the right, each column in its own order.
        private ColumnHead _alloyHead, _energyHead;
        private readonly List<TileRowSlot> _buildRows = new List<TileRowSlot>();
        private RectTransform _buildRowsBox;

        // The ALLOY panel: the alloy mark and the poll status, then a row
        // per tier of one or two tiles.
        private TMP_Text _extractorStatus;
        private readonly List<TileRowSlot> _extractorRows = new List<TileRowSlot>();
        private RectTransform _extractorRowsBox;

        private sealed class ColumnHead
        {
            internal GameObject Go;
            internal TMP_Text Total, Label;
        }

        private sealed class TileRowSlot
        {
            internal RectTransform Row;
            internal TilePool Pool;
        }

        private void SyncPanels()
        {
            if (!InMatch) return;
            var root = HudCanvas.Ensure();
            if (root == null) return;
            if (_build == null || !_build.Alive) BuildPanels(root);

            // Nothing to manage before the first thing goes up; and a build
            // panel means nothing in a replay's all-armies view.
            var showBuild = _cfgBuildEnabled.Value && _buildGroups.Count > 0 && OwnArmyFocused;
            _build.Show(showBuild);
            if (showBuild)
            {
                FillBuild();
                _build.SetScale(Mathf.Clamp(_cfgBuildScale.Value, 0.5f, 2.5f));
                var at = _build.Place(new Vector2(_cfgBuildPosX.Value, _cfgBuildPosY.Value));
                if (!_build.Dragging) Persist(_cfgBuildPosX, _cfgBuildPosY, at);
            }

            var showExtractors = _cfgExtractorsEnabled.Value && _alloyCount > 0;
            _extractors.Show(showExtractors);
            if (showExtractors)
            {
                FillExtractors();
                _extractors.SetScale(Mathf.Clamp(_cfgExtractorsScale.Value, 0.5f, 2.5f));
                var at = _extractors.Place(new Vector2(_cfgExtractorsPosX.Value, _cfgExtractorsPosY.Value));
                if (!_extractors.Dragging) Persist(_cfgExtractorsPosX, _cfgExtractorsPosY, at);
            }
        }

        private void BuildPanels(RectTransform root)
        {
            _build?.Destroy();
            _extractors?.Destroy();
            _buildRows.Clear();
            _extractorRows.Clear();

            _build = HudPanel.Create(root, "Eco manager: build", () => _cfgLocked.Value);
            var heads = _build.Row("Headings", 6f);
            _alloyHead = MakeHead(heads, "alloy", "ALLOY", AlloyColour);
            _energyHead = MakeHead(heads, "energy", "ENERGY", EnergyColour);
            _buildRowsBox = Column(_build.Rect, "Rows");

            _extractors = HudPanel.Create(root, "Eco manager: alloy", () => _cfgLocked.Value);
            var head = _extractors.Row("Heading", 8f, TextAnchor.MiddleCenter);
            var mark = HudCanvas.Icon(head, "alloy", MarkSize + 4f, AlloyColour);
            if (mark == null)
            {
                var label = HudCanvas.Text(head, "Label", 24f, AlloyColour, TextAlignmentOptions.Center, FontStyles.Bold);
                HudCanvas.SetText(label, "ALLOY");
            }
            _extractorStatus = HudCanvas.Text(head, "Status", 18f, new Color(1f, 1f, 1f, 0.6f), TextAlignmentOptions.MidlineLeft);
            _extractorRowsBox = Column(_extractors.Rect, "Rows");
        }

        /// A resource's mark (the game's own icon) in its colour over its
        /// total demand, as wide as the tile column beneath it.
        private static ColumnHead MakeHead(Transform parent, string key, string fallback, Color colour)
        {
            var go = new GameObject(fallback, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var column = go.AddComponent<VerticalLayoutGroup>();
            column.spacing = 0f;
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = PanelTile.Width;
            layout.minWidth = PanelTile.Width;
            var head = new ColumnHead { Go = go };
            if (HudCanvas.Icon(go.transform, key, MarkSize, colour) == null)
            {
                head.Label = HudCanvas.Text(go.transform, "Label", 22f, colour, TextAlignmentOptions.Center, FontStyles.Bold);
                HudCanvas.SetText(head.Label, fallback);
            }
            head.Total = HudCanvas.Text(go.transform, "Total", 24f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            return head;
        }

        private static RectTransform Column(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var column = go.AddComponent<VerticalLayoutGroup>();
            column.spacing = 6f;
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        /// The i-th row of a panel, made on demand: a horizontal group with
        /// its own pool of tiles.
        private static TileRowSlot RowAt(List<TileRowSlot> rows, RectTransform box, int index)
        {
            while (rows.Count <= index)
            {
                var go = new GameObject("Row", typeof(RectTransform));
                go.transform.SetParent(box, false);
                var group = go.AddComponent<HorizontalLayoutGroup>();
                group.spacing = 6f;
                group.childAlignment = TextAnchor.UpperLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                var row = (RectTransform)go.transform;
                rows.Add(new TileRowSlot { Row = row, Pool = new TilePool(row, "Tile") });
            }
            var slot = rows[index];
            if (!slot.Row.gameObject.activeSelf) slot.Row.gameObject.SetActive(true);
            return slot;
        }

        private static void HideRowsFrom(List<TileRowSlot> rows, int index)
        {
            for (var i = index; i < rows.Count; i++)
                if (rows[i].Row != null && rows[i].Row.gameObject.activeSelf) rows[i].Row.gameObject.SetActive(false);
        }

        // ---- the BUILD panel ------------------------------------------------

        private void FillBuild()
        {
            var groups = _buildGroups;

            // A column per resource, each headed by its total, each sorted
            // by its own spend so the top tile is the biggest.
            var byAlloy = groups.OrderByDescending(g => g.AlloyRate).ThenBy(g => g.Tier).ThenBy(g => g.TpId, StringComparer.Ordinal).ToList();
            var byEnergy = groups.OrderByDescending(g => g.EnergyRate).ThenBy(g => g.Tier).ThenBy(g => g.TpId, StringComparer.Ordinal).ToList();
            HudCanvas.SetText(_alloyHead.Total, "-" + Rate(groups.Sum(g => g.AlloyRate)) + "/s");
            HudCanvas.SetText(_energyHead.Total, "-" + Rate(groups.Sum(g => g.EnergyRate)) + "/s");

            for (var i = 0; i < groups.Count; i++)
            {
                var slot = RowAt(_buildRows, _buildRowsBox, i);
                slot.Pool.Begin();
                BuildTile(slot.Pool.Take(), byAlloy[i], false);
                BuildTile(slot.Pool.Take(), byEnergy[i], true);
                slot.Pool.End();
            }
            HideRowsFrom(_buildRows, groups.Count);
        }

        /// A template under construction: art, progress along the top, one
        /// resource's spend in the corner, the count, and the tier. Dimmed
        /// while nobody is working on it or while this panel holds its
        /// builders paused.
        private void BuildTile(PanelTile tile, BuildGroup g, bool energy)
        {
            var paused = TilePaused(g);
            var unattended = g.BuilderIds.Count == 0;
            var rate = g.Rate(energy);

            string title = null, body = null;
            if (_cfgTooltips.Value)
            {
                title = $"{g.Name}  x{g.Count}";
                body = $"alloy -{Rate(g.AlloyRate)}/s · energy -{Rate(g.EnergyRate)}/s\n" +
                       $"{Mathf.RoundToInt(g.Progress * 100f)}% built · {g.BuilderIds.Count} builder{(g.BuilderIds.Count == 1 ? "" : "s")}";
                if (g.Unattended > 0) body += $" · {g.Unattended} unattended";
                if (paused) body += "\nPAUSED from this panel";
                body += "\nclick: select builders";
                if (_cfgRightClickPauses.Value) body += paused ? " · right-click: resume" : " · right-click: pause";
            }

            tile.Set(g.PlateId, g.IconId, g.Name, paused || unattended ? 0.4f : 1f,
                g.Progress, rate > 0.005f ? "-" + Rate(rate) : "0", rate <= 0.005f,
                g.Count, unattended, "T" + g.Tier, paused, title, body);
            tile.OnClick = button =>
            {
                if (button == PointerEventData.InputButton.Left)
                {
                    // Builders when there are any, the ones this panel is
                    // holding paused when that is why there are none, and
                    // otherwise the half-built things themselves, which the
                    // game leaves selectable once nothing is working on them.
                    Select(g.BuilderIds.Count > 0 ? g.BuilderIds : TilePaused(g) ? HeldBuilders(g) : g.TargetIds);
                }
                else if (button == PointerEventData.InputButton.Right && _cfgRightClickPauses.Value)
                {
                    TogglePause(g);
                }
            };
        }

        // ---- the ALLOY panel ------------------------------------------------

        private void FillExtractors()
        {
            List<IdleGroup> tiers, upgrading;
            lock (_groupLock)
            {
                tiers = _alloyGroups;
                upgrading = _alloyUpgradingGroups;
            }
            var status = _pollStatus != "ok" ? _pollStatus : "";
            HudCanvas.SetText(_extractorStatus, status);
            if (_extractorStatus.gameObject.activeSelf != status.Length > 0) _extractorStatus.gameObject.SetActive(status.Length > 0);

            // A row per tier: the ones sitting at that tier on the left, and
            // on the right — only while any are — the ones upgrading away from
            // it, in the same art.
            var row = 0;
            foreach (var tier in tiers)
            {
                var up = upgrading.FirstOrDefault(u => u.Tier == tier.Tier);
                var upCount = up?.Count ?? 0;
                var upIds = up?.UnitIds ?? new List<int>();
                var atTier = tier.Count - upCount;
                var slot = RowAt(_extractorRows, _extractorRowsBox, row++);
                slot.Pool.Begin();
                // A tier whose every extractor is upgrading still gets its row,
                // with the left tile reading 0, so the rows stay in tier order.
                ExtractorTile(slot.Pool.Take(), tier.IconId, tier.Label, atTier, tier.UnitIds.Except(upIds).ToList(),
                    $"{tier.Label} alloy extractors: {atTier}");
                if (upCount > 0)
                {
                    ExtractorTile(slot.Pool.Take(), tier.IconId, "UP", upCount, upIds,
                        $"{tier.Label} alloy extractors upgrading: {upCount}");
                }
                slot.Pool.End();
            }
            HideRowsFrom(_extractorRows, row);
        }

        /// An extractor tile: the tier's art, a tag, and the count. The
        /// upgrading tile wears the same art with UP for its tag; the count
        /// is what tells the two apart at a glance.
        private void ExtractorTile(PanelTile tile, uint icon, string tag, int count, List<int> ids, string tip)
        {
            tile.Set(0, icon, tag, 1f, -1f, null, false, count, false, tag, false,
                _cfgTooltips.Value ? tip : null, _cfgTooltips.Value ? "click: select them" : null);
            tile.OnClick = button =>
            {
                if (button == PointerEventData.InputButton.Left) Select(ids);
            };
        }

        // ---- shared -----------------------------------------------------------------

        private static void Select(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return;
            _pendingSelection = new List<int>(ids);
            _applyOnFrame = -1;
        }

        /// A per-second rate, short enough for a tile corner.
        private static string Rate(float perSecond)
        {
            var v = Mathf.Abs(perSecond);
            if (v < 0.005f) return "0";
            if (v < 10f) return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return Mathf.RoundToInt(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
