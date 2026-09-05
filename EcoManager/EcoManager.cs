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
    // Alloy panel: one clickable row per extractor tier, and — only while any
    // are upgrading — a second block listing those. Clicking a row selects
    // that group through the client's own selection system.
    //
    // Extractors are identified by their strategic icon, which is
    // `structure1_t{1,2,3}_alloy` across all three factions; the tier is read
    // straight off it. Upgrading state is the game's own upgrade adornment
    // (ClientUnit:CheckShowUpgradingAdornment sets it from IsUpgradeQueued),
    // so it lights and clears exactly when the game's own icon does.
    //
    // Rows are labelled by tier, not "extractor", because the Tier-3 Alloy
    // Furnace carries the same strategic icon and nothing on the render entity
    // separates the two — so the T3 row is "tier-3 alloy structures".
    // Assisting an extractor to start its upgrade lives in AssistUpgrade.cs.
    [BepInPlugin("com.sanctuarydb.ecomanager", "Eco Manager", "0.3.0")]
    public partial class EcoManagerPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;

        // Geometry in 1080p-logical pixels (GUI.matrix rescales per resolution).
        private Rect _rect = new Rect(12, 420, 132, 44);

        private void Awake()
        {
            _log ??= Logger;

            _cfgPosX = Config.Bind("Panel", "PosX", 12f, "Alloy panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("Panel", "PosY", 420f, "Alloy panel Y in 1080p-logical pixels.");
            _rect.x = _cfgPosX.Value;
            _rect.y = _cfgPosY.Value;
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
                Logger.LogError($"Eco manager: economy patch failed (the panel will stay hidden): {e}");
            }
            Logger.LogInfo("Eco Manager panel loaded (toggle it from the F8 mod manager).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            // Unloading via the mod manager must also undo the Lua-side hook.
            RemoveAssistHook();
        }

        private void Update()
        {
            SharedTick();
            UpdateAssistUpgrade(Time.unscaledDeltaTime);

            // Persist the panel position once the drag is over.
            if (!Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _rect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _rect.y) > 0.5f))
            {
                _cfgPosX.Value = _rect.x;
                _cfgPosY.Value = _rect.y;
            }
        }

        private void OnGUI()
        {
            // Nothing to manage before the first extractor goes up.
            if (!InMatch || _alloyCount == 0) return;
            EnsureStyles();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;

            _rect.x = Mathf.Clamp(_rect.x, -_rect.width + 40, logicalWidth - 40);
            _rect.y = Mathf.Clamp(_rect.y, 0, Screen.height / scale - 30);
            _rect = GUI.Window(0x5DD, _rect, DrawWindow, GUIContent.none, _stWindow);

            GUI.matrix = previousMatrix;
        }

        private void DrawWindow(int id)
        {
            List<IdleGroup> groups, upgrading;
            lock (_groupLock)
            {
                groups = _alloyGroups;
                upgrading = _alloyUpgradingGroups;
            }

            _stName.normal.textColor = AlloyColour;
            GUI.Label(new Rect(8, 4, 150, 18), "ALLOY", _stName);
            if (_pollStatus != "ok")
            {
                GUI.Label(new Rect(84, 6, 68, 14), _pollStatus, _stSub);
            }

            // One clickable row per tier; clicking selects that group.
            var y = 23f;
            foreach (var group in groups)
            {
                y = DrawRow(group.Label, group.Count, group.UnitIds, y, AlloyColour);
            }

            if (groups.Count > 1)
            {
                GUI.DrawTexture(new Rect(8, y, _rect.width - 16, 1), _texBarBack);
                y += 3;
                y = DrawRow("ALL", _alloyCount, groups.SelectMany(g => g.UnitIds).ToList(), y, AlloyColour);
            }

            // Upgrading block: present only while something is actually
            // upgrading, so the panel stays quiet the rest of the time.
            if (upgrading.Count > 0)
            {
                GUI.DrawTexture(new Rect(8, y + 2, _rect.width - 16, 1), _texBarBack);
                y += 6;
                _stName.normal.textColor = UpgradeColour;
                GUI.Label(new Rect(8, y, 150, 16), "UPGRADING", _stSubHeading);
                y += 17f;

                foreach (var group in upgrading)
                {
                    y = DrawRow(group.Label, group.Count, group.UnitIds, y, UpgradeColour);
                }
                if (upgrading.Count > 1)
                {
                    y = DrawRow("ALL", _alloyUpgradingCount, upgrading.SelectMany(g => g.UnitIds).ToList(), y, UpgradeColour);
                }
            }

            _rect.height = y + 5f;
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private float DrawRow(string label, int count, List<int> ids, float y, Color countColour)
        {
            var row = new Rect(4, y, _rect.width - 8, 18);
            var hover = row.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(row, _texRowHover);

            GUI.Label(new Rect(row.x + 5, row.y, 90, 18), label, _stRowLabel);
            var previous = _stRowCount.normal.textColor;
            _stRowCount.normal.textColor = countColour;
            GUI.Label(new Rect(row.x + 42, row.y, 30, 18), count.ToString(), _stRowCount);
            _stRowCount.normal.textColor = previous;

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover && ids.Count > 0)
            {
                _pendingSelection = new List<int>(ids);
                _applyOnFrame = -1;
                Event.current.Use();
            }
            return y + 19f;
        }
    }
}
