using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // What the last build hotkey picked and the rest of that key's cycle, as
    // a strip of the build menu's own art on the HUD canvas: the live entry
    // lit on a highlight that slides along as you press again, the others
    // faded, left to right in the order further presses reach them. Shown
    // only, never clicked: it catches no clicks, so it cannot take one meant
    // for the battlefield underneath.
    //
    // Laid out by hand in canvas units from the 1080-logical settings (icon
    // size, distance from the top), centred across the screen.
    internal sealed class CycleStrip
    {
        /// How much of the next entry leans into view past the band's edge.
        private const float PeekFraction = 0.45f;
        private const float FadeIn = 0.15f, FadeOut = 0.35f, Rise = 10f;

        private RectTransform _plate, _viewport, _selector;
        private CanvasGroup _group;
        private TMP_Text _tier, _caption;
        private readonly List<Cell> _cells = new List<Cell>();
        private float _selectorX = float.NaN;
        /// When the strip last came up from hidden: it animates in once, and
        /// presses while it is up only move the highlight.
        private float _appearedAt = -1f;
        private bool _logged;

        private sealed class Cell
        {
            public RectTransform Rect;
            public Image Back, Icon;
        }

        /// From Update: shows the strip for a cycle the key just stepped
        /// through, age seconds ago, or hides it.
        internal void Sync(bool show, string[] names, uint[] icons, uint[] backs, int[] tiers, int index,
            float age, float seconds, float iconLogical, int cap, bool captioned, float topLogical)
        {
            try
            {
                show = show && names != null && names.Length > 0 && age <= seconds;
                if (!show)
                {
                    if (_plate != null && _plate.gameObject.activeSelf) _plate.gameObject.SetActive(false);
                    _selectorX = float.NaN;
                    _appearedAt = -1f;
                    return;
                }
                var root = HudCanvas.Ensure();
                if (root == null) return;
                if (_plate == null) Build(root);
                if (_appearedAt < 0f) _appearedAt = Time.unscaledTime;
                Layout(names, icons, backs, tiers, index, age, seconds, iconLogical, cap, captioned, topLogical);
                if (!_plate.gameObject.activeSelf) _plate.gameObject.SetActive(true);
            }
            catch (System.Exception e)
            {
                if (_logged) return;
                _logged = true;
                _log?.LogWarning($"Hotkey cycle strip could not be laid out (logged once): {e}");
            }
        }

        internal void Destroy()
        {
            if (_plate != null) Object.Destroy(_plate.gameObject);
            _plate = null;
            _cells.Clear();
        }

        private void Build(RectTransform root)
        {
            // The last match's cells went with its canvas.
            _cells.Clear();
            _selectorX = float.NaN;
            _logged = false;
            _plate = HudCanvas.Plate(root, "Hotkey cycle");
            _plate.anchorMin = _plate.anchorMax = new Vector2(0.5f, 1f);
            _plate.pivot = new Vector2(0.5f, 1f);
            // Shown, never clicked.
            _plate.GetComponent<Image>().raycastTarget = false;
            _group = _plate.gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _tier = HudCanvas.Text(_plate, "Tier", 24f, new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Center, FontStyles.Bold);
            _caption = HudCanvas.Text(_plate, "Caption", 26f, Color.white, TextAlignmentOptions.Center);
            foreach (var t in new[] { _tier, _caption })
            {
                var rt = t.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            }

            var view = new GameObject("Cells", typeof(RectTransform));
            view.transform.SetParent(_plate, false);
            view.AddComponent<RectMask2D>();
            _viewport = (RectTransform)view.transform;
            _viewport.anchorMin = _viewport.anchorMax = _viewport.pivot = new Vector2(0f, 1f);

            // The highlight under the live entry: a lit plate with a bright
            // line along its foot and a glow round it, drawn under the art.
            var sel = new GameObject("Selected", typeof(RectTransform));
            sel.transform.SetParent(_viewport, false);
            _selector = (RectTransform)sel.transform;
            _selector.anchorMin = _selector.anchorMax = _selector.pivot = new Vector2(0f, 1f);
            var lit = sel.AddComponent<Image>();
            lit.sprite = HudStyle.Shade;
            lit.type = Image.Type.Sliced;
            var accent = AccentColour;
            accent.a = 0.35f;
            lit.color = accent;
            lit.raycastTarget = false;
            var line = HudCanvas.Fill(_selector, "Line", AccentColour);
            HudCanvas.StretchAlongBottom(line.rectTransform, 4f);
            var edge = HudCanvas.Fill(_selector, "Edge", new Color(AccentColour.r, AccentColour.g, AccentColour.b, 0.8f));
            edge.sprite = HudStyle.Frame;
            edge.type = Image.Type.Sliced;
            var ert = edge.rectTransform;
            ert.anchorMin = Vector2.zero;
            ert.anchorMax = Vector2.one;
            ert.offsetMin = Vector2.zero;
            ert.offsetMax = Vector2.zero;
        }

        private Cell CellAt(int i)
        {
            while (_cells.Count <= i)
            {
                var go = new GameObject("Cell", typeof(RectTransform));
                go.transform.SetParent(_viewport, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                var back = HudCanvas.Fill(rt, "Plate", Color.white);
                var icon = HudCanvas.Fill(rt, "Icon", Color.white);
                // The plate fills its cell, which takes the plate's shape;
                // the art keeps its own proportions inside it, as on the
                // game's build buttons.
                icon.preserveAspect = true;
                foreach (var image in new[] { back, icon })
                {
                    var irt = image.rectTransform;
                    irt.anchorMin = Vector2.zero;
                    irt.anchorMax = Vector2.one;
                }
                _cells.Add(new Cell { Rect = rt, Back = back, Icon = icon });
            }
            return _cells[i];
        }

        private void Layout(string[] names, uint[] icons, uint[] backs, int[] tiers, int index,
            float age, float seconds, float iconLogical, int cap, bool captioned, float topLogical)
        {
            var u = HudCanvas.UnitsPerLogical;
            var icon = Mathf.Clamp(iconLogical, 16f, 256f) * u;
            float cellPad = 6f * u, padX = 10f * u, padY = 8f * u, chipW = 28f * u, captionH = 24f * u;

            var total = names.Length;
            var live = Mathf.Clamp(index - 1, 0, total - 1);

            // A short cycle shows whole. One that outgrows the cap shows the
            // run of entries sharing the live one's tier (the list is sorted
            // by tier), cut to the cap around the live one: a T3 engineer's
            // factory key is nine entries, too many for one strip.
            cap = Mathf.Max(1, cap);
            int first = 0, last = total - 1;
            if (total > cap)
            {
                first = last = live;
                if (tiers != null && tiers.Length == total)
                {
                    var tier = tiers[live];
                    while (first > 0 && tiers[first - 1] == tier) first--;
                    while (last < total - 1 && tiers[last + 1] == tier) last++;
                }
                if (last - first + 1 > cap)
                {
                    first = Mathf.Clamp(live - cap / 2, first, last - cap + 1);
                    last = first + cap - 1;
                }
            }
            var shown = last - first + 1;
            // The next entry runs off the edge when there is one: half an
            // icon says "there is more" without a number to read, and no peek
            // on the last band says the cycle ends there.
            var peek = last < total - 1;
            // The tier earns a column only while banding hides the rest.
            var banded = shown < total && tiers != null && tiers.Length == total;
            var chip = banded ? chipW : 0f;

            // Each cell is the shape of the build menu's plate (taller than
            // wide), at the icon size tall, so the plate goes round the art.
            var aspect = 1f;
            for (var i = first; i <= last && backs != null && i < backs.Length; i++)
            {
                var plate = backs[i] != 0u ? SpriteFor(backs[i]) : null;
                if (plate == null || plate.rect.height < 1f) continue;
                aspect = Mathf.Clamp(plate.rect.width / plate.rect.height, 0.5f, 1.5f);
                break;
            }
            var cellW = icon * aspect + cellPad * 2f;
            var cellH = icon + cellPad * 2f;
            var viewW = cellW * shown + (peek ? cellW * PeekFraction : 0f);
            var width = padX * 2f + chip + viewW;
            var height = padY * 2f + cellH + (captioned ? captionH : 0f);

            // In fast, settling up into place, once when it comes up; out
            // over the last moments after the last press.
            var inT = Mathf.Clamp01((Time.unscaledTime - _appearedAt) / FadeIn);
            var outT = Mathf.Clamp01((seconds - age) / FadeOut);
            _group.alpha = inT * outT;
            var rise = (1f - HudStyle.Overshoot(inT)) * Rise * u;
            _plate.sizeDelta = new Vector2(width, height);
            _plate.anchoredPosition = new Vector2(0f, -topLogical * u - rise);

            _tier.gameObject.SetActive(banded);
            if (banded)
            {
                _tier.rectTransform.anchoredPosition = new Vector2(padX, -padY);
                _tier.rectTransform.sizeDelta = new Vector2(chipW, cellH);
                HudCanvas.SetText(_tier, "T" + tiers[live]);
            }
            _caption.gameObject.SetActive(captioned);
            if (captioned)
            {
                _caption.rectTransform.anchoredPosition = new Vector2(0f, -(padY + cellH));
                _caption.rectTransform.sizeDelta = new Vector2(width, captionH);
                HudCanvas.SetText(_caption, names[live]);
            }

            _viewport.anchoredPosition = new Vector2(padX + chip, -padY);
            _viewport.sizeDelta = new Vector2(viewW, cellH);

            var count = shown + (peek ? 1 : 0);
            for (var c = 0; c < count; c++)
            {
                var i = first + c;
                var cell = CellAt(c);
                if (!cell.Rect.gameObject.activeSelf) cell.Rect.gameObject.SetActive(true);
                cell.Rect.anchoredPosition = new Vector2(c * cellW, 0f);
                cell.Rect.sizeDelta = new Vector2(cellW, cellH);
                foreach (var image in new[] { cell.Back, cell.Icon })
                {
                    image.rectTransform.offsetMin = new Vector2(cellPad, cellPad);
                    image.rectTransform.offsetMax = new Vector2(-cellPad, -cellPad);
                }
                // The entries not landed on are faded, so the live one reads
                // at a glance; the peek fainter still.
                var alpha = i == live ? 1f : i > last ? 0.2f : 0.35f;
                Paint(cell.Back, backs != null && i < backs.Length ? backs[i] : 0u, alpha);
                Paint(cell.Icon, icons != null && i < icons.Length ? icons[i] : 0u, alpha);
            }
            for (var c = count; c < _cells.Count; c++)
                if (_cells[c].Rect.gameObject.activeSelf) _cells[c].Rect.gameObject.SetActive(false);

            // The highlight slides to the live entry rather than jumping, so
            // a press reads as a step along the cycle.
            var target = (live - first) * cellW;
            _selectorX = float.IsNaN(_selectorX) ? target : Mathf.Lerp(_selectorX, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 22f));
            _selector.anchoredPosition = new Vector2(_selectorX, 0f);
            _selector.sizeDelta = new Vector2(cellW, cellH);
        }

        private static void Paint(Image image, uint index, float alpha)
        {
            var sprite = index != 0u ? SpriteFor(index) : null;
            image.sprite = sprite;
            image.enabled = sprite != null;
            image.color = new Color(1f, 1f, 1f, alpha);
        }
    }
}
