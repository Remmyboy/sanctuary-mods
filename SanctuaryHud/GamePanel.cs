using System;
using System.Collections.Generic;
using System.Linq;
using SanctuaryUI;
using TMPro;
using UnityEngine;

namespace SanctuaryHud
{
    // What the HUD borrows from, and does to, the game's own economy panel.
    //
    // Two jobs. Looks: the strip wants to read as part of the game's UI, so it
    // takes the game's typeface and the alloy/energy tints straight off the
    // panel rather than guessing them. Replacement: with the built-in readouts
    // hidden the strip is the only economy display, so the hide has to leave
    // the menu and pause buttons that share the panel alone, and put
    // everything back when the mod unloads.
    //
    // The typed EconomyPanelUI access is isolated in small methods and wrapped
    // in try/catch, so a game update renaming a field costs the feature, not
    // the plugin.
    internal static class GamePanel
    {
        // ---- typeface -------------------------------------------------------

        private static Font _font;
        private static bool _fontTried;

        /// The game's UI font (Rajdhani, via its TextMeshPro asset) as an
        /// IMGUI font, or the closest installed face, or null for Unity's
        /// default. Resolved once, in a match, when the UI assets are loaded.
        internal static Font ResolveFont(BepInEx.Logging.ManualLogSource log)
        {
            if (_fontTried) return _font;
            _fontTried = true;
            try
            {
                _font = FontFromTextMeshPro();
                if (_font != null)
                {
                    log?.LogInfo($"Strip font: game asset '{_font.name}'.");
                    return _font;
                }
            }
            catch (Exception e)
            {
                log?.LogInfo($"Game font lookup failed ({e.Message}); trying installed fonts.");
            }

            // Static TMP atlases ship without their source TTF, so the usual
            // outcome is a fallback. Bahnschrift is on every Windows 10/11
            // and is the same condensed, squared-off cut as Rajdhani.
            try
            {
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "Rajdhani SemiBold", "Rajdhani", "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" })
                {
                    if (!installed.Contains(name)) continue;
                    _font = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (_font != null)
                    {
                        _font.hideFlags = HideFlags.HideAndDontSave;
                        log?.LogInfo($"Strip font: installed '{name}'.");
                        return _font;
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogInfo($"Installed font lookup failed ({e.Message}); using the default.");
            }
            return null;
        }

        private static Font FontFromTextMeshPro()
        {
            Font best = null;
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (asset == null || asset.sourceFontFile == null) continue;
                var name = asset.name ?? "";
                if (name.IndexOf("Rajdhani", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Prefer the semibold cut the menus use for values.
                if (best == null || name.IndexOf("SemiBold", StringComparison.OrdinalIgnoreCase) >= 0) best = asset.sourceFontFile;
            }
            return best;
        }

        // ---- colours --------------------------------------------------------

        /// Alloy and energy tints read off the panel's own progress bars.
        /// Leaves the caller's fallback in place where the image is untinted
        /// (a white sprite carrying the colour, which we can't read cheaply).
        internal static void SampleColours(Component panel, ref Color alloy, ref Color energy)
        {
            try
            {
                if (!(panel is EconomyPanelUI eco)) return;
                var a = BarTint(eco.alloyBar);
                var e = BarTint(eco.energyBar);
                if (a.HasValue) alloy = a.Value;
                if (e.HasValue) energy = e.Value;
            }
            catch { /* colours are cosmetic; the fallback tints stand */ }
        }

        private static Color? BarTint(RadialProgressBarElement bar)
        {
            if (bar == null || bar.images == null) return null;
            foreach (var image in bar.images)
            {
                if (image == null) continue;
                var c = image.color;
                Color.RGBToHSV(c, out _, out var s, out var v);
                if (s < 0.2f || v < 0.3f) continue;
                c.a = 1f;
                return c;
            }
            return null;
        }

        // ---- hiding the built-in readouts -----------------------------------

        private static readonly List<GameObject> _hidden = new List<GameObject>();
        private static Component _appliedTo;

        /// Hides (or restores) the alloy and energy readouts of the game's
        /// panel. Idempotent per panel instance; a new match brings a new
        /// panel, which gets its own pass.
        internal static void SetBuiltInBarsHidden(Component panel, bool hide, BepInEx.Logging.ManualLogSource log)
        {
            if (!hide || panel == null || panel != _appliedTo) Restore();
            if (!hide || panel == null || panel == _appliedTo) return;

            _appliedTo = panel;
            try
            {
                var groups = Groups(panel, out var keep);
                if (groups == null) return;
                foreach (var group in groups) HideGroup(group, keep, panel.transform);
                log?.LogInfo($"Built-in economy readouts hidden ({_hidden.Count} object(s)).");
            }
            catch (Exception e)
            {
                log?.LogWarning($"Could not hide the game's economy readouts: {e.Message}");
            }
        }

        internal static void Restore()
        {
            foreach (var go in _hidden)
            {
                if (go != null) go.SetActive(true);
            }
            _hidden.Clear();
            _appliedTo = null;
        }

        private static List<List<Transform>> Groups(Component panel, out List<Transform> keep)
        {
            keep = null;
            if (!(panel is EconomyPanelUI eco)) return null;
            keep = new Component[] { eco.menuBtn, eco.pauseBtn, eco.versionText, eco.itemContainer }
                .Where(c => c != null).Select(c => c.transform).ToList();
            return new List<List<Transform>>
            {
                Members(eco.alloyBar, eco.alloyNet, eco.alloyStorageMax, eco.alloyStorageValue,
                        eco.alloyGeneratedIncome, eco.alloyExpense, eco.alloyHarvestIncome, eco.alloyHarvestTotal),
                Members(eco.energyBar, eco.energyNet, eco.energyStorageMax, eco.energyStorageValue,
                        eco.energyGeneneratedIncome, eco.energyExpense, eco.energyHarvestIncome, eco.energyHarvestTotal),
            };
        }

        private static List<Transform> Members(params Component[] parts) =>
            parts.Where(p => p != null).Select(p => p.transform).Distinct().ToList();

        // Hide the smallest ancestor holding the whole group (the resource's
        // block, frame and all) unless it would take the menu buttons or the
        // panel root with it, in which case hide the elements one by one and
        // leave the frame.
        private static void HideGroup(List<Transform> members, List<Transform> keep, Transform root)
        {
            if (members.Count == 0) return;

            var lca = members[0];
            while (lca != null && lca != root && !members.All(m => m.IsChildOf(lca))) lca = lca.parent;

            var safe = lca != null && lca != root && !keep.Any(k => k.IsChildOf(lca));
            if (safe)
            {
                Hide(lca.gameObject);
                return;
            }
            foreach (var m in members) Hide(m.gameObject);
        }

        private static void Hide(GameObject go)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            _hidden.Add(go);
        }
    }
}
