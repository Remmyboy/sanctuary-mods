using BepInEx.Configuration;
using SanctuaryUI;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The tier tabs over the build options (T1, T2, …). The game shows one
    // per tier up to the highest the selection can build, greyed where a
    // tier has nothing in it — so a T2 radar with only its upgrade to offer
    // gets a greyed T1 and a T2, and a tier-1 factory a lone T1: a row of
    // tabs with one place to click. With this on, the row is concealed
    // whenever no more than one tab is live; the options underneath are
    // whatever the game selected, as before.
    internal static class TierTabs
    {
        internal static ConfigEntry<bool> HideLone;

        internal static void Bind(ConfigFile config)
        {
            HideLone = config.Bind("SanctuaryUI", "HideLoneTierTab", true,
                "Hide the tier tabs over the build options when only one of them can be clicked: a tier-1 factory's lone T1, " +
                "or a structure whose only option is its own upgrade.");
        }

        private static readonly PanelConceal _conceal = new PanelConceal();

        internal static void Tick(bool hudShowing)
        {
            ConstructionFilterPanelUI panel = null;
            if (hudShowing && InMatch && HideLone.Value && !PanelConceal.Unavailable)
            {
                try
                {
                    var ui = SanctuaryUIManager.Instance;
                    if (ui != null && ui.TryGetPanel(UIPanelType.ConstructionFilter, out var found)) panel = found as ConstructionFilterPanelUI;
                }
                catch { panel = null; }
            }
            _conceal.Apply(panel != null && LiveTabs(panel) <= 1 ? panel : null);
        }

        internal static void Shutdown() => _conceal.Release();

        /// How many of the panel's tabs are on and clickable.
        private static int LiveTabs(ConstructionFilterPanelUI panel)
        {
            var container = panel.itemContainer;
            if (container == null) return 0;
            var live = 0;
            for (var i = 0; i < container.childCount; i++)
            {
                var child = container.GetChild(i);
                if (!child.gameObject.activeInHierarchy || child.localScale.x < 0.5f) continue;
                var toggle = child.GetComponent<Toggle>();
                if (toggle != null && toggle.interactable) live++;
            }
            return live;
        }
    }
}
