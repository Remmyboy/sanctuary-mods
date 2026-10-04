using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using SanctuaryUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // The unit information card: what the game shows for the selected (or
    // hovered) unit, bottom left between the selection list and the orders
    // panel. The game's card is a bitmap mock-up with fifteen text fields
    // laid over it and a portrait behind: template id beside the name,
    // income to three decimal places, and no telling which figure is which
    // without learning the picture.
    //
    // With this on, the game's card is made invisible (PanelConceal) and a
    // plainer one stands in its place on the HUD canvas, from the same values — the postfix on
    // InformationPanelUI.SetValues catches every update Lua sends — with
    // labelled rows, a health bar, figures rounded and coloured by resource,
    // and only the rows that apply to this unit. No portrait: with ten units
    // selected the card is about one of them, and the selection list above
    // already shows what is selected. The card keeps to the height of the
    // game's own, so it never grows up into that list. With it off, the
    // game's own card can still be tidied: template id hidden, income rounded.
    internal static class InfoCard
    {
        internal static ConfigEntry<bool> Enabled, TidyBuiltIn;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("BottomPanels", "UnitCard", true,
                "Replace the game's unit information card with a plainer one drawn from the same values: name, health, shields, " +
                "build cost, income and build power in labelled rows, figures rounded, template id left off. " +
                "The game's card comes back whenever the overlay is hidden or the mod is unloaded.");
            TidyBuiltIn = config.Bind("BottomPanels", "TidyGameUnitCard", true,
                "With the game's own card kept (UnitCard off): hide the unit's template id and round its income figures.");
        }

        // ---- the values ---------------------------------------------------------

        private static UIInformationValues _values;
        private static bool _haveValues;
        private static InformationPanelUI _source;
        private static string _name = "", _display = "";

        internal static void ApplyPatch(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(InformationPanelUI), nameof(InformationPanelUI.SetValues));
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(InfoCard), nameof(ValuesPostfix)));
        }

        private static void ValuesPostfix(InformationPanelUI __instance, object[] __args)
        {
            var box = __args?.FirstOrDefault(a => a is UIInformationValues);
            if (box == null || __instance == null) return;
            _values = (UIInformationValues)box;
            _haveValues = true;
            _source = __instance;
            // The strings in the struct point into Lua's memory; the panel's
            // own text fields hold managed copies, taken here while they're
            // fresh.
            _name = Text(__instance.unitName);
            _display = Text(__instance.unitDisplayName);
            if (_display == _name) _display = "";

            if (Enabled != null && TidyBuiltIn != null && !Enabled.Value && TidyBuiltIn.Value) Tidy(__instance, _values);
        }

        private static string Text(TMP_Text text) => text == null ? "" : (text.text ?? "").Trim();

        // ---- tidying the game's own card ---------------------------------------

        private static GameObject _hiddenId;

        private static void Tidy(InformationPanelUI panel, UIInformationValues v)
        {
            try
            {
                if (panel.unitTemplateId != null && panel.unitTemplateId.gameObject.activeSelf)
                {
                    panel.unitTemplateId.gameObject.SetActive(false);
                    _hiddenId = panel.unitTemplateId.gameObject;
                }
                SetRounded(panel.alloyNetIncome, v.alloyNetIncome);
                SetRounded(panel.energyNetIncome, v.energyNetIncome);
            }
            catch (Exception e)
            {
                // Cosmetic: the game's card stays as it drew it.
                if (!_tidyLogged)
                {
                    _tidyLogged = true;
                    _log?.LogWarning($"Unit card: the game's card could not be tidied (logged once): {e.Message}");
                }
            }
        }

        /// The game's figure to three decimals, made whole: the sign element
        /// keeps the sign, and a figure that rounds to nothing shows as 0.
        private static void SetRounded(SignedTextElement element, float value)
        {
            if (element == null || element.text == null) return;
            var whole = Mathf.Abs(value) < 0.5f;
            if (element.sign != null) element.sign.text = whole ? "" : value > 0f ? "+" : "-";
            element.text.text = whole ? "0" : SanctuaryHudPlugin.Fmt(value);
        }

        private static void RestoreBuiltIn()
        {
            if (_hiddenId != null) _hiddenId.SetActive(true);
            _hiddenId = null;
        }

        // ---- the build card -------------------------------------------------------
        //
        // With a build option under the mouse the game fills the card with
        // the template's figures, but nothing in them says so. The build
        // strip tells this which template it is, and a small Lua query works
        // out how long the selected builders would take: the template's
        // buildTime over their build power — summed for engineers assisting
        // one job, but a factory builds alone, so the biggest one selected.

        private static string _hoverTemplate;
        private static float _hoverSeconds;
        private static float _hoverRefresh;
        private static bool _queryLogged, _peekLogged, _tidyLogged;

        /// The template of the build option under the mouse, or null.
        internal static void SetHover(string template)
        {
            if (template == _hoverTemplate)
            {
                // The selection can change under a held hover.
                if (template != null && Time.realtimeSinceStartup >= _hoverRefresh) QueryTime();
                return;
            }
            _hoverTemplate = template;
            if (template != null) QueryTime();
        }

        private static void QueryTime()
        {
            _hoverRefresh = Time.realtimeSinceStartup + 1f;
            _hoverSeconds = 0f;
            var id = _hoverTemplate;
            if (string.IsNullOrEmpty(id) || !id.All(char.IsLetterOrDigit)) return;
            try
            {
                Queries.Tick();
                if (!Queries.Call($"__SdbCard.BuildTime('{id}')")) return;
                var raw = GetLuaGlobal("__SdbBuildSecs");
                if (string.IsNullOrEmpty(raw)) return;
                var comma = raw.IndexOf(',');
                float.TryParse(comma >= 0 ? raw.Substring(0, comma) : raw, NumberStyles.Float, Inv, out _hoverSeconds);
            }
            catch (Exception e)
            {
                // The card shows the cost without a time.
                if (!_queryLogged)
                {
                    _queryLogged = true;
                    _log?.LogWarning($"Unit card: build time query failed (logged once): {e.Message}");
                }
            }
        }

        private static string Duration(float seconds)
        {
            if (seconds < 60f) return seconds.ToString("0", Inv) + "s";
            var m = Mathf.FloorToInt(seconds / 60f);
            var s = Mathf.RoundToInt(seconds - m * 60f);
            if (s == 60) { m++; s = 0; }
            return m.ToString(Inv) + ":" + s.ToString("00", Inv);
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The card's two queries as functions, installed once per VM: the
        // peek runs four times a second while the card is up.
        private static readonly LuaHook Queries = new LuaHook("__SdbCard", "unit card queries", CardChunk)
        {
            LogInstalls = false,
        };

        private const string CardChunk =
            "__SdbCard = { " +
            "Peek = function() " + PeekBody + " end, " +
            // The selected builders' time for a template: its buildTime over
            // their build power, summed for mobile ones, the biggest of the rest.
            "BuildTime = function(id) " +
            "local ok = pcall(function() " +
            "  local tp = __Templates.Units[id] " +
            "  local bt = tp and tp.economy and tp.economy.buildTime or 0 " +
            "  local sel = Import('client/input/selectionSystem.lua') " +
            "  local picked = (sel.GetSelectedUnits and sel.GetSelectedUnits()) " +
            "    or (sel.GetSelectedEntities and sel.GetSelectedEntities()) or {} " +
            "  local mobile, fixed = 0, 0 " +
            "  for _, u in pairs(picked) do " +
            "    local p = u.buildPower or (u.tp and u.tp.construction and u.tp.construction.buildPower) or 0 " +
            "    if u.tp and u.tp.movement then mobile = mobile + p else fixed = math.max(fixed, p) end " +
            "  end " +
            "  local power = mobile > 0 and mobile or fixed " +
            "  local secs = power > 0 and bt / power or bt " +
            "  __SdbBuildSecs = string.format('%.1f,%.1f', secs, power) " +
            "end) " +
            "if not ok then __SdbBuildSecs = '' end " +
            "end }";

        // ---- a factory's queue and progress ----------------------------------------
        //
        // A factory under the mouse (or the one factory selected) shows what
        // it is building: the first five items of its queue as small tiles,
        // and how far the current one has got — as FA's card does. The
        // game's card has none of this; it comes from a small Lua query,
        // four times a second while the card is up: the hovered unit's
        // predictedBuildQueue, and its buildTarget's progress over the
        // target's buildTime.

        private sealed class QueueItem
        {
            public int Count;
            public uint Icon, Plate;
        }

        private static readonly List<QueueItem> _queue = new List<QueueItem>();
        private static float _factoryProgress = -1f;
        private static float _shieldCur, _shieldMax;
        private static float _shieldRecharge = -1f;
        private static bool _hoveringUnit;
        private static float _nextPeek;

        private const string PeekBody =
            "local ok = pcall(function() " +
            "  local m = Import('client/inputEventsFunctions.lua') " +
            "  local u = m.GetHoverUnit and m.GetHoverUnit() " +
            "  local hovering = (u and u.tp) and 1 or 0 " +
            "  if not (u and u.tp) then " +
            "    local sel = Import('client/input/selectionSystem.lua') " +
            "    local picked = (sel.GetSelectedUnits and sel.GetSelectedUnits()) " +
            "      or (sel.GetSelectedEntities and sel.GetSelectedEntities()) or {} " +
            "    local only, n = nil, 0 " +
            "    for _, e in pairs(picked) do only = e n = n + 1 end " +
            "    if n == 1 then u = only end " +
            "  end " +
            // Any builder: a factory, or an engineer (whose queue is the
            // structures it has been told to put up, and whose target may be
            // something else entirely when it is assisting).
            // The unit's shield, summed over its shields: the game's card
            // values never carry one (informationPanel.lua sends false).
            // ...and, while a shield is coming up, how far along its recharge
            // is (the client tracks the recharge in ticks; -1 when it is not).
            "  local sh, shm, rp = 0, 0, -1 " +
            "  if u and u.shields then " +
            "    for _, s in ipairs(u.shields) do " +
            "      sh = sh + (tonumber(s:GetHealth()) or 0) " +
            "      shm = shm + (tonumber(s:GetMaxHealth()) or 0) " +
            "      if s.GetRechargeProgress then " +
            "        local p, d = s:GetRechargeProgress() " +
            "        if p and d and d > 0 then rp = math.max(rp, p / d) end " +
            "      end " +
            "    end " +
            "  end " +
            "  local shield = string.format('%.0f:%.0f:%.3f', sh, shm, rp) " +
            "  if not (u and u.tp and u.tp.construction) then __SdbFactory = hovering .. '||||' .. shield return end " +
            "  local function art(tpId) " +
            "    local t = tpId and __Templates.Units[tpId] " +
            "    local g = t and t.general " +
            "    return (g and g.foregroundIconID and tonumber(g.foregroundIconID.index) or 0) .. ':' .. " +
            "           (g and g.backgroundIconID and tonumber(g.backgroundIconID.index) or 0) " +
            "  end " +
            "  local out = {} " +
            "  for i, item in ipairs(u.predictedBuildQueue or {}) do " +
            "    if i > 5 then break end " +
            "    out[#out + 1] = (tonumber(item.count) or 0) .. ':' .. art(item.tpId) " +
            "  end " +
            "  local prog, target = -1, '' " +
            "  local t = u.buildTarget " +
            "  if t and t.tp and t.tp.economy and tonumber(t.tp.economy.buildTime) and tonumber(t.progress) then " +
            "    prog = tonumber(t.progress) / tonumber(t.tp.economy.buildTime) " +
            "    target = art(t.tpId) " +
            "  end " +
            "  __SdbFactory = hovering .. '|' .. string.format('%.3f', prog) .. '|' .. target .. '|' .. table.concat(out, ';') .. '|' .. shield " +
            "end) " +
            "if not ok then __SdbFactory = '' end";

        private static void Peek()
        {
            if (Time.realtimeSinceStartup < _nextPeek) return;
            _nextPeek = Time.realtimeSinceStartup + 0.25f;
            try
            {
                Queries.Tick();
                if (!Queries.Call("__SdbCard.Peek()")) return;
                var raw = GetLuaGlobal("__SdbFactory") ?? "";
                var parts = raw.Split('|');
                _hoveringUnit = parts.Length > 0 && parts[0] == "1";
                _shieldCur = _shieldMax = 0f;
                _shieldRecharge = -1f;
                if (parts.Length >= 5)
                {
                    var sp = parts[4].Split(':');
                    if (sp.Length >= 3) float.TryParse(sp[2], NumberStyles.Float, Inv, out _shieldRecharge);
                    if (sp.Length >= 2)
                    {
                        float.TryParse(sp[0], NumberStyles.Float, Inv, out _shieldCur);
                        float.TryParse(sp[1], NumberStyles.Float, Inv, out _shieldMax);
                    }
                }
                _queue.Clear();
                _factoryProgress = -1f;
                if (parts.Length < 4) return;
                float.TryParse(parts[1], NumberStyles.Float, Inv, out _factoryProgress);
                // What it is working on right now, first — its own next
                // item for a factory, which then merges with the queue's
                // head; something else's job for an assisting engineer.
                var target = parts[2].Split(':');
                uint targetIcon = 0, targetPlate = 0;
                if (target.Length >= 2)
                {
                    uint.TryParse(target[0], out targetIcon);
                    uint.TryParse(target[1], out targetPlate);
                }
                foreach (var item in parts[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = item.Split(':');
                    if (f.Length < 3) continue;
                    int.TryParse(f[0], out var count);
                    uint.TryParse(f[1], out var icon);
                    uint.TryParse(f[2], out var plate);
                    _queue.Add(new QueueItem { Count = count, Icon = icon, Plate = plate });
                }
                if (targetIcon != 0 && (_queue.Count == 0 || _queue[0].Icon != targetIcon))
                {
                    _queue.Insert(0, new QueueItem { Count = 1, Icon = targetIcon, Plate = targetPlate });
                    if (_queue.Count > 5) _queue.RemoveAt(5);
                }
            }
            catch (Exception e)
            {
                // The card shows without the queue.
                if (!_peekLogged)
                {
                    _peekLogged = true;
                    _log?.LogWarning($"Unit card: queue and shield query failed (logged once): {e.Message}");
                }
            }
        }

        private static void ForgetPeek()
        {
            _queue.Clear();
            _factoryProgress = -1f;
            _shieldCur = _shieldMax = 0f;
            _shieldRecharge = -1f;
            _hoveringUnit = false;
        }

        // ---- the game's panel ---------------------------------------------------

        private static readonly PanelConceal _conceal = new PanelConceal();

        private static InformationPanelUI FindPanel()
        {
            try
            {
                var ui = SanctuaryUIManager.Instance;
                if (ui == null) return null;
                return ui.TryGetPanel(UIPanelType.Information, out var panel) ? panel as InformationPanelUI : null;
            }
            catch
            {
                return null;
            }
        }

        /// From Update. Conceals the game's card while the replacement stands
        /// in for it, and gives it back otherwise.
        internal static void Tick(bool hudShowing)
        {
            var replace = hudShowing && InMatch && Enabled.Value && !PanelConceal.Unavailable;
            var panel = replace ? FindPanel() : null;
            if (_conceal.Apply(panel)) Describe(panel);
            if (Enabled.Value || !TidyBuiltIn.Value) RestoreBuiltIn();
            if (panel != null && panel.IsVisible) Peek();
            else ForgetPeek();
            try
            {
                SyncCard(panel);
            }
            catch (Exception e)
            {
                if (!_syncLogged)
                {
                    _syncLogged = true;
                    _log?.LogWarning($"Unit card could not be laid out (logged once): {e}");
                }
                _card?.Show(false);
            }
        }

        internal static void Shutdown()
        {
            _conceal.Release();
            RestoreBuiltIn();
            Queries.Remove();
            _card?.Destroy();
            _card = null;
        }

        // ---- the card ----------------------------------------------------------------
        //
        // As wide as the game's own card (528 canvas units) and no wider —
        // the left column must not change width with the selection — so a
        // build queue shares the usage band at three small tiles; as tall as
        // its rows. Built once
        // on the HUD canvas (Card) and filled every frame from the values,
        // rows switched on and off as they apply.

        private const float Width = 528f;
        private const float Pad = BottomDock.Pad;
        private const float GaugeHeight = 52f;
        private const float QueueTile = 36f;
        private const float JobTile = 80f;
        private const float MarkSize = 24f;
        private const float LineHeight = 30f;

        private static readonly Color LabelColour = new Color(0.85f, 0.90f, 0.97f);
        private static readonly Color SubtitleColour = new Color(0.80f, 0.87f, 0.96f);

        private static Card _card;
        private static bool _syncLogged;

        /// From Update, after the conceal and the peek: shows the card
        /// where the game's sits, or hides it.
        private static void SyncCard(InformationPanelUI panel)
        {
            if (panel == null || !panel.IsVisible || !_haveValues || _source != panel)
            {
                _card?.Show(false);
                return;
            }
            // A build option under the mouse makes this a build card: what
            // it costs and how long the selected builders would take. That
            // shows whatever is selected. Otherwise, with a group selected
            // the game's card describes one unit of it, whichever came
            // first, which says nothing about the group; the selection row
            // carries what is selected.
            var building = _hoverTemplate != null;
            if (!building && !_hoveringUnit && SelectionRow.CountSelected() > 1)
            {
                _card?.Show(false);
                return;
            }
            var root = HudCanvas.Ensure(panel);
            if (root == null) return;
            if (_card == null || !_card.Alive)
            {
                HudCanvas.TakeFont(panel.unitName);
                _card = Card.Create(root);
            }

            _card.Show(true);
            _card.Fill(_values, building, BottomDock.Scale);
            // On top of the orders row, in the left column — and on top of
            // where it would be when nothing is selected (a hovered unit has
            // no orders row), so the card never moves.
            var scale = BottomDock.Scale;
            var ordersHeight = BottomDock.OrdersHeight > 0f ? BottomDock.OrdersHeight : BottomDock.RowHeight * scale;
            var at = new Vector2(BottomDock.Origin.x, BottomDock.Origin.y + ordersHeight);
            _card.Place(at);
            BottomDock.Column(_card.PlacedWidth);
            BottomDock.Add(new Rect(at.x, at.y, _card.PlacedWidth, _card.PlacedHeight), _card.Rect);
        }

        private static string Rate(float value) => (value > 0f ? "+" : "−") + SanctuaryHudPlugin.Fmt(value) + "/s";

        private static float _extrasVeterancy = float.NaN, _extrasTransport, _extrasAmmo;
        private static string _extras;

        /// The occasional figures, one line, only the ones that apply; made
        /// again only when one of them changes.
        private static string Extras(UIInformationValues v)
        {
            if (v.veterancy.Equals(_extrasVeterancy) && v.transportCapacity.Equals(_extrasTransport) && v.ammoCapacity.Equals(_extrasAmmo))
                return _extras;
            _extrasVeterancy = v.veterancy;
            _extrasTransport = v.transportCapacity;
            _extrasAmmo = v.ammoCapacity;
            string text = null;
            void Add(string part) => text = text == null ? part : text + "    " + part;
            if (v.veterancy > 0f) Add("VETERANCY " + SanctuaryHudPlugin.Fmt(v.veterancy));
            if (v.transportCapacity > 0f) Add("TRANSPORT " + SanctuaryHudPlugin.Fmt(v.transportCapacity));
            if (v.ammoCapacity > 0f) Add("AMMO " + SanctuaryHudPlugin.Fmt(v.ammoCapacity));
            return _extras = text;
        }

        /// SetActive that tells the panels a layout changed, when it did.
        private static void Active(GameObject go, bool on)
        {
            if (go == null || go.activeSelf == on) return;
            go.SetActive(on);
            HudCanvas.LayoutVersion++;
        }

        /// The card's objects: a plate with a column of rows.
        private sealed class Card
        {
            private RectTransform _rect;
            private TMP_Text _title, _aside;
            private readonly Gauge[] _gauges = new Gauge[5];   // shield, health, armour, bubble, building
            private FigureLine _build, _income, _power;
            private GameObject _band;
            private RectTransform _left;
            private GameObject _queueBlock;
            private readonly QueueTileView[] _small = new QueueTileView[2];
            private QueueTileView _job;
            private TMP_Text _jobPercent;
            // Each figure's text, made again only when its value changes.
            private readonly SanctuaryHudPlugin.FigureText _alloyCost = new SanctuaryHudPlugin.FigureText(),
                _energyCost = new SanctuaryHudPlugin.FigureText(), _time = new SanctuaryHudPlugin.FigureText(),
                _alloyRate = new SanctuaryHudPlugin.FigureText(), _energyRate = new SanctuaryHudPlugin.FigureText(),
                _buildPower = new SanctuaryHudPlugin.FigureText(), _percent = new SanctuaryHudPlugin.FigureText();

            /// Lay out again on the next Fill even if nothing in it changed:
            /// it was just shown, or its size changed.
            private bool _dirty = true;
            private float _scale = -1f;

            internal bool Alive => _rect != null;
            internal RectTransform Rect => _rect;
            internal float PlacedWidth => _rect != null ? _rect.rect.width * _rect.localScale.x : 0f;
            internal float PlacedHeight => _rect != null ? _rect.rect.height * _rect.localScale.y : 0f;

            internal static Card Create(RectTransform root)
            {
                var card = new Card();
                var rt = HudCanvas.Plate(root, "Unit card");
                card._rect = rt;
                var column = rt.gameObject.AddComponent<VerticalLayoutGroup>();
                column.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)(Pad - 4f));
                column.spacing = 2f;
                column.childAlignment = TextAnchor.UpperLeft;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                rt.sizeDelta = new Vector2(Width, 100f);

                // The class of thing it is on the left as the title ("Tier 3:
                // Tank" is what you act on), its given name after it.
                var titleRow = Row(rt, "Title", 16f, TextAnchor.MiddleLeft);
                card._title = HudCanvas.Text(titleRow, "Class", 30f, Color.white, TextAlignmentOptions.MidlineLeft);
                card._aside = HudCanvas.Text(titleRow, "Name", 24f, SubtitleColour, TextAlignmentOptions.MidlineLeft);
                card._aside.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

                var names = new[] { "Shield", "Health", "Armour", "Bubble shield", "Building" };
                for (var i = 0; i < card._gauges.Length; i++) card._gauges[i] = Gauge.Create(rt, names[i]);

                // The usage rows share a band with the current job on the
                // right: its art, large, the percentage under it, and the
                // rest of the queue as small tiles beside it.
                var band = Row(rt, "Band", 8f, TextAnchor.UpperLeft);
                card._band = band.gameObject;
                var left = new GameObject("Usage", typeof(RectTransform));
                left.transform.SetParent(band, false);
                var leftGroup = left.AddComponent<VerticalLayoutGroup>();
                leftGroup.spacing = 2f;
                leftGroup.childAlignment = TextAnchor.UpperLeft;
                leftGroup.childControlWidth = true;
                leftGroup.childControlHeight = true;
                leftGroup.childForceExpandWidth = false;
                leftGroup.childForceExpandHeight = false;
                left.AddComponent<LayoutElement>().flexibleWidth = 1f;
                card._left = (RectTransform)left.transform;
                // The build card's line — alloy, energy, time, each behind its
                // mark — takes the place of the usage lines while a build
                // option is hovered. Every line keeps its height when it has
                // nothing to say, and the band keeps the height a build queue
                // needs, so the card stays one size whatever is selected.
                card._build = FigureLine.Create(card._left, "Build", 3, false);
                card._build.Reserve(LineHeight);
                // Income, spend and build power on one line; the rarer figures
                // as words on a line under it, only when there are any.
                card._income = FigureLine.Create(card._left, "Income", 3, false);
                card._income.Reserve(LineHeight);
                card._power = FigureLine.Create(card._left, "Extras", 0, true);
                band.gameObject.AddComponent<LayoutElement>().minHeight = JobTile + 24f;

                var queue = Row(band, "Queue", 6f, TextAnchor.UpperLeft);
                card._queueBlock = queue.gameObject;
                for (var i = 0; i < card._small.Length; i++) card._small[i] = QueueTileView.Create(queue, "Queued", QueueTile, true);
                var jobColumn = new GameObject("Job", typeof(RectTransform));
                jobColumn.transform.SetParent(queue, false);
                var jobGroup = jobColumn.AddComponent<VerticalLayoutGroup>();
                jobGroup.spacing = 2f;
                jobGroup.childAlignment = TextAnchor.UpperCenter;
                jobGroup.childControlWidth = true;
                jobGroup.childControlHeight = true;
                jobGroup.childForceExpandWidth = false;
                jobGroup.childForceExpandHeight = false;
                card._job = QueueTileView.Create(jobColumn.transform, "Current", JobTile, false);
                card._jobPercent = HudCanvas.Text(jobColumn.transform, "Percent", 22f, Color.white, TextAlignmentOptions.Center);

                rt.gameObject.SetActive(false);
                return card;
            }

            /// A horizontal row of the column.
            private static RectTransform Row(Transform parent, string name, float spacing, TextAnchor alignment)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var group = go.AddComponent<HorizontalLayoutGroup>();
                group.spacing = spacing;
                group.childAlignment = alignment;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                return (RectTransform)go.transform;
            }

            internal void Show(bool showing)
            {
                if (_rect == null || _rect.gameObject.activeSelf == showing) return;
                _rect.gameObject.SetActive(showing);
                if (showing) _dirty = true;
            }

            internal void Place(Vector2 bottomLeft)
            {
                if (_rect != null) _rect.anchoredPosition = bottomLeft;
            }

            internal void Destroy()
            {
                if (_rect != null) UnityEngine.Object.Destroy(_rect.gameObject);
                _rect = null;
            }

            internal void Fill(UIInformationValues v, bool building, float scale)
            {
                // Anything below that changes the card's size bumps the
                // layout version (SetText, Active), so an unchanged card is
                // not laid out again.
                var version = HudCanvas.LayoutVersion;
                if (scale != _scale)
                {
                    _scale = scale;
                    _rect.localScale = new Vector3(scale, scale, 1f);
                    _dirty = true;
                }
                if (_dirty) HudCanvas.PlateStyle(_rect, false);
                var factory = _queue.Count > 0;
                if (Mathf.Abs(_rect.sizeDelta.x - Width) > 0.5f) _rect.sizeDelta = new Vector2(Width, _rect.sizeDelta.y);

                var title = _display.Length > 0 ? _display : _name;
                var aside = _display.Length > 0 ? _name : "";
                HudCanvas.SetText(_title, title);
                HudCanvas.SetText(_aside, aside);
                Active(_aside.gameObject, aside.Length > 0);

                var showArmour = v.isArmourShieldEnabled && v.armourShieldMax > 0f;
                var showBubble = v.isBubbleShieldEnabled && v.bubbleShieldMax > 0f;
                var showShield = !showBubble && _shieldMax > 0f;
                var showHealth = v.healthMax > 0f;

                // The shield as the client sees it, read with the queue poll,
                // above the health: it is what takes the hits first. While it
                // is coming up the bar is the recharge instead, at which point
                // the game shows 1 / 10,000 and the figure means nothing.
                if (showShield && _shieldRecharge >= 0f && _shieldRecharge < 1f)
                {
                    _gauges[0].SetPercent("SHIELD CHARGING", Mathf.Clamp01(_shieldRecharge), new Color(0.55f, 0.78f, 1f, 0.55f));
                }
                else if (showShield)
                {
                    _gauges[0].Set("SHIELD", _shieldCur, _shieldMax, 0f, UpgradeColour);
                }
                _gauges[0].Show(showShield);

                if (showHealth)
                {
                    var frac = Mathf.Clamp01(v.healthValue / v.healthMax);
                    var colour = frac > 0.6f ? GainColour : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour;
                    _gauges[1].Set("HEALTH", v.healthValue, v.healthMax, v.healthRegen, colour);
                }
                _gauges[1].Show(showHealth);
                if (showArmour) _gauges[2].Set("ARMOUR", v.armourShieldValue, v.armourShieldMax, v.armourShieldRegen, UpgradeColour);
                _gauges[2].Show(showArmour);
                if (showBubble) _gauges[3].Set("SHIELD", v.bubbleShieldValue, v.bubbleShieldMax, v.bubbleShieldRegen, UpgradeColour);
                _gauges[3].Show(showBubble);
                if (v.isConstructionPercentEnabled)
                {
                    _gauges[4].SetPercent("BUILDING", Mathf.Clamp01(v.constructionPercent), UpgradeColour);
                }
                _gauges[4].Show(v.isConstructionPercentEnabled);

                // The build card's line: cost and time. Otherwise what the
                // unit adds to or takes from the economy, per second, behind
                // the same marks, only where it is not zero (a generator
                // makes energy, not alloy); no build cost, since it is paid
                // by the time this card is about a unit.
                if (building)
                {
                    _build.Set(0, "alloy", SanctuaryHudPlugin.AlloyTint, _alloyCost.Fmt(v.alloyBuildCost));
                    _build.Set(1, "energy", SanctuaryHudPlugin.EnergyTint, _energyCost.Fmt(v.energyBuildCost));
                    _build.Set(2, "time", Color.white, _time.Get(_hoverSeconds, s => s > 0f ? Duration(s) : "—"));
                }
                _build.Show(building);

                var alloy = Mathf.Abs(v.alloyNetIncome) >= 0.5f;
                var energy = Mathf.Abs(v.energyNetIncome) >= 0.5f;
                if (alloy) _income.Set(0, "alloy", SanctuaryHudPlugin.AlloyTint, _alloyRate.Get(v.alloyNetIncome, f => Rate(f)));
                else _income.Clear(0);
                if (energy) _income.Set(1, "energy", SanctuaryHudPlugin.EnergyTint, _energyRate.Get(v.energyNetIncome, f => Rate(f)));
                else _income.Clear(1);
                // Build power behind its mark, on the same line.
                if (v.buildPower > 0f) _income.Set(2, "power", LabelColour, _buildPower.Fmt(v.buildPower));
                else _income.Clear(2);
                _income.Show(!building);

                var extras = Extras(v);
                _power.SetExtras(extras);
                _power.Show(!building && extras != null);

                // The current job, large, at the right edge of the band, the
                // percentage under it; whatever is queued behind it as small
                // tiles to its left, counts in their corners, nearest first.
                if (factory)
                {
                    _job.Set(_queue[0], false);
                    var pct = _percent.Get(_factoryProgress, p => p >= 0f ? (Mathf.Clamp01(p) * 100f).ToString("0", Inv) + "%" : "");
                    HudCanvas.SetText(_jobPercent, pct);
                    Active(_jobPercent.gameObject, pct.Length > 0);
                    var queued = Mathf.Min(_queue.Count - 1, _small.Length);
                    for (var i = 0; i < _small.Length; i++)
                    {
                        // Left to right: the furthest first, the next up beside the job.
                        var at = queued - i;
                        var on = i < queued;
                        if (on) _small[i].Set(_queue[at], true);
                        _small[i].Show(on);
                    }
                }
                Active(_queueBlock, factory);
                Active(_band, true);
                // So the height is right for the dock this frame, on the
                // frames something in it changed.
                if (_dirty || HudCanvas.LayoutVersion != version)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
                    _dirty = false;
                }
            }
        }

        /// A labelled gauge: label left, "value / max" right, regen beside
        /// the value when there is any, and the bar underneath.
        private sealed class Gauge
        {
            private GameObject _go;
            private TMP_Text _label, _value, _regen;
            private Image _fill;

            internal static Gauge Create(Transform parent, string name)
            {
                var gauge = new Gauge();
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                gauge._go = go;
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.spacing = 2f;
                column.childAlignment = TextAnchor.UpperLeft;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                var layout = go.AddComponent<LayoutElement>();
                layout.minHeight = GaugeHeight;
                layout.preferredHeight = GaugeHeight;

                var texts = new GameObject("Texts", typeof(RectTransform));
                texts.transform.SetParent(go.transform, false);
                var row = texts.AddComponent<HorizontalLayoutGroup>();
                row.spacing = 12f;
                row.childAlignment = TextAnchor.MiddleLeft;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = false;
                row.childForceExpandHeight = false;
                gauge._label = HudCanvas.Text(texts.transform, "Label", 22f, LabelColour, TextAlignmentOptions.MidlineLeft);
                gauge._label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                gauge._regen = HudCanvas.Text(texts.transform, "Regen", 22f, GainColour, TextAlignmentOptions.MidlineRight);
                gauge._value = HudCanvas.Text(texts.transform, "Value", 26f, Color.white, TextAlignmentOptions.MidlineRight);

                var track = AccentColour;
                track.a = 0.14f;
                var bar = HudCanvas.Fill(go.transform, "Bar", track);
                bar.gameObject.AddComponent<LayoutElement>().preferredHeight = 8f;
                gauge._fill = HudCanvas.Fill(bar.transform, "Fill", Color.white);
                gauge._fill.sprite = HudStyle.BarShade;
                gauge._fill.type = Image.Type.Filled;
                gauge._fill.fillMethod = Image.FillMethod.Horizontal;
                gauge._fill.fillOrigin = 0;
                var frt = gauge._fill.rectTransform;
                frt.anchorMin = Vector2.zero;
                frt.anchorMax = Vector2.one;
                frt.offsetMin = Vector2.zero;
                frt.offsetMax = Vector2.zero;
                go.SetActive(false);
                return gauge;
            }

            internal void Show(bool showing) => Active(_go, showing);

            // The figures as last written, so they are only made again when
            // they change.
            private float _shownValue = float.NaN, _shownMax = float.NaN, _shownRegen = float.NaN, _shownPercent = float.NaN;
            private string _valueText, _regenText;

            internal void Set(string label, float value, float max, float regen, Color colour)
            {
                if (_valueText == null || !value.Equals(_shownValue) || !max.Equals(_shownMax) || !float.IsNaN(_shownPercent))
                {
                    _shownValue = value;
                    _shownMax = max;
                    _shownPercent = float.NaN;
                    _valueText = SanctuaryHudPlugin.Fmt(value) + " / " + SanctuaryHudPlugin.Fmt(max);
                }
                if (!regen.Equals(_shownRegen))
                {
                    _shownRegen = regen;
                    _regenText = regen >= 0.5f ? "+" + SanctuaryHudPlugin.Fmt(regen) + "/s" : null;
                }
                var frac = max > 0f ? Mathf.Clamp01(value / max) : 0f;
                Set(label, _valueText, _regenText, frac, colour);
            }

            /// A bar that is a percentage (charging, building): the figure
            /// is the percentage, with no regen.
            internal void SetPercent(string label, float frac, Color colour)
            {
                var percent = frac * 100f;
                if (_valueText == null || !percent.Equals(_shownPercent))
                {
                    _shownPercent = percent;
                    _shownValue = _shownMax = float.NaN;
                    _valueText = percent.ToString("0", Inv) + "%";
                }
                Set(label, _valueText, null, frac, colour);
            }

            private void Set(string label, string value, string regen, float frac, Color colour)
            {
                HudCanvas.SetText(_label, label);
                HudCanvas.SetText(_value, value);
                HudCanvas.SetText(_regen, regen ?? "");
                Active(_regen.gameObject, !string.IsNullOrEmpty(regen));
                _fill.fillAmount = frac;
                _fill.color = colour;
            }
        }

        /// A line of figures, each behind a mark (one of the HUD's glyphs)
        /// in its tint; optionally a run of words after them.
        private sealed class FigureLine
        {
            private GameObject _go;
            private RawImage[] _marks;
            private TMP_Text[] _icons;
            private string[] _keys;
            private TMP_Text[] _figures;
            private GameObject[] _items;
            private TMP_Text _extras;

            internal static FigureLine Create(Transform parent, string name, int count, bool withExtras)
            {
                var line = new FigureLine();
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                line._go = go;
                var row = go.AddComponent<HorizontalLayoutGroup>();
                row.spacing = 16f;
                row.childAlignment = TextAnchor.MiddleLeft;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = false;
                row.childForceExpandHeight = false;
                line._marks = new RawImage[count];
                line._icons = new TMP_Text[count];
                line._keys = new string[count];
                line._figures = new TMP_Text[count];
                line._items = new GameObject[count];
                for (var i = 0; i < count; i++)
                {
                    var item = new GameObject("Figure", typeof(RectTransform));
                    item.transform.SetParent(go.transform, false);
                    var pair = item.AddComponent<HorizontalLayoutGroup>();
                    pair.spacing = 6f;
                    pair.childAlignment = TextAnchor.MiddleLeft;
                    pair.childControlWidth = true;
                    pair.childControlHeight = true;
                    pair.childForceExpandWidth = false;
                    pair.childForceExpandHeight = false;
                    var mark = new GameObject("Mark", typeof(RectTransform));
                    mark.transform.SetParent(item.transform, false);
                    var image = mark.AddComponent<RawImage>();
                    image.raycastTarget = false;
                    var markLayout = mark.AddComponent<LayoutElement>();
                    markLayout.preferredWidth = MarkSize;
                    markLayout.preferredHeight = MarkSize;
                    markLayout.minWidth = MarkSize;
                    markLayout.minHeight = MarkSize;
                    line._marks[i] = image;
                    line._figures[i] = HudCanvas.Text(item.transform, "Text", 22f, Color.white, TextAlignmentOptions.MidlineLeft);
                    line._items[i] = item;
                    item.SetActive(false);
                }
                if (withExtras)
                {
                    line._extras = HudCanvas.Text(go.transform, "Extras", 20f, LabelColour, TextAlignmentOptions.MidlineLeft);
                    line._extras.gameObject.SetActive(false);
                }
                go.SetActive(false);
                return line;
            }

            internal void Show(bool showing) => Active(_go, showing);

            /// Keeps this height even with nothing on the line.
            internal void Reserve(float height)
            {
                var layout = _go.GetComponent<LayoutElement>();
                if (layout == null) layout = _go.AddComponent<LayoutElement>();
                layout.minHeight = height;
            }

            internal void Set(int index, string glyph, Color tint, string text)
            {
                // The game's own icon for the figure where it has one, made
                // once per mark; the HUD's glyph behind the rest.
                if (_keys[index] != glyph)
                {
                    _keys[index] = glyph;
                    if (_icons[index] != null) UnityEngine.Object.Destroy(_icons[index].gameObject);
                    _icons[index] = HudCanvas.Icon(_items[index].transform, glyph, MarkSize, tint);
                    if (_icons[index] != null) _icons[index].transform.SetSiblingIndex(0);
                }
                if (_icons[index] != null)
                {
                    _icons[index].color = tint;
                    Active(_marks[index].gameObject, false);
                }
                else
                {
                    var mark = Glyphs.Get(glyph);
                    _marks[index].texture = mark;
                    _marks[index].color = tint;
                    Active(_marks[index].gameObject, mark != null);
                }
                _figures[index].color = tint;
                HudCanvas.SetText(_figures[index], text);
                Active(_items[index], true);
            }

            internal void Clear(int index) => Active(_items[index], false);

            internal void SetExtras(string text)
            {
                if (_extras == null) return;
                HudCanvas.SetText(_extras, text ?? "");
                Active(_extras.gameObject, !string.IsNullOrEmpty(text));
            }
        }

        /// One queue tile: the game's plate and art, and the count in the
        /// corner where it is more than one.
        private sealed class QueueTileView
        {
            private GameObject _go;
            private Image _plate, _icon;
            private GameObject _countBox;
            private TMP_Text _count;

            internal static QueueTileView Create(Transform parent, string name, float size, bool withCount)
            {
                var view = new QueueTileView();
                var back = HudCanvas.Fill(parent, name, new Color(0.1f, 0.12f, 0.15f, 0.9f));
                view._go = back.gameObject;
                var layout = view._go.AddComponent<LayoutElement>();
                layout.preferredWidth = size;
                layout.preferredHeight = size;
                layout.minWidth = size;
                layout.minHeight = size;
                view._plate = Stretched(back.transform, "Plate");
                view._icon = Stretched(back.transform, "Art");
                if (withCount)
                {
                    var box = HudCanvas.Fill(back.transform, "Count", new Color(0f, 0f, 0f, 0.65f));
                    view._countBox = box.gameObject;
                    var brt = box.rectTransform;
                    brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 0f);
                    brt.sizeDelta = new Vector2(22f, 20f);
                    view._count = HudCanvas.Text(box.transform, "Text", 18f, Color.white, TextAlignmentOptions.Center);
                    var crt = view._count.rectTransform;
                    crt.anchorMin = Vector2.zero;
                    crt.anchorMax = Vector2.one;
                    crt.offsetMin = Vector2.zero;
                    crt.offsetMax = Vector2.zero;
                }
                view._go.SetActive(false);
                return view;
            }

            private static Image Stretched(Transform parent, string name)
            {
                var image = HudCanvas.Fill(parent, name, Color.white);
                var rt = image.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                image.enabled = false;
                return image;
            }

            internal void Show(bool showing) => Active(_go, showing);

            private int _shownCount = -1;

            internal void Set(QueueItem item, bool withCount)
            {
                var icon = SpriteFor(item.Icon);
                var plate = icon != null ? SpriteFor(item.Plate) : null;
                _plate.sprite = plate;
                _plate.enabled = plate != null;
                _icon.sprite = icon;
                _icon.enabled = icon != null;
                if (_countBox == null) return;
                var on = withCount && item.Count > 1;
                if (on && item.Count != _shownCount)
                {
                    _shownCount = item.Count;
                    HudCanvas.SetText(_count, item.Count.ToString(Inv));
                }
                Active(_countBox, on);
            }
        }

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(InformationPanelUI panel)
        {
            try
            {
                _log?.LogDebug("Information panel concealed; its tree:");
                PanelConceal.DumpSubtree(panel.transform, 0, _log, 2);
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Information panel: could not describe it ({e.Message}).");
            }
        }
    }
}
