using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Sanctuary.ModApi;

namespace SanctuaryModTemplate
{
    // Your mod. The loader under engine\SanctuaryMods creates this component
    // when the mod starts and destroys it when the mod stops, is switched off,
    // or is replaced by a rebuild. See docs\writing-mods.md.
    //
    // The three rules of a hot-reloadable mod:
    //   1. Everything Awake sets up, OnDestroy takes down (Harmony patches,
    //      objects you created, handlers on the game's static events).
    //   2. Give ModEvents your plugin as the owner, so a reload drops the old
    //      copy's handlers for you.
    //   3. Keep state in fields, not statics: a reload is a new copy of your
    //      assembly, and the old copy's statics stay behind in memory.
    [BepInPlugin("templateauthorid.sanctuarymodtemplate", "SanctuaryModTemplate", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private ConfigEntry<bool> _example;

        private void Awake()
        {
            // Shows on the Mods page as a switch, under this mod's name.
            _example = Config.Bind("General", "ExampleSetting", true, "What this setting does.");

            // A new id per load, so a reload's patches never collide with the
            // copy being torn down.
            _harmony = new Harmony("templateauthorid.sanctuarymodtemplate." + System.Guid.NewGuid().ToString("N"));
            // _harmony.PatchAll(typeof(Plugin).Assembly);

            ModEvents.OnMatchStarting(this, () => Logger.LogInfo("Match starting."));
            ModEvents.OnMatchEnded(this, () => Logger.LogInfo("Match over."));
            Logger.LogInfo("SanctuaryModTemplate loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            ModEvents.Unsubscribe(this);
        }
    }
}
