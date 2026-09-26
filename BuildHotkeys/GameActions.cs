using System.Collections.Generic;

namespace SanctuaryHud
{
    /// One of the game's own hotkey actions, as inputActions.lua declares it:
    /// an action group, the action's name inside it, and the keys it ships on.
    internal sealed class GameAction
    {
        internal string Group;
        internal string Name;
        /// The game's defaults in its own format, before inputSystem.lua
        /// expands AnyModifier — which is also the format a remap is typed in.
        internal string Defaults;
        internal string Description;
        /// Where the setting sits, if not in its group's Game section under
        /// the action's own name.
        internal string Section;
        internal string Key;
        /// A setting this one replaces, whose saved value it takes over.
        internal string OldSection;
        internal string OldKey;

        internal GameAction(string group, string name, string defaults, string description,
            string section = null, string key = null, string oldSection = null, string oldKey = null)
        {
            Group = group;
            Name = name;
            Defaults = defaults;
            Description = description;
            Section = section ?? GameActions.Section(group);
            Key = key ?? name;
            OldSection = oldSection;
            OldKey = oldKey;
        }

        internal string Id => Group + "." + Name;
    }

    /// Every game hotkey that can be moved to another key.
    ///
    /// Listed here rather than read from the game so that the settings exist
    /// before a match does — the mod manager shows them from the front menu,
    /// where there is no client VM to ask. The install reads the live table as
    /// well, and anything a later game version adds that is missing from this
    /// list is bound as a setting then, so no action is left out for long.
    ///
    /// Left out on purpose: the mouse (cursor position, clicks, zoom and the
    /// rotate deltas are axes or the pointer itself, not keys), the append
    /// and alt modifiers (they are Shift and Alt themselves, and the order
    /// modes read the raw modifier state regardless), the pause menu, which
    /// PauseMenuKey already moves with escape's closing half kept, and the
    /// stock construction letters bar M. Those find their template by asking
    /// GetHotkeyForTemplate, which the build roles replace, so W, E, S, D, X,
    /// C and R are the roles' keys under another name and Y (mobile AA, a
    /// role on N) finds nothing. M, upgrade, is the one no role claims.
    internal static class GameActions
    {
        /// Groups and actions the remap never touches (see above), applied
        /// as well to the actions the install finds in the live table.
        internal static readonly HashSet<string> SkippedGroups = new HashSet<string> { "MouseControls", "GameMenu" };
        internal static readonly HashSet<string> SkippedActions = new HashSet<string>
        {
            "Orders.ToggleAppend", "Orders.ToggleAlt",
            "Construction.HotkeyW", "Construction.HotkeyE", "Construction.HotkeyS", "Construction.HotkeyD",
            "Construction.HotkeyX", "Construction.HotkeyC", "Construction.HotkeyR", "Construction.HotkeyY",
        };

        /// The two game keys shown with the build keys rather than in a Game
        /// section, since they act on builders: the stock upgrade key, and the
        /// pause toggle, which took over from the mod's own pause key in 0.4.0.
        internal const string UpgradeId = "Construction.HotkeyM";
        internal const string PauseId = "Orders.TogglePause";

        /// The settings section an action group is shown under.
        internal static string Section(string group)
        {
            switch (group)
            {
                case "Orders": return "GameOrders";
                case "Selection": return "GameSelection";
                case "ControlGroups": return "GameControlGroups";
                case "cameraControls": return "GameCamera";
                // The pause menu's key joins these (see the plugin's Awake).
                case "Chat": return "GameInterface";
                case "Construction": return "BuilderKeys";
                case "DebugControls": return "GameOther";
                default: return "Game" + (group.Length > 0 ? char.ToUpperInvariant(group[0]) + group.Substring(1) : "");
            }
        }

        internal static readonly List<GameAction> All = BuildAll();

        private static List<GameAction> BuildAll()
        {
            var all = new List<GameAction>
            {
                new GameAction("Orders", "OrderStop", "H, Shift-H", "Stop: clear the order queue."),
                new GameAction("Orders", "OrderAttack", "A, Shift-A", "Attack."),
                new GameAction("Orders", "OrderAttackMove", "F, Shift-F", "Attack-move."),
                new GameAction("Orders", "OrderRepair", "G, Shift-G", "Repair."),
                new GameAction("Orders", "OrderAssist", "I, Shift-I", "Assist."),
                new GameAction("Orders", "OrderReclaim", "V, Shift-V", "Reclaim."),
                new GameAction("Orders", "OrderCapture", "Z, Shift-Z", "Capture."),
                new GameAction("Orders", "OrderDash", "J, Shift-J", "Dash."),
                // Its setting keeps the name and slot of the mod's old pause key,
                // and so the key saved there: X for anyone who had that.
                new GameAction("Orders", "TogglePause", "P",
                    "Pauses the selected units, and again unpauses them. A build role on the same key goes first; " +
                    "the pause only when that had nothing to build.",
                    section: "BuilderKeys", key: "PauseKey", oldSection: "Toggles", oldKey: "PauseKey"),
                new GameAction("Orders", "ToggleSubmerge", "U", "Submerge or surface the selected submarines."),

                new GameAction("Selection", "SelectCommander", "Ctrl-Comma", "Select the commander."),
                new GameAction("Selection", "SelectAirMobile", "Ctrl-Period", "Select every mobile air unit."),
                new GameAction("Selection", "SelectIdleEngineers", "Ctrl-Slash", "Select the idle mobile engineers."),
                new GameAction("Selection", "FilterAirMobileSelection", "Ctrl-A", "While held, a drag selects only mobile air units."),
                new GameAction("Selection", "FilterEngineerSelection", "Ctrl-E", "While held, a drag selects only mobile engineers."),
            };

            // Select first, as the game orders the digits' own keys: bare digit
            // selects, Ctrl saves, Shift adds. Local rather than a static field:
            // All is initialised before any field declared below it.
            var digits = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 };
            foreach (var d in digits)
                all.Add(new GameAction("ControlGroups", "SelectControlGroup" + d, "Digit" + d,
                    "Select control group " + d + "; press twice quickly to put the camera on it."));
            foreach (var d in digits)
                all.Add(new GameAction("ControlGroups", "SaveControlGroup" + d, "Ctrl-Digit" + d,
                    "Make the selected units control group " + d + "."));
            foreach (var d in digits)
                all.Add(new GameAction("ControlGroups", "AppendControlGroup" + d, "Shift-Digit" + d,
                    "Add the selected units to control group " + d + "."));

            all.AddRange(new[]
            {
                new GameAction("cameraControls", "cameraPanUp", "AnyModifier-UpArrow", "Pan the camera up while held."),
                new GameAction("cameraControls", "cameraPanDown", "AnyModifier-DownArrow", "Pan the camera down while held."),
                new GameAction("cameraControls", "cameraPanLeft", "AnyModifier-LeftArrow", "Pan the camera left while held."),
                new GameAction("cameraControls", "cameraPanRight", "AnyModifier-RightArrow", "Pan the camera right while held."),
                new GameAction("cameraControls", "cameraSetCameraRotation", "AnyModifier-Space", "Rotate the camera with the mouse while held."),
                new GameAction("cameraControls", "cameraSetCameraPan", "AnyModifier-MiddleButton", "Drag the camera with the mouse while held."),
                new GameAction("cameraControls", "cameraResetController", "AnyModifier-Home", "Reset the camera."),
                new GameAction("cameraControls", "cameraToggleFreeCamera", "AnyModifier-End", "Free camera on and off."),

                new GameAction("Chat", "OpenTeamChat", "Enter", "Write a message to your team."),
                new GameAction("Chat", "OpenAllChat", "Shift-Enter", "Write a message to everyone."),

                new GameAction("Construction", "HotkeyM", "M, Shift-M",
                    "Upgrades the selected structure: the game's own upgrade key, which no build role claims.",
                    key: "UpgradeKey"),

                new GameAction("DebugControls", "DestroySelectedUnits", "Delete", "Self-destruct the selected units."),
                new GameAction("DebugControls", "DeleteSelectedUnits", "Ctrl-Delete", "Delete the selected units outright."),
                new GameAction("DebugControls", "SimSpeedUp", "AnyModifier-PageUp", "Game speed up by one."),
                new GameAction("DebugControls", "SimSpeedDown", "AnyModifier-PageDown", "Game speed down by one."),
                new GameAction("DebugControls", "ToggleStrategicIcons", "Ctrl-Shift-I", "Strategic icons on and off."),
                new GameAction("DebugControls", "ToggleProgressBars", "Ctrl-Shift-B", "Progress bars on and off."),
                new GameAction("DebugControls", "ToggleSoundMute", "Ctrl-Alt-M", "Sound on and off."),
                new GameAction("DebugControls", "ToggleUIHUD", "Ctrl-Alt-F2", "The whole HUD on and off."),
                new GameAction("DebugControls", "DebugToggleProfilerUI", "Ctrl-Alt-F1", "The profiler overlay on and off."),
            });
            return all;
        }
    }
}
