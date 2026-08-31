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
    // The idle-engineers panel: one clickable row per tech tier of idle
    // engineers (plus COMMANDER and ALL rows); clicking selects that group.
    // Standalone mod — the ECS poll, Lua selection bridge and styles come from
    // shared\HudCore.cs, compiled into this assembly, so it works with or
    // without the HUD mod loaded.
    [BepInPlugin("com.sanctuarydb.idleengineers", "Idle Engineers", "0.1.0")]
    public class IdleEngineersPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;

        // Geometry in 1080p-logical pixels (GUI.matrix rescales per resolution).
        private Rect _idleRect = new Rect(12, 250, 132, 44);

        private void Awake()
        {
            _log ??= Logger;

            _cfgPosX = Config.Bind("Panel", "PosX", 12f, "Idle panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("Panel", "PosY", 250f, "Idle panel Y in 1080p-logical pixels.");
            _idleRect.x = _cfgPosX.Value;
            _idleRect.y = _cfgPosY.Value;

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
        }

        private void Update()
        {
            SharedTick();

            // Persist the panel position once the drag is over.
            if (!Input.GetMouseButton(0) &&
                (Math.Abs(_cfgPosX.Value - _idleRect.x) > 0.5f || Math.Abs(_cfgPosY.Value - _idleRect.y) > 0.5f))
            {
                _cfgPosX.Value = _idleRect.x;
                _cfgPosY.Value = _idleRect.y;
            }
        }

        private void OnGUI()
        {
            // The panel only exists when there is something to act on.
            if (!InMatch || _idleCount == 0) return;
            EnsureStyles();

            var scale = Screen.height / 1080f;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var logicalWidth = Screen.width / scale;

            _idleRect.x = Mathf.Clamp(_idleRect.x, -_idleRect.width + 40, logicalWidth - 40);
            _idleRect.y = Mathf.Clamp(_idleRect.y, 0, Screen.height / scale - 30);
            _idleRect = GUI.Window(0x5DC, _idleRect, DrawIdleWindow, GUIContent.none, _stWindow);

            GUI.matrix = previousMatrix;
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
    }
}
