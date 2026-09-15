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
    // The idle panel: one clickable tile per tech tier of idle engineers
    // (plus COM and ALL), then idle factories by type and tier; clicking
    // selects that group. A tile is the unit's own build-menu art with the
    // count in the corner and the tier under it — the same shape as the
    // eco panels, so the two read as one set.
    // Standalone mod — the ECS poll, Lua selection bridge and styles come from
    // shared\HudCore.cs, compiled into this assembly, so it works with or
    // without the HUD mod loaded.
    [BepInPlugin("com.sanctuarydb.idleengineers", "Idle Engineers", "0.4.0")]
    public class IdleEngineersPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _cfgFactories;
        private ConfigEntry<float> _cfgPosX;
        private ConfigEntry<float> _cfgPosY;
        private ConfigEntry<float> _cfgScale;

        // Geometry in 1080p-logical pixels (GUI.matrix rescales per resolution).
        private Rect _idleRect = new Rect(12, 250, 132, 44);

        private static readonly Color IdleColour = new Color(1f, 0.62f, 0.2f);

        private const float Pad = 6f;
        private const float TileW = 54f;
        private const float TileH = 58f;
        private const float TileGap = 3f;
        private const float ArtSize = 48f;

        private static readonly Color DimText = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color TileBack = new Color(0.1f, 0.12f, 0.15f, 0.9f);

        private static bool _tileStylesReady;
        private static GUIStyle _stTileCount, _stTileTag, _stTileFallback;

        private void Awake()
        {
            _log ??= Logger;

            _cfgFactories = Config.Bind("Factories", "Enabled", true,
                "List idle factories under the engineers, by type (land, air, naval) and tier.");
            _cfgPosX = Config.Bind("Panel", "PosX", 12f, "Idle panel X in 1080p-logical pixels.");
            _cfgPosY = Config.Bind("Panel", "PosY", 250f, "Idle panel Y in 1080p-logical pixels.");
            _cfgScale = Config.Bind("Panel", "Scale", 1f,
                new ConfigDescription("Size of the panel, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));
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
            // Under the game's pause menu, a settings screen or the result
            // screen nothing of the game's own HUD shows, so nothing of this
            // should either — as the HUD's strip steps aside.
            if (MenuOpen()) return;
            EnsureStyles();
            EnsureTileStyles();

            // The panel's own scale rides on the screen's: the window is laid
            // out in unscaled units and its position kept in those too.
            var s = Mathf.Clamp(_cfgScale.Value, 0.7f, 1.6f);
            var scale = Screen.height / 1080f * s;
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

            // One column of tiles, so the panel is a tile wide, or as wide as
            // its widest heading.
            // The engineers stack down one column; the factories go along a
            // row under their heading.
            var width = Pad * 2f + TileW;
            if (factories.Count > 0)
            {
                width = Mathf.Max(width, Pad * 2f + factories.Count * TileW + (factories.Count - 1) * TileGap);
                width = Mathf.Max(width, Pad * 2f + 4f + _stSubHeading.CalcSize(new GUIContent("FACTORIES")).x);
            }
            if (_pollStatus != "ok") width = Mathf.Max(width, 152f);
            _idleRect.width = width;

            _stName.normal.textColor = IdleColour;
            GUI.Label(new Rect(Pad + 2f, 4f, width - Pad * 2f, 18f), "IDLE", _stName);
            if (_pollStatus != "ok")
            {
                GUI.Label(new Rect(40, 6, width - 48, 14), _pollStatus, _stSub);
            }

            // The commander on a line of its own (there is only ever one, so
            // its tile needs no count), then the engineers, a tile per tier.
            var y = 23f;
            var commander = groups.FirstOrDefault(g => g.Tier == 0);
            if (commander != null)
            {
                Tile(new Rect(Pad, y, TileW, TileH), commander.PlateId, commander.IconId, commander.Label, -1, commander.UnitIds);
                y += TileH + TileGap;
            }
            foreach (var group in groups.Where(g => g.Tier != 0))
            {
                Tile(new Rect(Pad, y, TileW, TileH), group.PlateId, group.IconId, group.Label, group.Count, group.UnitIds);
                y += TileH + TileGap;
            }

            // Factories: one heading, which selects every idle factory, then a
            // tile per type and tier along a row (the poll sorts them by type
            // then tier).
            if (factories.Count > 0)
            {
                if (groups.Count > 0)
                {
                    GUI.DrawTexture(new Rect(Pad + 2f, y + 2f, width - Pad * 2f - 4f, 1f), _texBarBack);
                    y += 6f;
                }

                y = DrawFactoryHeading(factories.SelectMany(g => g.UnitIds).ToList(), y);
                var x = Pad;
                foreach (var group in factories)
                {
                    Tile(new Rect(x, y, TileW, TileH), group.PlateId, group.IconId, group.Label, group.Count, group.UnitIds);
                    x += TileW + TileGap;
                }
                y += TileH + TileGap;
            }

            _idleRect.height = y + Pad - TileGap;
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        /// One tile: the unit's own build-menu art where the game has it
        /// loaded (else the label, so a tile is never blank), the count in
        /// the bottom-right corner, the tier tag bottom-left. Click selects.
        private static void Tile(Rect rect, uint plate, uint icon, string tag, int count, List<int> ids)
        {
            Fill(rect, TileBack);
            var hover = rect.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(rect, _texRowHover);

            var art = new Rect(rect.x + 3f, rect.y + 7f, ArtSize, ArtSize);
            if (HasSprite(icon))
            {
                // The game's plate first: it carries the colour for where the
                // unit goes (land, water, both), as on its own build strip.
                DrawSprite(art, plate);
                DrawSprite(art, icon);
            }
            else
            {
                GUI.Label(new Rect(art.x, art.y + 4f, art.width, art.height - 8f), tag, _stTileFallback);
            }
            if (count >= 0)
            {
                Shadowed(new Rect(rect.x + 2f, rect.yMax - 17f, rect.width - 4f, 15f), count.ToString(), _stTileCount, Color.white);
            }
            if (HasSprite(icon))
            {
                Shadowed(new Rect(rect.x + 4f, rect.yMax - 14f, 30f, 12f), tag, _stTileTag, DimText);
            }

            var e = Event.current;
            if (hover && e.type == EventType.MouseDown && e.button == 0 && ids.Count > 0)
            {
                _pendingSelection = new List<int>(ids);
                _applyOnFrame = -1;
                e.Use();
            }
        }

        /// "FACTORIES", in the sub-heading style, and clickable like a tile:
        /// it selects every idle factory.
        private float DrawFactoryHeading(List<int> ids, float y)
        {
            var row = new Rect(4, y, _idleRect.width - 8, 16);
            var hover = row.Contains(Event.current.mousePosition);
            if (hover) GUI.DrawTexture(row, _texRowHover);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover && ids.Count > 0)
            {
                _pendingSelection = new List<int>(ids);
                _applyOnFrame = -1;
                Event.current.Use();
            }

            var previous = _stSubHeading.normal.textColor;
            _stSubHeading.normal.textColor = IdleColour;
            GUI.Label(new Rect(row.x + 4, row.y, row.width - 4, 16), "FACTORIES", _stSubHeading);
            _stSubHeading.normal.textColor = previous;
            return y + 17f;
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
            _stTileCount = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight };
            _stTileTag = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
            _stTileFallback = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.8f) } };
        }
    }
}
