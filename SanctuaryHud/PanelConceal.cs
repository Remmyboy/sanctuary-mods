using System.Collections.Generic;
using HarmonyLib;
using SanctuaryUI;
using UnityEngine;

namespace SanctuaryHud
{
    // Standing in for one of the game's HUD panels. The panel is left running
    // — Lua keeps filling it in and flipping it on and off with the selection,
    // and its buttons stay live for synthetic clicks — but its canvas group
    // is held at alpha zero, so nothing of it draws and nothing of it catches
    // the mouse. Lua's own visibility calls go through SanctuaryPanelUI.
    // SetPanelVisibility, which sets alpha back to one; a postfix on that puts
    // it straight back down, so the panel never shows for a frame.
    //
    // Releasing gives the panel back exactly as Lua last asked for it.
    internal sealed class PanelConceal
    {
        private static readonly List<PanelConceal> _all = new List<PanelConceal>();

        /// Set when the hooks the stand-ins need failed to apply: every
        /// stand-in then leaves the game's panels alone, without writing
        /// "off" into the player's settings.
        internal static bool Unavailable;

        private SanctuaryPanelUI _panel;
        private SanctuaryPanelUI _taken;   // the last panel Apply said was new

        internal PanelConceal()
        {
            _all.Add(this);
        }

        /// Conceal this panel (null: conceal nothing). A change of panel — a
        /// new match brings new ones — releases the old one first. Returns
        /// true the first time a given panel is taken, not each time it is
        /// taken again after a release (the overlay toggled back on).
        internal bool Apply(SanctuaryPanelUI panel)
        {
            if (panel != _panel) Release();
            if (panel == null) return false;
            var first = panel != _taken;
            _taken = panel;
            _panel = panel;
            Hide(panel);
            return first;
        }

        internal void Release()
        {
            var panel = _panel;
            _panel = null;
            if (panel == null) return;   // Unity null too: destroyed with the scene
            try
            {
                var group = panel.canvasGroup;
                if (group == null) return;
                var visible = panel.IsVisible;
                group.alpha = visible ? 1f : 0f;
                group.blocksRaycasts = visible;
            }
            catch { /* the panel is on its way out */ }
        }

        private static void Hide(SanctuaryPanelUI panel)
        {
            var group = panel.canvasGroup;
            if (group == null) return;
            if (group.alpha != 0f) group.alpha = 0f;
            if (group.blocksRaycasts) group.blocksRaycasts = false;
        }

        // ---- the game's own visibility calls ----------------------------------

        internal static void ApplyPatch(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(SanctuaryPanelUI), nameof(SanctuaryPanelUI.SetPanelVisibility));
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(PanelConceal), nameof(VisibilityPostfix)));
        }

        private static void VisibilityPostfix(SanctuaryPanelUI __instance)
        {
            if (__instance == null) return;
            foreach (var conceal in _all)
            {
                if (conceal._panel != null && conceal._panel == __instance) Hide(__instance);
            }
        }

        /// The panel's tree, once, into the log: what it holds and where,
        /// so the next tweak to a stand-in has something to go on.
        internal static void DumpSubtree(Transform node, int depth, BepInEx.Logging.ManualLogSource log, int maxDepth = 5)
        {
            if (node == null || depth > maxDepth) return;
            var components = string.Join(",", System.Linq.Enumerable.Where(
                System.Linq.Enumerable.Select(node.GetComponents<Component>(), c => c == null ? null : c.GetType().Name),
                n => n != null && n != "RectTransform" && n != "CanvasRenderer"));
            var size = node is RectTransform rt ? $" [{rt.rect.width:F0}x{rt.rect.height:F0} @ {rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0}]" : "";
            log?.LogInfo($"  {new string(' ', depth * 2)}{node.name}{(node.gameObject.activeSelf ? "" : " (inactive)")}{size} {{{components}}}");
            for (var i = 0; i < node.childCount; i++) DumpSubtree(node.GetChild(i), depth + 1, log, maxDepth);
        }
    }
}
