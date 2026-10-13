using System;
using System.Collections.Generic;
using System.Linq;
using EM.UI;
using Michsky.UI.Beam;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;
using Object = UnityEngine.Object;

namespace SanctuaryHud
{
    // The ladder queue on the game's own Play screen, above Multiplayer /
    // Skirmish: a "Ranked ladder" badge like the screen's own, the factions a
    // 1v1 may launch you as, a button per mode showing how many are waiting
    // (or how long you have been), and a line saying what's going on.
    // Buttons and badge are clones of the screen's own, so they look, sound
    // and highlight like the game's; Queue.cs does the queueing.
    //
    // The block goes with the menu scene: a fresh PlayInterface (the menu
    // rebuilt after a match) gets a fresh block.
    public partial class LadderReporterPlugin
    {
        private const float RankedModeWidth = 340f;
        private const float RankedFactionWidth = (QueueModesCount - 1) * (RankedModeWidth + RankedGap) / 3f - RankedGap;
        private const int QueueModesCount = 3;
        private const float RankedButtonHeight = 60f;
        private const float RankedGap = 16f;

        private PlayInterface _prBuiltFor;
        private RectTransform _prBlock, _prTemplate;
        private RankedButton[] _prModes, _prFactions;
        private ButtonManager _prLink;
        private TMP_Text _prLine;
        private string _prErr;

        // A cloned game button with the state it was last given. "On"
        // (queued, or a faction picked) is the button's own checkmark.
        private sealed class RankedButton
        {
            public ButtonManager Button;
            public string Label;
            public bool On, Set_;

            public void Set(string label, bool on)
            {
                if (Set_ && label == Label && on == On) return;
                Set_ = true;
                Label = label;
                On = on;
                Button.enableIcon = on;
                Button.SetText(label);   // re-applies the icon too
            }
        }

        private void UpdatePlayRanked()
        {
            try
            {
                var pi = PlayInterface.Instance;
                var show = pi != null && QueueMenuWindow() == InterfaceManager.Window.Play;
                if (!show)
                {
                    if (_prBlock != null && _prBlock.gameObject.activeSelf) _prBlock.gameObject.SetActive(false);
                    return;
                }
                if (!ReferenceEquals(pi, _prBuiltFor) || _prBlock == null)
                {
                    _prBuiltFor = pi;
                    if (_prBlock != null) Object.Destroy(_prBlock.gameObject);
                    BuildPlayRanked(pi);
                    if (_prBlock == null) return;
                }
                if (!_prBlock.gameObject.activeSelf) _prBlock.gameObject.SetActive(true);
                FillPlayRanked();
                PlacePlayRanked();
            }
            catch (Exception e)
            {
                if (_prErr == e.Message) return;
                _prErr = e.Message;
                Logger.LogWarning($"Queue: Play screen block: {e}");
            }
        }

        private void BuildPlayRanked(PlayInterface pi)
        {
            _prBlock = null;
            var content = pi.transform.Find("Content") as RectTransform;
            var template = content != null ? content.Find("Multiplayer Button") as RectTransform : null;
            if (template == null || template.GetComponent<ButtonManager>() == null)
            {
                Logger.LogWarning("Queue: the Play screen has no Multiplayer Button to copy; no ranked buttons there.");
                return;
            }
            _prTemplate = template;
            var font = template.GetComponentInChildren<TMP_Text>(true);

            // Clones are made in an inactive holder, so nothing on them runs
            // until they are set up and moved into the live block.
            var holder = new GameObject("Ranked holder", typeof(RectTransform));
            holder.SetActive(false);
            holder.transform.SetParent(content, false);

            var block = new GameObject("Ranked Ladder", typeof(RectTransform));
            block.transform.SetParent(content, false);
            _prBlock = (RectTransform)block.transform;
            _prBlock.anchorMin = _prBlock.anchorMax = _prBlock.pivot = Vector2.zero;
            var group = block.AddComponent<VerticalLayoutGroup>();
            group.spacing = 16f;
            group.childAlignment = TextAnchor.LowerLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var fit = block.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // The screen's own title badge ("PLAY"), retitled; plain text if
            // it isn't there.
            var badge = content.Find("Panel Header/Content");
            if (badge != null)
            {
                var copy = Object.Instantiate(badge.gameObject, holder.transform, false);
                copy.name = "Ranked badge";
                foreach (var loc in copy.GetComponentsInChildren<LocalizedObject>(true)) Object.DestroyImmediate(loc);
                var cg = copy.GetComponent<CanvasGroup>();
                if (cg != null) cg.alpha = 1f;
                foreach (var t in copy.GetComponentsInChildren<TMP_Text>(true)) t.text = "Ranked Ladder";
                var row = Row(_prBlock, "Badge row", TextAnchor.MiddleLeft);
                copy.transform.SetParent(row, false);
            }
            else
            {
                BlockText(_prBlock, "Title", font, 30f).text = "RANKED LADDER";
            }

            // Which factions a 1v1 may launch you as (team games are hosted
            // by hand, where you pick in the lobby).
            var factions = Row(_prBlock, "Factions", TextAnchor.MiddleLeft);
            var label = BlockText(factions, "Label", font, 24f);
            label.text = "1V1 AS";
            label.fontStyle = FontStyles.UpperCase;
            if (font != null) label.characterSpacing = font.characterSpacing;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(1f, 1f, 1f, 0.6f);
            // As wide as 1v1, so the factions sit over the other two modes.
            HudControls.Size(label.gameObject, RankedModeWidth, RankedButtonHeight);
            _prFactions = new RankedButton[AllFactions.Length];
            for (var i = 0; i < AllFactions.Length; i++)
            {
                var faction = AllFactions[i];
                _prFactions[i] = CloneGameButton(template.gameObject, holder.transform, factions, "Faction " + faction, RankedFactionWidth);
                _prFactions[i].Button.onClick.AddListener(() => ToggleFaction(faction));
            }

            var modes = Row(_prBlock, "Modes", TextAnchor.MiddleLeft);
            _prModes = new RankedButton[QueueModes.Length];
            for (var i = 0; i < QueueModes.Length; i++)
            {
                var mode = QueueModes[i];
                _prModes[i] = CloneGameButton(template.gameObject, holder.transform, modes, "Ranked " + mode, RankedModeWidth);
                _prModes[i].Button.onClick.AddListener(() => ToggleQueue(mode));
            }

            _prLine = BlockText(_prBlock, "Line", font, 24f);
            _prLine.textWrappingMode = TextWrappingModes.Normal;
            HudControls.Size(_prLine.gameObject, RowWidth, -1f);

            var link = CloneGameButton(template.gameObject, holder.transform, _prBlock, "Ranked match page", RowWidth);
            link.Set("Open the match on SanctuaryDB", false);
            link.Button.onClick.AddListener(OpenQueueMatch);
            _prLink = link.Button;
            _prLink.gameObject.SetActive(false);

            Object.Destroy(holder);
            Logger.LogInfo($"Queue: ranked buttons added to the Play screen (badge {(badge != null ? "copied" : "missing")}).");
        }

        private static float RowWidth => QueueModes.Length * RankedModeWidth + (QueueModes.Length - 1) * RankedGap;

        private static RectTransform Row(Transform parent, string name, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = RankedGap;
            h.childAlignment = alignment;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        // A copy of the game's button with its own text and click, sized by
        // its row rather than by its text.
        private static RankedButton CloneGameButton(GameObject template, Transform holder, Transform parent, string name, float width)
        {
            var go = Object.Instantiate(template, holder, false);
            go.name = name;
            foreach (var loc in go.GetComponentsInChildren<LocalizedObject>(true)) Object.DestroyImmediate(loc);
            var b = go.GetComponent<ButtonManager>();
            b.useLocalization = false;
            b.onClick = new UnityEvent();
            b.onDoubleClick = new UnityEvent();
            var button = go.GetComponent<Button>();
            if (button != null) button.onClick = new Button.ButtonClickedEvent();
            b.autoFitContent = false;
            b.buttonText = name;
            HudControls.Size(go, width, RankedButtonHeight);
            go.transform.SetParent(parent, false);
            return new RankedButton { Button = b };
        }

        private static TMP_Text BlockText(Transform parent, string name, TMP_Text font, float size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font.font;
                text.fontSharedMaterial = font.fontSharedMaterial;
            }
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }

        private void FillPlayRanked()
        {
            for (var i = 0; i < QueueModes.Length; i++)
            {
                var time = ModeTime(i);
                _prModes[i].Set(time ?? $"{QueueModes[i].ToUpperInvariant()}  ·  {_qWaiting[i]} queued", time != null);
            }
            var picked = ConfiguredFactions();
            for (var i = 0; i < AllFactions.Length; i++) _prFactions[i].Set(AllFactions[i], picked.Contains(AllFactions[i]));

            var live = LiveGamesText();
            var line = QueueLine();
            SetTmp(_prLine, live.Length == 0 ? line : $"<color=#FFFFFF99>{live}.</color>  {line}");
            var link = MatchToHost;
            if (_prLink.gameObject.activeSelf != link) _prLink.gameObject.SetActive(link);
        }

        // One faction on or off; never the last one off (none would mean
        // any, which is what all three already say).
        private void ToggleFaction(string faction)
        {
            var picked = new List<string>(ConfiguredFactions());
            if (picked.Contains(faction))
            {
                if (picked.Count == 1) return;
                picked.Remove(faction);
            }
            else picked.Add(faction);
            var ordered = AllFactions.Where(picked.Contains).ToArray();
            _cfgQueueFactions.Value = ordered.Length == AllFactions.Length ? "" : string.Join(",", ordered);
            Logger.LogInfo($"Queue: 1v1 factions now {string.Join(",", ordered)}.");
            // Already waiting for a 1v1: the site takes the new pick on a
            // rejoin, which keeps your place.
            if (InQueue("1v1")) RequestQueue(new[] { "1v1" }, Enumerable.Empty<string>(), null);
        }

        private static void SetTmp(TMP_Text text, string value)
        {
            if (text.text != value) text.text = value;
        }

        // Just above Multiplayer / Skirmish, left edges lined up, wherever
        // the screen has put that button.
        private void PlacePlayRanked()
        {
            var parent = (RectTransform)_prBlock.parent;
            var r = parent.rect;
            var min = parent.InverseTransformPoint(_prTemplate.TransformPoint(_prTemplate.rect.min));
            var max = parent.InverseTransformPoint(_prTemplate.TransformPoint(_prTemplate.rect.max));
            var at = new Vector2(Mathf.Round(min.x - r.xMin), Mathf.Round(max.y - r.yMin + 48f));
            if (_prBlock.anchoredPosition != at) _prBlock.anchoredPosition = at;
        }
    }
}
