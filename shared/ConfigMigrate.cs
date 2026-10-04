using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SanctuaryHud
{
    // Carrying renamed settings over. A line in the .cfg that no Bind claims
    // stays in the file as an orphan (ConfigFile.OrphanedEntries, private),
    // so the old value is still there to read; it moves to the new entry and
    // the old line goes, so it only ever happens once.
    internal static class ConfigMigrate
    {
        private static Dictionary<ConfigDefinition, string> Orphans(ConfigFile config) =>
            AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config, null)
                as Dictionary<ConfigDefinition, string>;

        /// After every Bind: moves each renamed setting's saved value to its
        /// new name. With dropUnclaimed, whatever is still unclaimed (a setting
        /// no version reads any more) goes from the file too.
        internal static void MoveAll(ConfigFile config, IEnumerable<(string oldSection, string oldKey, string section, string key)> renames,
            ManualLogSource log, bool dropUnclaimed)
        {
            try
            {
                var orphans = Orphans(config);
                if (orphans == null || orphans.Count == 0) return;
                var moved = 0;
                var save = config.SaveOnConfigSet;
                config.SaveOnConfigSet = false;
                try
                {
                    foreach (var (oldSection, oldKey, section, key) in renames)
                    {
                        var old = new ConfigDefinition(oldSection, oldKey);
                        if (!orphans.TryGetValue(old, out var text)) continue;
                        orphans.Remove(old);
                        config[new ConfigDefinition(section, key)].SetSerializedValue(text);
                        moved++;
                    }
                }
                finally
                {
                    config.SaveOnConfigSet = save;
                }
                var dropped = 0;
                if (dropUnclaimed)
                {
                    dropped = orphans.Count;
                    orphans.Clear();
                }
                if (moved == 0 && dropped == 0) return;
                config.Save();
                if (moved > 0) log?.LogInfo($"Settings: carried {moved} value(s) over to their renamed settings.");
                if (dropped > 0) log?.LogInfo($"Settings: dropped {dropped} line(s) left over from settings that no longer exist.");
            }
            catch (Exception e)
            {
                log?.LogWarning($"Settings: renamed settings could not be carried over, so they are at their defaults ({e.Message}).");
            }
        }

        /// Binds a setting that used to live under another section or name,
        /// carrying across a value saved there.
        internal static ConfigEntry<T> Rebind<T>(ConfigFile config, string oldSection, string oldKey, string section, string key,
            T defaultValue, ConfigDescription description, ManualLogSource log)
        {
            var entry = config.Bind(section, key, defaultValue, description);
            try
            {
                var orphans = Orphans(config);
                var old = new ConfigDefinition(oldSection, oldKey);
                if (orphans != null && orphans.TryGetValue(old, out var saved))
                {
                    entry.Value = (T)TomlTypeConverter.ConvertToValue(saved, typeof(T));
                    orphans.Remove(old);
                    config.Save();
                    log?.LogInfo($"Settings: moved {oldSection}.{oldKey} = '{saved}' to {section}.{key}.");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"Settings: could not carry {oldSection}.{oldKey} over to {section}.{key} ({e.Message}); it is back at its default.");
            }
            return entry;
        }
    }
}
