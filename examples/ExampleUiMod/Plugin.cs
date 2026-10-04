using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Sanctuary.ModApi;
using UnityEngine;

namespace ExampleUiMod
{
    // A UI mod: yours alone, switched on and off from the Mods page at any
    // time, invisible to other players. This one writes which gameplay mods
    // are live in a corner of the screen.
    //
    // The three rules of a hot-reloadable mod:
    //   1. Everything Awake sets up, OnDestroy takes down (Harmony patches,
    //      objects you created, event handlers on the game's static events).
    //   2. Give ModEvents your plugin as the owner, so a reload drops the old
    //      copy's handlers for you.
    //   3. Keep state in fields, not statics: a reload is a new copy of your
    //      assembly, and the old copy's statics stay behind in memory.
    [BepInPlugin("sanctuarymods.example.uimod", "Example UI Mod", "1.0.0")]
    public class ExampleUiPlugin : BaseUnityPlugin
    {
        // Settings bound here show up on the Mods page by themselves: a bool
        // as a switch, a value list as a selector, a range as a slider.
        private ConfigEntry<bool> _show;
        private ConfigEntry<string> _corner;
        private ConfigEntry<int> _fontSize;

        private string _text = "";
        private GUIStyle _style;
        private string _measured;
        private int _measuredFont;
        private Vector2 _size;

        private void Awake()
        {
            _show = Config.Bind("Banner", "Show", true, "Show which gameplay mods are live.");
            _corner = Config.Bind("Banner", "Corner", "Top left",
                new ConfigDescription("Where the banner sits.",
                    new AcceptableValueList<string>("Top left", "Top right", "Bottom left", "Bottom right")));
            _fontSize = Config.Bind("Banner", "FontSize", 16,
                new ConfigDescription("Text size.", new AcceptableValueRange<int>(10, 40)));

            ModEvents.OnSelectionChanged(this, Refresh);
            ModEvents.OnMatchStarting(this, () => Logger.LogInfo($"Match starting. {Describe()}"));
            ModEvents.OnMatchEnded(this, () => Logger.LogInfo("Match over."));
            Refresh();

            var self = Modding.Self(this);
            Logger.LogInfo($"Example UI mod loaded from {self?.Folder ?? "(not under SanctuaryMods)"}.");
        }

        private void OnDestroy()
        {
            // Not strictly needed (the owner is gone, so ModEvents drops the
            // handlers anyway), but it is the habit that keeps reloads clean.
            ModEvents.Unsubscribe(this);
        }

        private void Refresh() => _text = Describe();

        private static string Describe()
        {
            var live = Modding.ActiveGameplayMods;
            return live.Count == 0
                ? "Gameplay mods: none (vanilla)"
                : "Gameplay mods: " + string.Join(", ", live.Select(m => $"{m.Name} {m.Version}"));
        }

        private void OnGUI()
        {
            if (!_show.Value || !(Modding.InLobby || Modding.InReplay)) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label);
            // OnGUI runs several times a frame: the text is measured again
            // only when it or its size has changed.
            if (_measured != _text || _measuredFont != _fontSize.Value)
            {
                _measured = _text;
                _measuredFont = _fontSize.Value;
                _style.fontSize = _measuredFont;
                _size = _style.CalcSize(new GUIContent(_text));
            }
            var size = _size;
            var right = _corner.Value.EndsWith("right");
            var bottom = _corner.Value.StartsWith("Bottom");
            var rect = new Rect(right ? Screen.width - size.x - 12 : 12, bottom ? Screen.height - size.y - 12 : 12, size.x, size.y);
            GUI.Label(rect, _text, _style);
        }
    }
}
