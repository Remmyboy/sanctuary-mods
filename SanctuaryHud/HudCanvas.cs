using System;
using SanctuaryUI;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The HUD's uGUI root: one full-screen RectTransform on the game's own
    // HUD canvas, placed just above the group its panels live in. Anything
    // built on it takes the game's UI scale (the manager sets every canvas
    // scaler to screen height over 2160 times the UI Scale setting), its
    // TextMeshPro fonts, and its event system — so a click over a tile here
    // never reaches the map, with no shield, and a hover reaches the game's
    // tooltip panel.
    //
    // Canvas units, then: the game's tiles are 80 across, which is 40 pixels
    // at 1080p with the scale at 1, so a 1080-logical figure from the IMGUI
    // code is doubled here.
    //
    // The root goes with the scene — the manager and its canvas are torn
    // down with the match — so it is built again beside the first panel
    // asked for in the next one.
    internal static class HudCanvas
    {
        private static RectTransform _root;
        private static Transform _holder;
        private static float _retryAt;

        /// The root, or null while there is none.
        internal static RectTransform Root => _root;

        /// An inactive holder under the root: a clone instantiated here has
        /// nothing run on it until it is moved into a live row, so it can be
        /// stripped and configured first.
        internal static Transform Holder => _holder;

        /// The root, built on the canvas the given game panel is on the
        /// first time (and again after a match change destroys it with the
        /// scene). Null when there is nothing to build on yet.
        internal static RectTransform Ensure(SanctuaryPanelUI beside)
        {
            if (_root != null) return _root;
            if (beside == null || Time.realtimeSinceStartup < _retryAt) return null;
            try
            {
                var canvas = beside.GetComponentInParent<Canvas>();
                if (canvas == null) return null;
                var canvasTransform = canvas.rootCanvas.transform;
                // Just above the top-level group the panel sits in: over the
                // game's panels, under whatever the canvas draws later (the
                // tooltip, the menus).
                var top = beside.transform;
                while (top.parent != null && top.parent != canvasTransform) top = top.parent;

                var go = new GameObject("SanctuaryHud", typeof(RectTransform));
                _root = (RectTransform)go.transform;
                _root.SetParent(canvasTransform, false);
                _root.SetSiblingIndex(top.GetSiblingIndex() + 1);
                _root.anchorMin = Vector2.zero;
                _root.anchorMax = Vector2.one;
                _root.offsetMin = Vector2.zero;
                _root.offsetMax = Vector2.zero;
                _root.localScale = Vector3.one;
                go.AddComponent<LayoutElement>().ignoreLayout = true;

                var holder = new GameObject("Holder", typeof(RectTransform));
                holder.SetActive(false);
                holder.transform.SetParent(_root, false);
                _holder = holder.transform;

                _log?.LogInfo($"HUD canvas: root on '{canvas.rootCanvas.name}' after '{top.name}', scale factor {canvas.scaleFactor:0.###}.");
                return _root;
            }
            catch (Exception e)
            {
                _retryAt = Time.realtimeSinceStartup + 5f;
                _log?.LogWarning($"HUD canvas could not be built; trying again in a few seconds ({e.Message}).");
                if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
                _root = null;
                _holder = null;
                return null;
            }
        }

        /// Shows or hides everything on the root at once: the HUD hidden
        /// with its key, or standing aside under the game's menus.
        internal static void SetShowing(bool showing)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != showing) _root.gameObject.SetActive(showing);
        }

        /// From OnDestroy: a hot reload leaves the old assembly in memory, so
        /// the old copy's objects must go with it.
        internal static void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
            _holder = null;
        }

        // ---- where things are -----------------------------------------------

        private static readonly Vector3[] _corners = new Vector3[4];

        /// Where a RectTransform sits, in the root's own units with the
        /// origin at the root's bottom-left corner and y up — the anchored
        /// position a child anchored bottom-left wants, to sit where it does.
        /// False when there is no root or nothing to measure.
        internal static bool LocalRect(Component target, out Rect rect)
        {
            rect = default;
            if (_root == null || target == null || !(target.transform is RectTransform rt)) return false;
            rt.GetWorldCorners(_corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var corner in _corners)
            {
                var p = _root.InverseTransformPoint(corner);
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            var origin = _root.rect.min;
            rect = new Rect(minX - origin.x, minY - origin.y, maxX - minX, maxY - minY);
            return rect.width > 1f && rect.height > 1f;
        }

        /// The root's size in its own units (the screen, in canvas units).
        internal static Vector2 Size => _root != null ? _root.rect.size : Vector2.zero;

        // ---- building blocks ----------------------------------------------------

        /// A plate for a row: the game's panel colour with the accent hairline
        /// along its top, anchored by its bottom-left corner on the root, and
        /// a raycast target so the gaps between tiles don't let a click
        /// through to the map either. The caller adds the layout group; the
        /// hairline is child 0 and stays out of the layout.
        internal static RectTransform Plate(RectTransform root, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            var back = go.AddComponent<Image>();
            back.color = SanctuaryHudPlugin.GamePanelColour;
            back.raycastTarget = true;
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            var line = Fill(rt, "Accent", accent);
            StretchAlongTop(line.rectTransform, 2f);
            line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return rt;
        }

        /// Sizes a RectTransform to what its layout group asks for.
        internal static void FitToContents(RectTransform rt)
        {
            var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        /// A plain rectangle of colour: an Image with no sprite.
        internal static Image Fill(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        /// Anchors a RectTransform to one edge of its parent, stretched along it.
        internal static void StretchAlongTop(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -height);
            rt.offsetMax = new Vector2(0f, 0f);
        }

        internal static void StretchAlongBottom(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, height);
        }
    }
}
