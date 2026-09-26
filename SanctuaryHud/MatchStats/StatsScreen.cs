using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SanctuaryUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The stats screen: a window over the result screen with a header (map,
    // length), a table of whole-match figures per army (economy or units,
    // by tab), and a chart of one figure over time, one line per army in
    // its own colour, with a readout of every army's value under the mouse.
    //
    // It stands on the game's HUD canvas just above the result panel, so
    // it takes the game's UI Scale and font, and its dimmed backdrop takes
    // every click, so nothing reaches the map behind it. Closing it leaves
    // a MATCH STATS button under the game's result text to bring it back.
    //
    // Laid out by hand in canvas units from the window's top-left rather
    // than by layout groups: the table and chart are rebuilt whole on each
    // refresh, which is cheap at this size and keeps the maths in one place.
    internal sealed class StatsScreen
    {
        private const float Pad = 48f;
        private const float HeaderH = 104f;
        private const float TabH = 56f;
        private const float TableHeadH = 44f;
        private const float AxisW = 130f;
        private const float AxisH = 48f;

        private static readonly Color TextColour = new Color(1f, 1f, 1f, 0.92f);
        private static readonly Color DimText = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color HeadText = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color WindowColour = new Color(0.07f, 0.1f, 0.13f, 0.97f);

        private readonly RectTransform _root;
        private readonly GameObject _overlay;
        private readonly RectTransform _window;
        private readonly TMP_Text _title, _sub;
        private readonly PanelHeading _close;
        private readonly List<PanelHeading> _tableTabs = new List<PanelHeading>();
        private readonly List<PanelHeading> _chartTabs = new List<PanelHeading>();
        private readonly RectTransform _table;
        private readonly RectTransform _chartBox;
        private readonly LineChart _chart;
        private readonly RectTransform _labels;
        private readonly RectTransform _tip;
        private readonly TMP_Text _tipText;
        private readonly TMP_Text _empty;
        private readonly RectTransform _reopen;

        private MatchData _data;
        private int _endTick = -1;
        private int _tableTab;
        private int _chartTab;
        private int _shownCursor = -1;
        private float _refreshAt;
        private Vector2 _laidOutFor;
        private float _tableRowH = 56f;

        // What the chart is showing, for the hover readout.
        private readonly List<(ArmyStats army, List<Vector2> points)> _plotted = new List<(ArmyStats, List<Vector2>)>();
        private Func<float, string> _chartFormat = Fmt;

        internal bool IsOpen { get; private set; }

        /// False once the scene has taken the objects with it.
        internal bool Alive => _overlay != null && _reopen != null;

        private sealed class ChartKind
        {
            public string Title;
            public Func<Sample, float> Value;
            /// Rates are smoothed over ten seconds: a raw per-second income
            /// jumps with every reclaim and every stall.
            public bool Rate;
            /// Summed over time: a running total rather than the figure.
            public bool Cumulative;
            public Func<float, string> Format;
        }

        private static readonly ChartKind[] Charts =
        {
            new ChartKind { Title = "SCORE", Value = s => s.Score, Format = Fmt },
            new ChartKind { Title = "ALLOY INCOME", Value = s => s.AlloyIncome, Rate = true, Format = v => Fmt(v) + "/s" },
            new ChartKind { Title = "ENERGY INCOME", Value = s => s.EnergyIncome, Rate = true, Format = v => Fmt(v) + "/s" },
            new ChartKind { Title = "ALLOY SPENT", Value = s => s.AlloySpend, Rate = true, Format = v => Fmt(v) + "/s" },
            new ChartKind { Title = "ALLOY GATHERED", Value = s => s.AlloyIncome, Cumulative = true, Format = Fmt },
            new ChartKind { Title = "ARMY VALUE", Value = s => s.ArmyValue, Format = Fmt },
            new ChartKind { Title = "UNITS", Value = s => s.Units, Format = v => Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture) },
            new ChartKind { Title = "ALLOY STORED", Value = s => s.AlloyStored, Format = Fmt },
        };

        private sealed class Column
        {
            public string Title;
            public float Weight;
            public Func<ArmyStats, string> Value;
            public Color Tint = HeadText;
        }

        private readonly Column[][] _columns;

        internal StatsScreen(RectTransform root)
        {
            _root = root;

            // The dim over everything, which also takes every click.
            var dim = HudCanvas.Fill(root, "Match stats", new Color(0f, 0f, 0f, 0.6f));
            dim.raycastTarget = true;
            Stretch(dim.rectTransform);
            _overlay = dim.gameObject;

            var window = HudCanvas.Fill(dim.transform, "Window", WindowColour);
            window.raycastTarget = true;
            _window = window.rectTransform;
            _window.anchorMin = _window.anchorMax = new Vector2(0.5f, 0.5f);
            _window.pivot = new Vector2(0.5f, 0.5f);
            var accent = AccentColour;
            accent.a = 0.7f;
            HudCanvas.StretchAlongTop(HudCanvas.Fill(_window, "Accent", accent).rectTransform, 3f);

            _title = HudCanvas.Text(_window, "Title", 46f, AccentColour, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            HudCanvas.SetText(_title, "MATCH STATS");
            _sub = HudCanvas.Text(_window, "Sub", 26f, DimText, TextAlignmentOptions.MidlineLeft);

            _close = PanelHeading.Create(_window, "Close", 26f, TextColour, TextAlignmentOptions.Center);
            HudCanvas.SetText(_close.Text, "CLOSE");
            _close.OnClick = Close;

            foreach (var name in new[] { "ECONOMY", "UNITS" })
            {
                var index = _tableTabs.Count;
                var tab = PanelHeading.Create(_window, "Table tab " + name, 24f, TextColour, TextAlignmentOptions.Center);
                HudCanvas.SetText(tab.Text, name);
                tab.OnClick = () => { _tableTab = index; Refresh(); };
                _tableTabs.Add(tab);
            }
            foreach (var kind in Charts)
            {
                var index = _chartTabs.Count;
                var tab = PanelHeading.Create(_window, "Chart tab " + kind.Title, 22f, TextColour, TextAlignmentOptions.Center);
                HudCanvas.SetText(tab.Text, kind.Title);
                tab.OnClick = () => { _chartTab = index; Refresh(); };
                _chartTabs.Add(tab);
            }

            _table = Box(_window, "Table");

            _chartBox = Box(_window, "Chart box");
            var chartGo = new GameObject("Chart", typeof(RectTransform));
            chartGo.transform.SetParent(_chartBox, false);
            _chart = chartGo.AddComponent<LineChart>();
            _chart.raycastTarget = false;
            Stretch(_chart.rectTransform);
            _labels = Box(_window, "Axis labels");

            _empty = HudCanvas.Text(_chartBox, "Empty", 28f, DimText, TextAlignmentOptions.Center);
            Stretch(_empty.rectTransform);
            HudCanvas.SetText(_empty, "Nothing was recorded for this match.");

            var tip = HudCanvas.Fill(_window, "Readout", new Color(0.05f, 0.07f, 0.09f, 0.95f));
            _tip = tip.rectTransform;
            _tip.anchorMin = _tip.anchorMax = new Vector2(0f, 1f);
            _tip.pivot = new Vector2(0f, 1f);
            _tipText = HudCanvas.Text(_tip, "Text", 24f, TextColour, TextAlignmentOptions.TopLeft);
            _tipText.richText = true;
            var trt = _tipText.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(16f, 12f);
            trt.offsetMax = new Vector2(-16f, -12f);
            _tip.gameObject.SetActive(false);

            _overlay.SetActive(false);

            // The way back once it has been closed.
            _reopen = HudCanvas.Plate(root, "Match stats button");
            var group = _reopen.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(12, 12, 8, 8);
            group.childControlWidth = true;
            group.childControlHeight = true;
            HudCanvas.FitToContents(_reopen);
            var reopen = PanelHeading.Create(_reopen, "Open", 28f, TextColour, TextAlignmentOptions.Center);
            reopen.gameObject.AddComponent<LayoutElement>().minWidth = 300f;
            HudCanvas.SetText(reopen.Text, "MATCH STATS");
            reopen.OnClick = () => Open(_data, _endTick);
            _reopen.gameObject.SetActive(false);

            _columns = new[]
            {
                new[]
                {
                    Col("SCORE", 0.9f, a => Fmt(a.Score), AccentColour),
                    Col("ALLOY GATHERED", 1f, a => Fmt(a.AlloyGathered), AlloyColour),
                    Col("ENERGY GATHERED", 1f, a => Fmt(a.EnergyGathered), EnergyColour),
                    Col("ALLOY SPENT", 1f, a => Fmt(a.AlloySpent), AlloyColour),
                    Col("ENERGY SPENT", 1f, a => Fmt(a.EnergySpent), EnergyColour),
                    Col("PEAK ALLOY", 0.9f, a => Fmt(a.PeakAlloyIncome) + "/s", AlloyColour),
                    Col("ALLOY WASTED", 0.9f, a => Fmt(a.AlloyWasted), AlloyColour),
                    Col("ENERGY WASTED", 0.9f, a => Fmt(a.EnergyWasted), EnergyColour),
                    Col("ALLOY STALL", 0.9f, a => Clock(Seconds(a.AlloyStallTicks)), AlloyColour),
                    Col("ENERGY STALL", 0.9f, a => Clock(Seconds(a.EnergyStallTicks)), EnergyColour),
                },
                new[]
                {
                    Col("SCORE", 0.9f, a => Fmt(a.Score), AccentColour),
                    Col("BUILT", 0.8f, a => a.BuiltTotal.ToString(CultureInfo.InvariantCulture)),
                    Col("LAND", 0.7f, a => a.BuiltLand.ToString(CultureInfo.InvariantCulture)),
                    Col("AIR", 0.7f, a => a.BuiltAir.ToString(CultureInfo.InvariantCulture)),
                    Col("NAVAL", 0.7f, a => a.BuiltNaval.ToString(CultureInfo.InvariantCulture)),
                    Col("ENGINEERS", 0.8f, a => a.BuiltEngineers.ToString(CultureInfo.InvariantCulture)),
                    Col("STRUCTURES", 0.9f, a => a.BuiltStructures.ToString(CultureInfo.InvariantCulture)),
                    Col("BUILT VALUE", 0.9f, a => Fmt(a.BuiltValue), AlloyColour),
                    Col("LOST", 0.7f, a => a.LostTotal.ToString(CultureInfo.InvariantCulture)),
                    Col("LOST VALUE", 0.9f, a => Fmt(a.LostValue), AlloyColour),
                    Col("KILLED VALUE", 0.9f, a => Fmt(a.KilledValue), AlloyColour),
                    Col("PEAK ARMY", 0.9f, a => Fmt(a.PeakArmyValue), AlloyColour),
                    Col("PEAK UNITS", 0.8f, a => a.PeakUnits.ToString(CultureInfo.InvariantCulture)),
                },
            };
        }

        private static Column Col(string title, float weight, Func<ArmyStats, string> value, Color? tint = null) =>
            new Column { Title = title, Weight = weight, Value = value, Tint = tint ?? HeadText };

        // ---- open, close, per frame ---------------------------------------------

        internal void Open(MatchData data, int endTick)
        {
            if (data == null || !Alive) return;
            _data = data;
            _endTick = endTick;
            IsOpen = true;
            _overlay.SetActive(true);
            _overlay.transform.SetAsLastSibling();
            _laidOutFor = Vector2.zero;
            Refresh();
        }

        internal void Close()
        {
            IsOpen = false;
            if (_overlay != null) _overlay.SetActive(false);
        }

        internal void Destroy()
        {
            if (_overlay != null) UnityEngine.Object.Destroy(_overlay);
            if (_reopen != null) UnityEngine.Object.Destroy(_reopen.gameObject);
        }

        /// Every frame once the match has a result: the button under the
        /// result text while the screen is closed, the readout while it is
        /// open, and a refresh when new figures have come in.
        internal void Update(MatchData data, SanctuaryPanelUI result)
        {
            if (!Alive) return;
            if (data != null) _data = data;

            var showButton = !IsOpen && result != null;
            if (_reopen.gameObject.activeSelf != showButton) _reopen.gameObject.SetActive(showButton);
            if (showButton) PlaceButton(result);

            if (!IsOpen || _data == null) return;
            if (HudCanvas.Size != _laidOutFor) Refresh();
            else if (_data.Cursor != _shownCursor && Time.realtimeSinceStartup >= _refreshAt) Refresh();
            Hover();
        }

        private void PlaceButton(SanctuaryPanelUI result)
        {
            var size = HudCanvas.Size;
            var w = _reopen.rect.width;
            var h = _reopen.rect.height;
            var x = (size.x - w) * 0.5f;
            var y = size.y * 0.3f;
            var text = (result as GameResultPanelUI)?.gameResultText;
            if (text != null && HudCanvas.LocalRect(text, out var r) && r.yMin - 40f - h > 0f)
            {
                x = r.center.x - w * 0.5f;
                y = r.yMin - 40f - h;
            }
            _reopen.anchoredPosition = new Vector2(Mathf.Round(x), Mathf.Round(y));
        }

        // ---- layout and contents ------------------------------------------------

        private void Refresh()
        {
            if (_data == null || !Alive) return;
            _shownCursor = _data.Cursor;
            _refreshAt = Time.realtimeSinceStartup + 2f;

            var screen = HudCanvas.Size;
            _laidOutFor = screen;
            var w = Mathf.Min(screen.x * 0.92f, 3200f);
            var h = Mathf.Min(screen.y * 0.9f, 1900f);
            _window.sizeDelta = new Vector2(w, h);

            var players = _data.Players();
            var inner = w - Pad * 2f;

            // Header: title, map and length, close.
            Place(_title.rectTransform, Pad, Pad * 0.6f, inner * 0.5f, 60f);
            var seconds = _data.Seconds(_endTick >= 0 ? _endTick : _data.Tick);
            var map = _data.Map.Length > 0 ? _data.Map : "Unknown map";
            HudCanvas.SetText(_sub, $"<color=#FFFFFFDD>{Escape(map)}</color>      {Clock(seconds)}      {players.Count} {(players.Count == 1 ? "army" : "armies")}");
            Place(_sub.rectTransform, Pad, Pad * 0.6f + 58f, inner * 0.7f, 40f);
            Place((RectTransform)_close.transform, w - Pad - 200f, Pad * 0.6f, 200f, 60f);

            // Table tabs, then the table.
            var y = HeaderH + Pad * 0.5f;
            for (var i = 0; i < _tableTabs.Count; i++)
            {
                Place((RectTransform)_tableTabs[i].transform, Pad + i * 220f, y, 210f, TabH);
                Tab(_tableTabs[i], i == _tableTab);
            }
            y += TabH + 8f;
            _tableRowH = players.Count > 6 ? 46f : 56f;
            var tableH = TableHeadH + _tableRowH * Math.Max(1, players.Count);
            Place(_table, Pad, y, inner, tableH);
            BuildTable(players, inner);
            y += tableH + 28f;

            // Chart tabs, then the chart and its axes.
            var tabW = Mathf.Min(300f, inner / Charts.Length);
            for (var i = 0; i < _chartTabs.Count; i++)
            {
                Place((RectTransform)_chartTabs[i].transform, Pad + i * tabW, y, tabW - 10f, TabH);
                Tab(_chartTabs[i], i == _chartTab);
            }
            y += TabH + 20f;
            var chartH = Mathf.Max(160f, h - y - Pad - AxisH);
            Place(_chartBox, Pad + AxisW, y, inner - AxisW, chartH);
            Place(_labels, 0f, 0f, w, h);
            BuildChart(players, inner - AxisW, chartH, Pad + AxisW, y);
        }

        private void BuildTable(List<ArmyStats> players, float width)
        {
            Clear(_table);
            var columns = _columns[_tableTab];
            const float playerWeight = 2.6f;
            const float resultWeight = 0.9f;
            var total = playerWeight + resultWeight + columns.Sum(c => c.Weight);
            var unit = width / total;

            // Header row.
            Cell(_table, "PLAYER", 0f, 0f, playerWeight * unit, TableHeadH, 20f, HeadText, TextAlignmentOptions.MidlineLeft);
            var x = playerWeight * unit;
            Cell(_table, "RESULT", x, 0f, resultWeight * unit, TableHeadH, 20f, HeadText, TextAlignmentOptions.Center);
            x += resultWeight * unit;
            foreach (var c in columns)
            {
                Cell(_table, c.Title, x, 0f, c.Weight * unit, TableHeadH, 20f, c.Tint, TextAlignmentOptions.Center);
                x += c.Weight * unit;
            }
            var rule = HudCanvas.Fill(_table, "Rule", new Color(1f, 1f, 1f, 0.15f));
            Place(rule.rectTransform, 0f, TableHeadH - 2f, width, 2f);

            var rowY = TableHeadH;
            var lastTeam = int.MinValue;
            var row = 0;
            foreach (var a in players)
            {
                // A line between teams, so allies read as one block.
                if (lastTeam != int.MinValue && a.Team != lastTeam)
                {
                    var split = HudCanvas.Fill(_table, "Team rule", new Color(1f, 1f, 1f, 0.1f));
                    Place(split.rectTransform, 0f, rowY - 1f, width, 2f);
                }
                lastTeam = a.Team;
                if (row++ % 2 == 1)
                {
                    var band = HudCanvas.Fill(_table, "Band", new Color(1f, 1f, 1f, 0.03f));
                    Place(band.rectTransform, 0f, rowY, width, _tableRowH);
                    band.rectTransform.SetAsFirstSibling();
                }

                var swatch = HudCanvas.Fill(_table, "Swatch", a.Colour);
                Place(swatch.rectTransform, 4f, rowY + (_tableRowH - 26f) * 0.5f, 26f, 26f);
                var you = a.Id == _data.Focus ? "  <color=#3DAFFF>YOU</color>" : "";
                var faction = a.FactionName.Length > 0 ? $"  <color=#FFFFFF88><size=80%>{a.FactionName}</size></color>" : "";
                var name = Cell(_table, Escape(a.DisplayName) + faction + you, 46f, rowY, playerWeight * unit - 46f, _tableRowH, 26f, NameColour(a), TextAlignmentOptions.MidlineLeft);
                name.richText = true;
                name.overflowMode = TextOverflowModes.Ellipsis;

                x = playerWeight * unit;
                var (label, colour) = Result(a);
                Cell(_table, label, x, rowY, resultWeight * unit, _tableRowH, 24f, colour, TextAlignmentOptions.Center, FontStyles.Bold);
                x += resultWeight * unit;
                foreach (var c in columns)
                {
                    Cell(_table, c.Value(a), x, rowY, c.Weight * unit, _tableRowH, 26f, TextColour, TextAlignmentOptions.Center);
                    x += c.Weight * unit;
                }
                rowY += _tableRowH;
            }
        }

        private static Color NameColour(ArmyStats a) => a.Condition == 2 ? new Color(1f, 1f, 1f, 0.7f) : TextColour;

        private (string, Color) Result(ArmyStats a)
        {
            switch (a.Condition)
            {
                case 1: return ("VICTORY", GainColour);
                case 2: return (a.ConditionTick > 0 ? "DEFEAT " + Clock(_data.Seconds(a.ConditionTick)) : "DEFEAT", LossColour);
                default: return ("-", DimText);
            }
        }

        private void BuildChart(List<ArmyStats> players, float width, float height, float left, float top)
        {
            Clear(_labels);
            _chart.Lines.Clear();
            _plotted.Clear();
            var kind = Charts[_chartTab];
            _chartFormat = kind.Format;

            // At most a point every few canvas units: a long game has
            // thousands of samples and the chart is a couple of thousand wide.
            var maxPoints = Mathf.Max(50, Mathf.RoundToInt(width / 4f));
            float xMax = 1f, yMax = 0f;
            foreach (var a in players)
            {
                var points = Points(a, kind, maxPoints);
                if (points.Count == 0) continue;
                xMax = Mathf.Max(xMax, points[points.Count - 1].x);
                foreach (var p in points) yMax = Mathf.Max(yMax, p.y);
                var series = new LineChart.Series { Colour = a.Colour, Thickness = a.Id == _data.Focus ? 5f : 3.5f };
                series.Points.AddRange(points);
                _chart.Lines.Add(series);
                _plotted.Add((a, points));
            }
            // The focused army's line drawn last, so it is on top.
            _chart.Lines.Sort((p, q) => p.Thickness.CompareTo(q.Thickness));

            var empty = _plotted.Count == 0;
            if (_empty.gameObject.activeSelf != empty) _empty.gameObject.SetActive(empty);

            yMax = NiceCeiling(yMax <= 0f ? 1f : yMax * 1.05f);
            _chart.XMax = xMax;
            _chart.YMax = yMax;
            _chart.GridLines = 4;
            _chart.CursorX = -1f;
            _chart.SetVerticesDirty();

            // Y labels on the grid lines, right-aligned against the chart.
            for (var i = 0; i <= _chart.GridLines; i++)
            {
                var value = yMax * i / _chart.GridLines;
                var y = top + height - height * i / _chart.GridLines;
                Cell(_labels, kind.Format(value), left - AxisW, y - 16f, AxisW - 16f, 32f, 20f, DimText, TextAlignmentOptions.MidlineRight);
            }
            // Time along the bottom, at a step that gives no more than eight.
            var step = new[] { 30f, 60f, 120f, 300f, 600f, 900f, 1200f, 1800f, 3600f }.FirstOrDefault(s => xMax / s <= 8f);
            if (step <= 0f) step = 3600f;
            for (var t = 0f; t <= xMax + 0.01f; t += step)
            {
                var x = left + width * t / xMax;
                Cell(_labels, Clock(t), x - 60f, top + height + 8f, 120f, 32f, 20f, DimText, TextAlignmentOptions.Center);
            }
        }

        // One army's line for a chart: the figure at each second, smoothed
        // or summed as the chart asks, then averaged down into buckets.
        private List<Vector2> Points(ArmyStats a, ChartKind kind, int maxPoints)
        {
            var samples = a.Samples;
            var n = samples.Count;
            var result = new List<Vector2>();
            if (n == 0) return result;

            var values = new float[n];
            var running = 0f;
            for (var i = 0; i < n; i++)
            {
                var v = kind.Value(samples[i]);
                if (kind.Cumulative)
                {
                    running += v;
                    v = running;
                }
                values[i] = v;
            }
            if (kind.Rate)
            {
                const int window = 10;
                var smoothed = new float[n];
                var sum = 0f;
                for (var i = 0; i < n; i++)
                {
                    sum += values[i];
                    if (i >= window) sum -= values[i - window];
                    smoothed[i] = sum / Math.Min(i + 1, window);
                }
                values = smoothed;
            }

            var bucket = Math.Max(1, (int)Math.Ceiling(n / (double)maxPoints));
            for (var i = 0; i < n; i += bucket)
            {
                float sx = 0f, sy = 0f;
                var count = 0;
                for (var j = i; j < Math.Min(n, i + bucket); j++)
                {
                    sx += _data.Seconds(samples[j].Tick);
                    sy += values[j];
                    count++;
                }
                result.Add(new Vector2(sx / count, sy / count));
            }
            return result;
        }

        private void Hover()
        {
            var canvas = HudCanvas.Canvas;
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var rt = _chart.rectTransform;
            var inside = _plotted.Count > 0 &&
                         RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out var local) &&
                         rt.rect.Contains(local);
            if (!inside)
            {
                if (_chart.CursorX >= 0f)
                {
                    _chart.CursorX = -1f;
                    _chart.SetVerticesDirty();
                }
                if (_tip.gameObject.activeSelf) _tip.gameObject.SetActive(false);
                return;
            }

            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out local);
            var t = _chart.ToDataX(local.x);
            if (Mathf.Abs(t - _chart.CursorX) > 0.01f)
            {
                _chart.CursorX = t;
                _chart.SetVerticesDirty();
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("<color=#FFFFFF99>").Append(Clock(t)).Append("</color>");
            foreach (var (army, points) in _plotted.OrderByDescending(p => ValueAt(p.points, t)))
            {
                var hex = ColorUtility.ToHtmlStringRGB(army.Colour);
                sb.Append("\n<color=#").Append(hex).Append(">")
                  .Append(Escape(army.DisplayName)).Append("</color>   <b>").Append(_chartFormat(ValueAt(points, t))).Append("</b>");
            }
            HudCanvas.SetText(_tipText, sb.ToString());
            if (!_tip.gameObject.activeSelf) _tip.gameObject.SetActive(true);
            _tip.SetAsLastSibling();

            var pref = _tipText.GetPreferredValues(_tipText.text);
            var size = new Vector2(pref.x + 32f, pref.y + 24f);
            _tip.sizeDelta = size;

            // Beside the cursor, in the window's own top-left units, flipped
            // to the left of it on the right half of the chart.
            var windowLocal = _window.InverseTransformPoint(rt.TransformPoint(local));
            var wr = _window.rect;
            var x = windowLocal.x - wr.xMin;
            var y = wr.yMax - windowLocal.y;
            x = x + 28f + size.x > wr.width - Pad ? x - 28f - size.x : x + 28f;
            y = Mathf.Clamp(y - size.y * 0.5f, Pad, wr.height - Pad - size.y);
            _tip.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
        }

        private static float ValueAt(List<Vector2> points, float t)
        {
            if (points.Count == 0) return 0f;
            if (t <= points[0].x) return points[0].y;
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i].x < t) continue;
                var a = points[i - 1];
                var b = points[i];
                var span = b.x - a.x;
                return span <= 1e-4f ? b.y : Mathf.Lerp(a.y, b.y, (t - a.x) / span);
            }
            return points[points.Count - 1].y;
        }

        // ---- helpers --------------------------------------------------------------

        private float Seconds(int ticks) => _data != null ? _data.Seconds(ticks) : ticks / 10f;

        private static void Tab(PanelHeading tab, bool active)
        {
            tab.Text.color = active ? AccentColour : DimText;
            tab.Text.fontStyle = active ? FontStyles.Bold : FontStyles.Normal;
        }

        private static RectTransform Box(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Clear(Transform t)
        {
            for (var i = t.childCount - 1; i >= 0; i--)
            {
                var child = t.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static TMP_Text Cell(Transform parent, string text, float x, float y, float w, float h, float size, Color colour,
                                     TextAlignmentOptions alignment, FontStyles style = FontStyles.Normal)
        {
            var t = HudCanvas.Text(parent, "Cell", size, colour, alignment, style);
            t.richText = true;
            HudCanvas.SetText(t, text);
            Place(t.rectTransform, x, y, w, h);
            return t;
        }

        /// Puts a RectTransform at (x, y) from its parent's top-left, in
        /// canvas units, at the given size.
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
            rt.sizeDelta = new Vector2(Mathf.Round(w), Mathf.Round(h));
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// A player's name as plain text inside rich text.
        private static string Escape(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

        /// A figure the way the HUD writes them: 1.2K above 999, 3.4M above
        /// a million, one decimal below ten.
        internal static string Fmt(float v)
        {
            var inv = CultureInfo.InvariantCulture;
            var abs = Mathf.Abs(v);
            if (abs >= 1e6f) return (v / 1e6f).ToString(abs >= 1e8f ? "0" : "0.#", inv) + "M";
            if (abs >= 1000f) return (v / 1000f).ToString(abs >= 1e5f ? "0" : "0.#", inv) + "K";
            return v.ToString(abs < 10f ? "0.#" : "0", inv);
        }

        internal static string Clock(float seconds)
        {
            var s = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return s >= 3600
                ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}"
                : $"{s / 60}:{s % 60:00}";
        }

        private static float NiceCeiling(float v)
        {
            var exp = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(v)));
            var f = v / exp;
            var nice = f <= 1f ? 1f : f <= 2f ? 2f : f <= 2.5f ? 2.5f : f <= 5f ? 5f : 10f;
            return nice * exp;
        }
    }
}
