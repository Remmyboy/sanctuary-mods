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
    // The idle panel: one clickable row per tech tier of idle engineers (plus
    // COM and ALL rows), then idle factories by type and tier; clicking
    // selects that group. Each row carries the unit's own build-menu art.
    // Standalone mod — the ECS poll, Lua selection bridge and styles come from
    // shared\HudCore.cs, compiled into this assembly, so it works with or
    // without the HUD mod loaded.
    [BepInPlugin("com.sanctuarydb.idleengineers", "Idle Engineers", "0.3.0")]
    public class IdleEngineersPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgFactories;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;

        // Geometry in 1080p-logical pixels (GUI.matrix rescales per resolution).
        private Rect _idleRect = new Rect(12, 250, 132, 44);

        private static readonly Color IdleColour = new Color(1f, 0.62f, 0.2f);

        private void Awake()
        {
            _log ??= Logger;

            _cfgFactories = Config.Bind("Factories", "Enabled", true,
                "List idle factories under the engineers, by type (land, air, naval) and tier.");
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
            // Read every frame, so the switch on the Mods page takes effect
            // at the next poll.
            _trackIdleFactories = _cfgFactories.Value;
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
            var factoriesIdle = _cfgFactories.Value && _idleFactoryCount > 0;
            if (!InMatch || (_idleCount == 0 && !factoriesIdle)) return;
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
            List<IdleGroup> groups, factories;
            lock (_groupLock)
            {
                groups = _idleGroups;
                factories = _idleFactoryGroups;
            }
            // Switched off mid-match: hide at once rather than at the next poll.
            if (!_cfgFactories.Value) factories = new List<IdleGroup>();
            // The poll hands factories over sorted by type and then tier, and
            // GroupBy keeps that order.
            var domains = factories.GroupBy(g => g.Domain).ToList();

            // The panel is only ever as wide as what it draws, the same way it
            // is only ever as tall (below). Engineer and factory rows share one
            // layout, so their art, labels and counts stay in columns.
            var layout = MeasureRows(groups.Concat(factories).ToList(), groups.Count > 1 || domains.Count > 1,
                Math.Max(_idleCount, _idleFactoryCount), _pollStatus != "ok");
            // A factory heading is wider than any row, so it has to be measured
            // too or it would clip against the window edge.
            foreach (var domain in domains)
            {
                layout.Width = Mathf.Max(layout.Width,
                    16f + _stSubHeading.CalcSize(new GUIContent(FactoryHeading(domain.Key))).x);
            }
            _idleRect.width = layout.Width;

            _stName.normal.textColor = IdleColour;
            GUI.Label(new Rect(8, 4, layout.Width - 16, 18), "IDLE", _stName);
            if (_pollStatus != "ok")
            {
                GUI.Label(new Rect(40, 6, layout.Width - 48, 14), _pollStatus, _stSub);
            }

            // One clickable row per tech tier; clicking selects that group,
            // and the ALL row selects every idle engineer.
            var y = 23f;
            foreach (var group in groups)
            {
                // There is only ever one commander, so its row needs no count.
                y = DrawIdleRow(group.Label, group.Tier == 0 ? -1 : group.Count, group.UnitIds, y, group.IconId, layout);
            }

            if (groups.Count > 1)
            {
                GUI.DrawTexture(new Rect(8, y, _idleRect.width - 16, 1), _texBarBack);
                y += 3;
                // The ALL row has no art of its own, but keeps the indent.
                y = DrawIdleRow("ALL", _idleCount, groups.SelectMany(g => g.UnitIds).ToList(), y, 0, layout);
            }

            // Factories: a heading per type, which selects every idle factory
            // of that type, then one row per tier.
            if (factories.Count > 0)
            {
                if (groups.Count > 0)
                {
                    GUI.DrawTexture(new Rect(8, y + 2, _idleRect.width - 16, 1), _texBarBack);
                    y += 6;
                }

                foreach (var domain in domains)
                {
                    y = DrawFactoryHeading(domain.Key, domain.SelectMany(g => g.UnitIds).ToList(), y);
                    foreach (var group in domain)
                    {
                        y = DrawIdleRow(group.Label, group.Count, group.UnitIds, y, group.IconId, layout);
                    }
                }

                // With a single type idle its heading already selects them all.
                if (domains.Count > 1)
                {
                    GUI.DrawTexture(new Rect(8, y, _idleRect.width - 16, 1), _texBarBack);
                    y += 3;
                    y = DrawIdleRow("ALL", _idleFactoryCount, factories.SelectMany(g => g.UnitIds).ToList(), y, 0, layout);
                }
            }

            _idleRect.height = y + 5f;
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        // Rows carry the unit's own build-menu art where the game has it
        // loaded, with the tier still spelled out beside it: the art is what
        // you recognise, the label is what makes the tier certain.
        private float DrawIdleRow(string label, int count, List<int> ids, float y, uint icon, RowLayout layout)
        {
            var row = new Rect(4, y, _idleRect.width - 8, RowHeight);
            SelectableRow(row, ids);

            DrawSprite(new Rect(row.x + 3, row.y + 1, IconSize, IconSize), icon);
            GUI.Label(new Rect(row.x + layout.Indent, row.y, row.width - layout.Indent, RowHeight), label, _stRowLabel);
            if (count >= 0)
            {
                GUI.Label(new Rect(row.x + layout.CountX, row.y, row.width - layout.CountX, RowHeight), count.ToString(), _stRowCount);
            }
            return y + RowHeight + 1f;
        }

        private static string FactoryHeading(int domain)
        {
            var type = domain > 0 && domain < FactoryDomains.Length ? FactoryDomains[domain] : "OTHER";
            return type + " FACTORIES";
        }

        /// "LAND FACTORIES" and so on, in the sub-heading style, and clickable
        /// like a row.
        private float DrawFactoryHeading(int domain, List<int> ids, float y)
        {
            var row = new Rect(4, y, _idleRect.width - 8, 16);
            SelectableRow(row, ids);

            var previous = _stSubHeading.normal.textColor;
            _stSubHeading.normal.textColor = IdleColour;
            GUI.Label(new Rect(row.x + 4, row.y, row.width - 4, 16), FactoryHeading(domain), _stSubHeading);
            _stSubHeading.normal.textColor = previous;
            return y + 17f;
        }

        /// Hover highlight; a click queues the row's units for selection.
        private static void SelectableRow(Rect row, List<int> ids)
        {
            var hover = row.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(row, _texRowHover);

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover && ids.Count > 0)
            {
                _pendingSelection = new List<int>(ids);
                _applyOnFrame = -1;
                Event.current.Use();
            }
        }
    }
}
