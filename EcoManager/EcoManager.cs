using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
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
    // be dropped or shrunk without the other.
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

        // Geometry in 1080p-logical pixels, before each panel's own scale
        // (GUI.matrix rescales per resolution and per panel).
        private Rect _buildRect = new Rect(12, 560, 132, 44);
        private Rect _extractorRect = new Rect(12, 420, 132, 44);

        private const int BuildWindowId = 0x5DD;
        private const int ExtractorWindowId = 0x5DF;
        private const int TipWindowId = 0x5DE;
        private const float Pad = 6f;
        private const float TileW = 54f;
        private const float TileH = 58f;
        private const float TileGap = 3f;
        private const float ArtSize = 48f;
        private const float TipWidth = 200f;

        private static readonly Color ProgressColour = new Color(1f, 0.8f, 0.2f, 0.95f);
        private static readonly Color DimText = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color TileBack = new Color(0.1f, 0.12f, 0.15f, 0.9f);

        private static bool _tileStylesReady;
        private static GUIStyle _stTileRate, _stTileCount, _stTileTag, _stColumn, _stTotal, _stTip;

        // The hovered tile's detail line, drawn in a window beside the panel
        // that owns it: a GUI.Window clips to its own rect, so a tooltip
        // cannot hang out of the panel's edge from inside it.
        private string _tip;
        private float _tipY;
        private int _tipOwner;

        private void Awake()
        {
            _log ??= Logger;

            _cfgTooltips = Config.Bind("Panel", "Tooltips", true,
                "Show a detail box beside a panel while a tile is hovered: name, both rates, progress, " +
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

            _buildRect.x = _cfgBuildPosX.Value;
            _buildRect.y = _cfgBuildPosY.Value;
            _extractorRect.x = _cfgExtractorsPosX.Value;
            _extractorRect.y = _cfgExtractorsPosY.Value;
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
            DestroyShield();
        }

        private void Update()
        {
            SharedTick();
            UpdateAssistUpgrade(Time.unscaledDeltaTime);
            UpdateConstruction(Time.unscaledDeltaTime);
            TickShield();

            // Persist the panel positions once the drag is over.
            if (!Input.GetMouseButton(0))
            {
                Persist(_cfgBuildPosX, _cfgBuildPosY, _buildRect);
                Persist(_cfgExtractorsPosX, _cfgExtractorsPosY, _extractorRect);
            }
        }

        private static void Persist(ConfigEntry<float> x, ConfigEntry<float> y, Rect rect)
        {
            if (Math.Abs(x.Value - rect.x) > 0.5f || Math.Abs(y.Value - rect.y) > 0.5f)
            {
                x.Value = rect.x;
                y.Value = rect.y;
            }
        }

        private void OnGUI()
        {
            if (!InMatch) return;
            // Under the game's pause menu, the Mods page (F8), a settings
            // screen or the result screen nothing of the game's own HUD
            // shows, so nothing of this should either.
            if (MenuOpen()) return;
            EnsureStyles();
            EnsureTileStyles();

            // Nothing to manage before the first thing goes up; and a build
            // panel means nothing in a replay's all-armies view.
            if (_cfgBuildEnabled.Value && _buildGroups.Count > 0 && OwnArmyFocused)
                DrawPanel(ref _buildRect, _cfgBuildScale.Value, BuildWindowId, DrawBuildWindow);
            if (_cfgExtractorsEnabled.Value && _alloyCount > 0)
                DrawPanel(ref _extractorRect, _cfgExtractorsScale.Value, ExtractorWindowId, DrawExtractorWindow);
        }

        /// One panel: its own scale on top of the resolution scale, kept on
        /// screen, shielded from the map, and its tooltip beside it. The
        /// rect is in 1080p-logical pixels; the window works in the panel's
        /// own scaled units, so its position is divided out and back.
        private void DrawPanel(ref Rect rect, float panelScale, int id, GUI.WindowFunction draw)
        {
            panelScale = Mathf.Clamp(panelScale, 0.5f, 2.5f);
            var scale = Screen.height / 1080f * panelScale;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var width = Screen.width / scale;
            var height = Screen.height / scale;

            var local = new Rect(rect.x / panelScale, rect.y / panelScale, rect.width, rect.height);
            // Kept wholly on screen, whatever the resolution or the panel's size.
            local.x = Mathf.Clamp(local.x, 0, Mathf.Max(0f, width - local.width));
            local.y = Mathf.Clamp(local.y, 0, Mathf.Max(0f, height - local.height));
            local = GUI.Window(id, local, draw, GUIContent.none, _stWindow);
            // A right-click that also reached the map would order the
            // selection somewhere; the shield keeps every click on the panel.
            Shield(local, scale);
            rect = new Rect(local.x * panelScale, local.y * panelScale, local.width, local.height);

            if (_tip != null && _tipOwner == id)
            {
                var tipHeight = _stTip.CalcHeight(new GUIContent(_tip), TipWidth - 12f) + 10f;
                // Beside the panel, level with the tile; the other side when
                // the panel sits against the right edge.
                var x = local.xMax + 4f;
                if (x + TipWidth > width) x = local.x - TipWidth - 4f;
                var y = Mathf.Clamp(local.y + _tipY, 0f, height - tipHeight);
                GUI.Window(TipWindowId, new Rect(x, y, TipWidth, tipHeight), DrawTip, GUIContent.none, _stWindow);
                GUI.BringWindowToFront(TipWindowId);
            }

            GUI.matrix = previousMatrix;
        }

        private void DrawTip(int id)
        {
            // Window functions run after the rest of OnGUI, so the text can
            // have been cleared since this window was asked for.
            GUI.Label(new Rect(6f, 5f, TipWidth - 12f, 400f), _tip ?? "", _stTip);
        }

        // ---- the BUILD panel ------------------------------------------------

        private void DrawBuildWindow(int id)
        {
            var groups = _buildGroups;
            var width = Pad * 2f + 2f * TileW + TileGap;
            _buildRect.width = width;
            ClearTip(id);

            // A column per resource, each headed by its name and its total,
            // each sorted by its own spend so the top tile is the biggest.
            var byAlloy = groups.OrderByDescending(g => g.AlloyRate).ThenBy(g => g.Tier).ThenBy(g => g.TpId, StringComparer.Ordinal).ToList();
            var byEnergy = groups.OrderByDescending(g => g.EnergyRate).ThenBy(g => g.Tier).ThenBy(g => g.TpId, StringComparer.Ordinal).ToList();

            ColumnHeader(new Rect(Pad, 3f, TileW, 16f), "ALLOY", AlloyColour, groups.Sum(g => g.AlloyRate));
            ColumnHeader(new Rect(Pad + TileW + TileGap, 3f, TileW, 16f), "ENERGY", EnergyColour, groups.Sum(g => g.EnergyRate));

            var top = 34f;
            for (var i = 0; i < byAlloy.Count; i++)
                DrawBuildTile(new Rect(Pad, top + i * (TileH + TileGap), TileW, TileH), byAlloy[i], false, id);
            for (var i = 0; i < byEnergy.Count; i++)
                DrawBuildTile(new Rect(Pad + TileW + TileGap, top + i * (TileH + TileGap), TileW, TileH), byEnergy[i], true, id);

            _buildRect.height = top + groups.Count * (TileH + TileGap) - TileGap + Pad;
            if (!_cfgLocked.Value) GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        /// The resource's name in its colour, its total demand in white below.
        private static void ColumnHeader(Rect rect, string label, Color colour, float total)
        {
            // The resource's mark, large, in place of its name — the same
            // ingot and bolt as the HUD's strip and card.
            var mark = Glyphs.Get(label == "ALLOY" ? "alloy" : "energy");
            if (mark != null)
            {
                var previousColour = GUI.color;
                GUI.color = colour;
                GUI.DrawTexture(new Rect(rect.center.x - 9f, rect.y - 1f, 18f, 18f), mark);
                GUI.color = previousColour;
            }
            else
            {
                var previous = _stColumn.normal.textColor;
                _stColumn.normal.textColor = colour;
                GUI.Label(rect, label, _stColumn);
                _stColumn.normal.textColor = previous;
            }
            GUI.Label(new Rect(rect.x, rect.y + 13f, rect.width, 16f), "-" + Rate(total) + "/s", _stTotal);
        }

        /// A template under construction: art, progress along the top, one
        /// resource's spend in the corner, the count, and the tier. Dimmed
        /// while nobody is working on it or while this panel holds its
        /// builders paused.
        private void DrawBuildTile(Rect rect, BuildGroup g, bool energy, int owner)
        {
            var hover = Frame(rect);
            var paused = TilePaused(g);
            var unattended = g.BuilderIds.Count == 0;

            Art(rect, g.PlateId, g.IconId, g.Name, paused || unattended ? 0.4f : 1f);

            // Progress bar along the top edge, as FA draws it.
            var bar = new Rect(rect.x + 3f, rect.y + 2f, ArtSize, 3f);
            GUI.DrawTexture(bar, _texBarBack);
            if (g.Progress > 0f) Fill(new Rect(bar.x, bar.y, bar.width * g.Progress, bar.height), ProgressColour);

            var rate = g.Rate(energy);
            Shadowed(new Rect(rect.x + 2f, rect.y + 6f, rect.width - 4f, 14f),
                rate > 0.005f ? "-" + Rate(rate) : "0", _stTileRate, rate > 0.005f ? Color.white : DimText);
            Shadowed(new Rect(rect.x + 2f, rect.yMax - 17f, rect.width - 4f, 15f), g.Count.ToString(), _stTileCount,
                unattended ? DimText : Color.white);
            Shadowed(new Rect(rect.x + 4f, rect.yMax - 14f, 24f, 12f), "T" + g.Tier, _stTileTag, DimText);

            if (paused)
            {
                Fill(new Rect(rect.center.x - 7f, rect.center.y - 8f, 5f, 16f), new Color(1f, 1f, 1f, 0.9f));
                Fill(new Rect(rect.center.x + 2f, rect.center.y - 8f, 5f, 16f), new Color(1f, 1f, 1f, 0.9f));
            }

            if (!hover) return;

            var tip = $"{g.Name}  x{g.Count}\nalloy -{Rate(g.AlloyRate)}/s · energy -{Rate(g.EnergyRate)}/s\n" +
                      $"{Mathf.RoundToInt(g.Progress * 100f)}% built · {g.BuilderIds.Count} builder{(g.BuilderIds.Count == 1 ? "" : "s")}";
            if (g.Unattended > 0) tip += $" · {g.Unattended} unattended";
            if (paused) tip += "\nPAUSED from this panel";
            tip += "\nclick: select builders";
            if (_cfgRightClickPauses.Value) tip += paused ? " · right-click: resume" : " · right-click: pause";
            SetTip(owner, tip, rect);

            var e = Event.current;
            if (e.type != EventType.MouseDown) return;
            if (e.button == 0)
            {
                // Builders when there are any, the ones this panel is holding
                // paused when that is why there are none, and otherwise the
                // half-built things themselves, which the game leaves
                // selectable once nothing is working on them.
                Select(g.BuilderIds.Count > 0 ? g.BuilderIds : paused ? HeldBuilders(g) : g.TargetIds);
                e.Use();
            }
            else if (e.button == 1 && _cfgRightClickPauses.Value)
            {
                TogglePause(g);
                e.Use();
            }
        }

        // ---- the ALLOY panel ------------------------------------------------

        private void DrawExtractorWindow(int id)
        {
            List<IdleGroup> tiers, upgrading;
            lock (_groupLock)
            {
                tiers = _alloyGroups;
                upgrading = _alloyUpgradingGroups;
            }
            // A row per tier: the ones sitting at that tier on the left, and
            // on the right — only while any are — the ones upgrading away from
            // it, in the same art. The right column exists only while
            // something is upgrading, so the panel is one tile wide otherwise.
            var anyUpgrading = _alloyUpgradingCount > 0;
            var columns = anyUpgrading ? 2 : 1;
            var width = Pad * 2f + columns * TileW + (columns - 1) * TileGap;
            _extractorRect.width = width;
            ClearTip(id);

            _stName.normal.textColor = AlloyColour;
            var alloyMark = Glyphs.Get("alloy");
            if (alloyMark != null)
            {
                var previousColour = GUI.color;
                GUI.color = AlloyColour;
                GUI.DrawTexture(new Rect(width / 2f - 10f, 2f, 20f, 20f), alloyMark);
                GUI.color = previousColour;
            }
            else
            {
                GUI.Label(new Rect(Pad + 2f, 3f, width - Pad * 2f, 18f), "ALLOY", _stName);
            }
            if (_pollStatus != "ok")
            {
                GUI.Label(new Rect(width - Pad - 100f, 5f, 100f, 14f), _pollStatus, _stSub);
            }

            var top = 23f;
            var row = 0;
            foreach (var tier in tiers)
            {
                var up = upgrading.FirstOrDefault(u => u.Tier == tier.Tier);
                var upCount = up?.Count ?? 0;
                var upIds = up?.UnitIds ?? new List<int>();
                var atTier = tier.Count - upCount;
                // A tier whose every extractor is upgrading still gets its row,
                // with the left tile reading 0, so the rows stay in tier order.
                var y = top + row * (TileH + TileGap);
                ExtractorTile(new Rect(Pad, y, TileW, TileH), tier.IconId, tier.Label, atTier, tier.UnitIds.Except(upIds).ToList(),
                    $"{tier.Label} alloy extractors: {atTier}\nclick: select them", id);
                if (upCount > 0)
                {
                    ExtractorTile(new Rect(Pad + TileW + TileGap, y, TileW, TileH), tier.IconId, "UP", upCount, upIds,
                        $"{tier.Label} alloy extractors upgrading: {upCount}\nclick: select them", id);
                }
                row++;
            }

            _extractorRect.height = top + row * (TileH + TileGap) - TileGap + Pad;
            if (!_cfgLocked.Value) GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        /// An extractor tile: the tier's art, a tag, and the count. The
        /// upgrading tile wears the same art with UP for its tag; the count
        /// is what tells the two apart at a glance.
        private void ExtractorTile(Rect rect, uint icon, string tag, int count, List<int> ids, string tip, int owner)
        {
            var hover = Frame(rect);
            Art(rect, 0, icon, tag, 1f);
            Shadowed(new Rect(rect.x + 2f, rect.yMax - 17f, rect.width - 4f, 15f), count.ToString(), _stTileCount, Color.white);
            Shadowed(new Rect(rect.x + 4f, rect.yMax - 14f, 24f, 12f), tag, _stTileTag, DimText);

            if (!hover) return;
            SetTip(owner, tip, rect);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0) { Select(ids); e.Use(); }
        }

        // ---- shared tile drawing ---------------------------------------------

        /// The tile's plate and hover highlight. Returns whether it is hovered.
        private static bool Frame(Rect rect)
        {
            Fill(rect, TileBack);
            var hover = rect.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(rect, _texRowHover);
            return hover;
        }

        /// The build-menu art (with its plate) where the game has it loaded,
        /// else the name, so a tile is never blank.
        private static void Art(Rect rect, uint plate, uint icon, string fallback, float alpha)
        {
            var art = new Rect(rect.x + 3f, rect.y + 7f, ArtSize, ArtSize);
            if (HasSprite(icon))
            {
                var previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                DrawSprite(art, plate);
                DrawSprite(art, icon);
                GUI.color = previous;
            }
            else
            {
                var previous = _stTip.normal.textColor;
                _stTip.normal.textColor = new Color(1f, 1f, 1f, 0.7f * alpha);
                GUI.Label(new Rect(art.x, art.y + 8f, art.width, art.height - 8f), fallback, _stTip);
                _stTip.normal.textColor = previous;
            }
        }

        /// Re-decided every pass by the panel that owns the tip; a tile sets
        /// it again while hovered.
        private void ClearTip(int owner)
        {
            if (_tipOwner == owner) _tip = null;
        }

        private void SetTip(int owner, string text, Rect tile)
        {
            if (!_cfgTooltips.Value) return;
            _tip = text;
            _tipY = tile.y;
            _tipOwner = owner;
        }

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

        private static void Fill(Rect rect, Color colour)
        {
            var previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _texWhite);
            GUI.color = previous;
        }

        /// Text over art needs an edge to read against.
        private static void Shadowed(Rect rect, string text, GUIStyle style, Color colour)
        {
            var previous = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            style.normal.textColor = colour;
            GUI.Label(rect, text, style);
            style.normal.textColor = previous;
        }

        private static void EnsureTileStyles()
        {
            if (_tileStylesReady) return;
            _tileStylesReady = true;
            _stTileRate = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
            _stTileCount = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight };
            _stTileTag = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
            _stColumn = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _stTotal = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _stTip = new GUIStyle { fontSize = 11, wordWrap = true, alignment = TextAnchor.UpperLeft, normal = { textColor = new Color(1f, 1f, 1f, 0.85f) } };
        }
    }
}
