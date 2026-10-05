using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // A small card beside the game's victory/defeat panel saying what this
    // mod did with the match: the result report, the stats upload and the
    // replay upload (with its progress), and a link to the match page once
    // the site has said which match it is. A ranked game with both uploads
    // off gets one line saying where to turn them on instead.
    //
    // It is on a canvas of the mod's own (HudCanvas.EnsureOwn), over the
    // game's HUD, so it still shows with the HUD hidden and over
    // SanctuaryHud's stats screen. It shows only while the result panel is
    // up and no menu is open over it. Whatever finishes after the player
    // has left the match is told in the menu by the overlay toast instead.
    public partial class LadderReporterPlugin
    {
        private enum Tone { Busy, Good, Bad, Quiet }

        private sealed class CardLine
        {
            public string Text;
            public Tone Tone;
        }

        // What the card says about the match just decided; replaced by the
        // next one's.
        private sealed class MatchStatus
        {
            public string MatchId;                 // for the link, once known
            public string Recording;               // the match's replay file: which match it is
            public readonly CardLine Report = new CardLine();
            public CardLine Stats, Replay;         // null: not wanted
            public bool UploadsOff;
            public int Version;                    // bumped on every change
        }

        private MatchStatus _status;

        private const float CardWidth = 640f;

        private RectTransform _card;
        private readonly CardRow[] _cardRows = new CardRow[4];
        private PanelHeading _cardLink;
        private int _cardShownVersion = -1;
        private string _cardErr;

        private sealed class CardRow
        {
            public RectTransform Row;
            public Image Dot;
            public TMP_Text Text;
        }

        // ---- the status ----------------------------------------------------

        /// At the result: a new card for this match.
        private void StartStatus(bool ranked)
        {
            _status = new MatchStatus { Recording = RecordingPath() };
            if (!ranked) SetReport(Tone.Quiet, "Not a ladder game: nothing reported (test upload)");
            else SetReport(Tone.Busy, "Reporting the result...");
        }

        private void SetReport(Tone tone, string text) => SetLine(_status?.Report, tone, text);

        // /api/report's outcomes: a claimed win waits for the opponent (or
        // the confirmation window), a loss or an agreeing report applies at
        // once, a contradicting one freezes the match.
        private static string ReportLine(string outcome)
        {
            switch (outcome)
            {
                case "applied": return "Result recorded on the ladder";
                case "reported": return "Result reported: waiting for your opponent to confirm";
                case "disputed": return "Result disputed: the two reports disagree, an admin will settle it";
                default: return "Result reported";
            }
        }

        private void SetStats(Tone tone, string text) => SetLine(_status?.Stats, tone, text);

        /// The replay line, when the card is this match's; otherwise a
        /// toast in the menu for an outcome worth telling (`final`).
        private void SetReplay(string matchId, Tone tone, string text, bool final = false)
        {
            if (_status?.Replay != null && _status.MatchId == matchId)
            {
                SetLine(_status.Replay, tone, text);
                if (CardShowing) return;
            }
            if (final && !NetworkManagerIsReplay()) Overlay("REPLAY", text, 8f);
        }

        private void SetLine(CardLine line, Tone tone, string text)
        {
            if (line == null || _status == null || (line.Tone == tone && line.Text == text)) return;
            line.Tone = tone;
            line.Text = text;
            _status.Version++;
        }

        private static bool NetworkManagerIsReplay() => EM.Network.NetworkManager.IsReplayPlayback;

        private bool CardShowing => _card != null && _card.gameObject.activeSelf;

        // ---- the card ------------------------------------------------------

        /// Every frame.
        private void UpdateCard()
        {
            try
            {
                // Only in the match the card is about: a new match records
                // into a new file.
                var result = _status != null && !NetworkManagerIsReplay() &&
                             string.Equals(RecordingPath(), _status.Recording, StringComparison.OrdinalIgnoreCase)
                    ? MatchStatsCore.ResultPanel()
                    : null;
                var show = result != null && result.IsVisible && !MenuOpen(false);
                if (!show)
                {
                    if (_card != null && _card.gameObject.activeSelf) _card.gameObject.SetActive(false);
                    return;
                }
                var root = HudCanvas.EnsureOwn();
                if (root == null) return;
                if (_card == null) BuildCard(root);
                if (!_card.gameObject.activeSelf)
                {
                    _card.gameObject.SetActive(true);
                    _cardShownVersion = -1;
                    _cardPlacedLogged = false;
                    Logger.LogInfo("Ladder reporter: status card shown beside the result panel.");
                }
                if (_cardShownVersion != _status.Version) FillCard();
                PlaceCard(result as SanctuaryUI.GameResultPanelUI);
            }
            catch (Exception e)
            {
                if (_cardErr == e.Message) return;
                _cardErr = e.Message;
                Logger.LogWarning($"Ladder reporter: status card: {e}");
            }
        }

        private void BuildCard(RectTransform root)
        {
            _card = HudCanvas.Plate(root, "Ladder status");
            var group = _card.gameObject.AddComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(24, 24, 18, 16);
            group.spacing = 8f;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            HudCanvas.FitToContents(_card);
            HudControls.Size(_card.gameObject, CardWidth, -1f);

            HudControls.Label(_card, "Heading", "SANCTUARYDB LADDER", 22f, AccentColour, TextAlignmentOptions.Left,
                style: FontStyles.Bold);
            for (var i = 0; i < _cardRows.Length; i++)
            {
                var row = HudControls.Row(_card, "Line " + i, 14f, TextAnchor.UpperLeft);
                var dotCell = HudControls.Cell(row, "Dot", 14f, 32f);
                var dot = HudCanvas.Fill(dotCell, "Fill", Color.white);
                dot.sprite = HudStyle.Shade;
                dot.type = Image.Type.Sliced;
                var drt = dot.rectTransform;
                drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
                drt.sizeDelta = new Vector2(14f, 14f);
                var text = HudControls.Label(row, "Text", "", 26f, Color.white, TextAlignmentOptions.TopLeft);
                text.textWrappingMode = TextWrappingModes.Normal;
                HudControls.Size(text.gameObject, CardWidth - 48f - 28f, -1f);
                _cardRows[i] = new CardRow { Row = row, Dot = dot, Text = text };
            }
            _cardLink = PanelHeading.Create(_card, "Link", 24f, AccentColour, TextAlignmentOptions.Left);
            HudCanvas.SetText(_cardLink.Text, "VIEW THE MATCH ON SANCTUARYDB  >");
            _cardLink.OnClick = OpenMatchPage;
        }

        private void FillCard()
        {
            _cardShownVersion = _status.Version;
            var i = 0;
            Row(_status.Report);
            if (_status.UploadsOff)
            {
                Row(new CardLine { Tone = Tone.Quiet, Text = "Stats and replay uploads are off: turn them on in F8 > Ladder Reporter > Upload" });
            }
            else
            {
                Row(_status.Stats);
                Row(_status.Replay);
            }
            for (; i < _cardRows.Length; i++) _cardRows[i].Row.gameObject.SetActive(false);
            var link = _status.MatchId != null && !_status.MatchId.StartsWith("dryrun-", StringComparison.Ordinal);
            if (_cardLink.gameObject.activeSelf != link) _cardLink.gameObject.SetActive(link);
            HudCanvas.LayoutVersion++;

            void Row(CardLine line)
            {
                if (line?.Text == null || i >= _cardRows.Length) return;
                var row = _cardRows[i++];
                row.Row.gameObject.SetActive(true);
                row.Dot.color = ToneColour(line.Tone);
                HudCanvas.SetText(row.Text, line.Text);
                row.Text.color = line.Tone == Tone.Quiet ? HudControls.TextMid : Color.white;
            }
        }

        private static Color ToneColour(Tone tone)
        {
            switch (tone)
            {
                case Tone.Good: return GainColour;
                case Tone.Bad: return LossColour;
                case Tone.Busy: return AccentColour;
                default: return HudControls.TextDim;
            }
        }

        // To the right of the result text, its top level with the text's;
        // where that doesn't fit, the screen's top right.
        private void PlaceCard(SanctuaryUI.GameResultPanelUI result)
        {
            var size = HudCanvas.Size;
            var w = _card.rect.width;
            var h = _card.rect.height;
            var x = size.x - w - 60f;
            var y = size.y - h - 160f;
            var text = result != null ? result.gameResultText : null;
            if (text != null && ScreenRectOnRoot(text.rectTransform, out var r) && r.xMax + 60f + w < size.x - 20f)
            {
                x = r.xMax + 60f;
                y = Mathf.Clamp(r.yMax - h, 20f, size.y - h - 20f);
            }
            var at = new Vector2(Mathf.Round(x), Mathf.Round(y));
            if (_card.anchoredPosition == at) return;
            _card.anchoredPosition = at;
            if (!_cardPlacedLogged)
            {
                _cardPlacedLogged = true;
                Logger.LogInfo(FormattableString.Invariant($"Ladder reporter: status card at {at.x:0},{at.y:0} ({w:0}x{h:0}) on a {size.x:0}x{size.y:0} canvas."));
            }
        }

        private bool _cardPlacedLogged;
        private static readonly Vector3[] _cardCorners = new Vector3[4];

        // Where a game UI element is, in the card root's units (origin at
        // its bottom-left, y up). Through screen pixels: the game's canvas
        // and the mod's own overlay need not share a world space (a canvas
        // drawn by a camera has world corners that mean nothing here).
        private static bool ScreenRectOnRoot(RectTransform target, out Rect rect)
        {
            rect = default;
            var root = HudCanvas.Root;
            var canvas = target.GetComponentInParent<Canvas>();
            if (root == null || canvas == null) return false;
            var top = canvas.rootCanvas;
            var cam = top.renderMode == RenderMode.ScreenSpaceOverlay ? null : top.worldCamera;
            target.GetWorldCorners(_cardCorners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var corner in _cardCorners)
            {
                var screen = RectTransformUtility.WorldToScreenPoint(cam, corner);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out var p)) return false;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }
            var origin = root.rect.min;
            rect = new Rect(minX - origin.x, minY - origin.y, maxX - minX, maxY - minY);
            return rect.width > 1f && rect.height > 1f;
        }

        private void OpenMatchPage()
        {
            var id = _status?.MatchId;
            if (id == null || UploadId(id) != id) return;
            var url = _cfgMmBaseUrl.Value.TrimEnd('/') + "/ladder/match/" + id;
            Logger.LogInfo($"Ladder reporter: opening {url}");
            Application.OpenURL(url);
        }

        private void DestroyCard()
        {
            HudCanvas.Destroy();
            _card = null;
        }
    }
}
