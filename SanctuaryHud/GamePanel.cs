using System;
using System.Collections.Generic;
using System.Linq;
using EM.UI;
using SanctuaryUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // What the HUD borrows from, and does to, the game's own economy panel.
    //
    // Two jobs. Looks: the strip wants to read as part of the game's UI, so it
    // takes the game's typeface and the alloy/energy tints straight off the
    // panel rather than guessing them. Replacement: with the built-in panel
    // hidden the strip is the only economy display, so the buttons that share
    // the panel (menu, pause and the rest) move into the strip, still driving
    // the game's originals, and everything goes back when the mod unloads.
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

        // ---- hiding the built-in panel --------------------------------------

        private static readonly List<GameObject> _hidden = new List<GameObject>();
        private static readonly List<Graphic> _blanked = new List<Graphic>();
        private static Component _appliedTo;

        internal enum ControlKind { Menu, Pause, Help, Other }

        /// One of the hidden panel's clickable controls, as the strip shows it.
        internal sealed class PanelControl
        {
            internal GameObject Target;
            internal ControlKind Kind;
            internal string Label;
            internal Graphic Icon;
        }

        /// The hidden panel's clickable controls, in its own order; empty
        /// while the panel is showing. The strip draws these in its middle.
        internal static readonly List<PanelControl> Controls = new List<PanelControl>();

        /// The hidden panel's version line ("Beta v…"), or null.
        internal static string VersionText;

        /// Hides (or restores) the game's economy panel. Idempotent per panel
        /// instance; a new match brings a new panel, which gets its own pass.
        internal static void SetBuiltInBarsHidden(Component panel, bool hide, BepInEx.Logging.ManualLogSource log)
        {
            if (!hide || panel == null || panel != _appliedTo) Restore();
            if (!hide || panel == null || panel == _appliedTo) return;

            _appliedTo = panel;
            try
            {
                if (!(panel is EconomyPanelUI eco)) return;
                var keep = Keep(eco);
                HideAllBut(panel.transform, keep);
                BlankControls(panel.transform);
                CaptureControls(eco, keep);
                log?.LogInfo($"Built-in economy panel hidden ({_hidden.Count} object(s), {_blanked.Count} graphic(s) blanked); " +
                             $"{Controls.Count} control(s) moved to the strip: {string.Join("; ", Controls.Select(Describe))}.");
                DumpSubtree(panel.transform, 0, log);
            }
            catch (Exception e)
            {
                // Half a hide is worse than none: put the panel back, and stay
                // marked as applied so this doesn't retry every frame.
                Restore();
                _appliedTo = panel;
                log?.LogWarning($"Could not hide the game's economy panel: {e.Message}");
            }
        }

        internal static void Restore()
        {
            foreach (var go in _hidden)
            {
                if (go != null) go.SetActive(true);
            }
            _hidden.Clear();
            foreach (var graphic in _blanked)
            {
                if (graphic != null) graphic.enabled = true;
            }
            _blanked.Clear();
            Controls.Clear();
            VersionText = null;
            _appliedTo = null;
        }

        /// Restore, and remove what the HUD added to the scene. For unload.
        internal static void Shutdown()
        {
            Restore();
            HudCore.DestroyShield();
        }

        // What survives: the panel's controls. The menu and pause buttons the
        // panel names, plus every other selectable in it (the help button is
        // not a field) and the version text.
        private static List<Transform> Keep(EconomyPanelUI eco)
        {
            var keep = new Component[] { eco.menuBtn, eco.pauseBtn, eco.versionText }
                .Where(c => c != null).Select(c => c.transform).ToList();
            foreach (var selectable in eco.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable != null && !keep.Contains(selectable.transform)) keep.Add(selectable.transform);
            }
            return keep;
        }

        // Switch off everything in the panel that holds no control: walk
        // top-down and switch off the highest node on every branch that leads
        // to no kept item. That takes the gauges, their ring art, labels and
        // the frame with them, and leaves the controls with whatever they sit
        // on, which BlankControls then takes out of sight. (Hiding just the
        // readout elements left the empty dials around them, which was the
        // worse look.)
        private static void HideAllBut(Transform node, List<Transform> keep)
        {
            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                if (keep.Any(k => k == child)) continue;
                if (keep.Any(k => k.IsChildOf(child))) HideAllBut(child, keep);
                else Hide(child.gameObject);
            }
        }

        // The controls stay switched on and only stop drawing. The strip
        // clicks them through Unity's event system, which skips inactive
        // objects (and a Button won't press while inactive), so switching them
        // off like the rest would leave the strip's buttons dead. Disabling a
        // graphic takes its art and its raycast target; the button behaviours
        // keep running. Inactive ones too, so nothing the game switches on
        // later shows through.
        private static void BlankControls(Transform root)
        {
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null || !graphic.enabled) continue;
                graphic.enabled = false;
                _blanked.Add(graphic);
            }
        }

        // The strip's buttons: each kept control that a pointer click does
        // something to, in the panel's hierarchy order.
        private static void CaptureControls(EconomyPanelUI eco, List<Transform> keep)
        {
            VersionText = eco.versionText != null ? eco.versionText.text : null;
            foreach (var node in eco.GetComponentsInChildren<Transform>(true))
            {
                if (!keep.Contains(node)) continue;
                if (eco.versionText != null && node == eco.versionText.transform) continue;
                // A clickable inside another kept control is part of that one.
                if (keep.Any(k => k != node && node.IsChildOf(k))) continue;
                if (!node.GetComponents<Component>().Any(c => c is IPointerClickHandler)) continue;

                var kind = eco.menuBtn != null && node == eco.menuBtn.transform ? ControlKind.Menu
                    : eco.pauseBtn != null && node == eco.pauseBtn.transform ? ControlKind.Pause
                    : node.name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0 ? ControlKind.Help
                    : ControlKind.Other;
                Controls.Add(new PanelControl
                {
                    Target = node.gameObject,
                    Kind = kind,
                    Label = kind == ControlKind.Other ? Readable(node.name) : kind.ToString().ToUpperInvariant(),
                    Icon = FindIcon(node),
                });
            }
        }

        private static string Readable(string name)
        {
            var s = (name ?? "").Replace("Button", "").Replace("Btn", "").Trim();
            return s.Length > 0 ? s.ToUpperInvariant() : "?";
        }

        // The symbol a button carries: on this panel a RawImage child named
        // "symbol" (the panel logs pause, help and menu that way); on Beam UI
        // buttons an Image named "Icon". Nothing else is guessed at: a wrong
        // picture reads worse than the strip's own stand-in glyph.
        private static Graphic FindIcon(Transform control)
        {
            foreach (var g in control.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || g.transform == control) continue;
                if (!g.name.Equals("symbol", StringComparison.OrdinalIgnoreCase) &&
                    !g.name.Equals("Icon", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is RawImage raw && raw.texture != null) return raw;
                if (g is Image image && image.sprite != null) return image;
            }
            return null;
        }

        private static string IconName(Graphic icon) =>
            icon is RawImage raw && raw.texture != null ? raw.texture.name :
            icon is Image image && image.sprite != null ? image.sprite.name : null;

        /// One control for the log: what handles its click, any listeners
        /// wired in the scene, and the icon found for it.
        private static string Describe(PanelControl c)
        {
            var handlers = string.Join("+", c.Target.GetComponents<Component>()
                .Where(x => x is IPointerClickHandler).Select(x => x.GetType().Name));
            var button = c.Target.GetComponent<Button>();
            var wired = button == null ? "" : string.Join(",", Enumerable.Range(0, button.onClick.GetPersistentEventCount())
                .Select(i => button.onClick.GetPersistentMethodName(i)));
            return $"{c.Label} '{c.Target.name}' {{{handlers}{(wired.Length > 0 ? " -> " + wired : "")}}} icon {(c.Icon != null ? "'" + IconName(c.Icon) + "'" : "none")}";
        }

        /// The panel's tree, once, into the log: what got hidden and what
        /// stayed, so the next tweak to this rule has something to go on.
        private static void DumpSubtree(Transform node, int depth, BepInEx.Logging.ManualLogSource log)
        {
            if (depth > 8) return;
            var components = string.Join(",", node.GetComponents<Component>()
                .Where(c => c != null).Select(c => c.GetType().Name)
                .Where(n => n != "RectTransform" && n != "CanvasRenderer"));
            log?.LogInfo($"  {new string(' ', depth * 2)}{node.name}{(node.gameObject.activeSelf ? "" : " (hidden)")} {{{components}}}");
            if (!node.gameObject.activeSelf) return;
            for (var i = 0; i < node.childCount; i++) DumpSubtree(node.GetChild(i), depth + 1, log);
        }

        private static void Hide(GameObject go)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            _hidden.Add(go);
        }

        // ---- driving the moved controls ---------------------------------------

        /// Clicks one of the hidden panel's controls the way the mouse would,
        /// so whatever the game hung on it (a listener, a Beam UI button, a
        /// toggle) runs unchanged.
        internal static void Click(PanelControl control, BepInEx.Logging.ManualLogSource log)
        {
            if (control == null || control.Target == null) return;
            try
            {
                var data = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left,
                    clickCount = 1,
                    position = Input.mousePosition,
                };
                ExecuteEvents.ExecuteHierarchy(control.Target, data, ExecuteEvents.pointerClickHandler);
            }
            catch (Exception e)
            {
                log?.LogWarning($"Strip {control.Label} button: the game's control threw ({e.Message}).");
            }
        }

        private static readonly HashSet<int> _unreadableSprites = new HashSet<int>();

        /// Draws a control's icon fitted into rect, tinted by GUI.color, and
        /// read at draw time so a picture the game swaps (pause to play) shows.
        /// False when there is nothing drawable: no icon, or a sprite packed
        /// too tightly for its rectangle in the atlas to be read.
        internal static bool DrawIcon(Rect rect, Graphic icon)
        {
            if (icon == null) return false;
            if (icon is RawImage raw)
            {
                var texture = raw.texture;
                if (texture == null) return false;
                var uv = raw.uvRect;
                return DrawFitted(rect, texture, uv, texture.width * Mathf.Abs(uv.width), texture.height * Mathf.Abs(uv.height));
            }

            var sprite = icon is Image image ? image.overrideSprite : null;
            var tex = sprite == null ? null : sprite.texture;
            if (tex == null || _unreadableSprites.Contains(sprite.GetInstanceID())) return false;
            Rect tr;
            try
            {
                tr = sprite.textureRect;
            }
            catch
            {
                _unreadableSprites.Add(sprite.GetInstanceID());
                return false;
            }
            return DrawFitted(rect, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height),
                tr.width, tr.height);
        }

        private static bool DrawFitted(Rect rect, Texture texture, Rect uv, float width, float height)
        {
            if (width <= 0f || height <= 0f) return false;
            var fit = Mathf.Min(rect.width / width, rect.height / height);
            var w = width * fit;
            var h = height * fit;
            GUI.DrawTextureWithTexCoords(new Rect(rect.center.x - w / 2f, rect.center.y - h / 2f, w, h), texture, uv);
            return true;
        }

        // ---- the game's menus -------------------------------------------------

        /// Whether one of the game's menus is up over the match. Moved into
        /// HudCore, because the mini-map has to step aside for the same ones;
        /// this keeps the strip's call sites reading as they did.
        internal static bool GameMenuOpen() => HudCore.MenuOpen();

        // ---- keeping strip clicks off the battlefield -------------------------

        // The shield itself moved into HudCore, because the mini-map panel
        // needs exactly the same trick; these two keep the strip's call sites
        // reading as they always did.

        /// Stands the shield over rect (strip coordinates) for this frame.
        /// Call from OnGUI; TickShield takes it down once the calls stop.
        internal static void Shield(Rect rect, float scale) => HudCore.Shield(rect, scale);

        /// From Update: down once the strip has stopped drawing its buttons.
        internal static void TickShield() => HudCore.TickShield();
    }
}
