using System;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using SanctuaryUI;
using TMPro;
using UnityEngine;
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
    // plainer one draws in its place from the same values — the postfix on
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
        internal static ConfigEntry<float> Scale;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("InfoCard", "ReplaceCard", true,
                "Replace the game's unit information card with a plainer one drawn from the same values: name, health, shields, " +
                "build cost, income and build power in labelled rows, figures rounded, template id left off. " +
                "The game's card comes back whenever the overlay is hidden or the mod is unloaded.");
            TidyBuiltIn = config.Bind("InfoCard", "TidyGameCard", true,
                "With the game's own card kept (ReplaceCard off): hide the unit's template id and round its income figures.");
            Scale = config.Bind("InfoCard", "Scale", 1f,
                new ConfigDescription("Size of the replacement card, as a multiple of the standard size.", new AcceptableValueRange<float>(0.7f, 1.6f)));
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
            catch { /* cosmetic */ }
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
            var replace = hudShowing && InMatch && Enabled.Value;
            var panel = replace ? FindPanel() : null;
            if (_conceal.Apply(panel)) Describe(panel);
            if (Enabled.Value || !TidyBuiltIn.Value) RestoreBuiltIn();
        }

        internal static void Shutdown()
        {
            _conceal.Release();
            RestoreBuiltIn();
        }

        // ---- drawing --------------------------------------------------------------

        // As wide as the game's own card (528 canvas units at a 4K reference
        // is 264 at 1080), and never taller than it: the selection list sits
        // directly above and must stay clear.
        private const float Width = 264f;
        private const float Pad = 8f;
        private const float GaugeHeight = 26f;

        private static GUIStyle _stTitle, _stSubtitle, _stLabel, _stValue, _stFigure, _stSmall;

        internal static void ApplyFont(Font font)
        {
            _stTitle = new GUIStyle(_stName) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            _stSubtitle = new GUIStyle(_stStripMax) { fontSize = 12, alignment = TextAnchor.MiddleRight, normal = { textColor = SanctuaryHudPlugin.MutedText } };
            _stLabel = new GUIStyle(_stStripLabel) { fontSize = 11, alignment = TextAnchor.MiddleLeft, normal = { textColor = SanctuaryHudPlugin.MutedText } };
            _stValue = new GUIStyle(_stStripIn) { fontSize = 13, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };
            _stFigure = new GUIStyle(_stStripIn) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            _stSmall = new GUIStyle(_stStripIn) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            if (font == null) return;
            foreach (var style in new[] { _stTitle, _stSubtitle, _stLabel, _stValue, _stFigure, _stSmall }) style.font = font;
        }

        /// From OnGUI, under the 1080-logical matrix.
        internal static void Draw(float logicalWidth, float logicalHeight, float scale, Texture2D panelTexture)
        {
            var panel = _conceal.Panel as InformationPanelUI;
            if (panel == null || !panel.IsVisible || !_haveValues || _source != panel) return;
            // With a group selected the game's card describes one unit of
            // it, whichever came first, which says nothing about the group;
            // the selection row carries what is selected.
            if (SelectionRow.CountSelected() > 1) return;
            if (_stTitle == null) ApplyFont(null);

            var v = _values;
            var s = Mathf.Clamp(Scale.Value, 0.7f, 1.6f);

            var showArmour = v.isArmourShieldEnabled && v.armourShieldMax > 0f;
            var showBubble = v.isBubbleShieldEnabled && v.bubbleShieldMax > 0f;
            var showHealth = v.healthMax > 0f;
            var showIncome = Mathf.Abs(v.alloyNetIncome) >= 0.5f || Mathf.Abs(v.energyNetIncome) >= 0.5f;
            var extras = Extras(v);

            var height = Pad + 20f;
            if (showHealth) height += GaugeHeight;
            if (showArmour) height += GaugeHeight;
            if (showBubble) height += GaugeHeight;
            if (v.isConstructionPercentEnabled) height += GaugeHeight;
            if (showIncome) height += 20f;
            if (extras != null) height += 16f;
            height += Pad - 2f;

            var area = new Rect(14f, logicalHeight - 14f - height * s, Width * s, height * s);
            // Where the game's own card sits: the replacement takes its bottom-left corner.
            if (PanelConceal.GuiRect(panel, scale, out var anchor))
            {
                area.x = anchor.x;
                area.y = anchor.yMax - area.height;
            }

            Shield(area, scale);
            GUI.DrawTexture(area, panelTexture);
            var accent = SanctuaryHudPlugin.GameAccent;
            accent.a = 0.6f;
            SanctuaryHudPlugin.Fill(new Rect(area.x, area.y, area.width, 1f), accent);

            var previousMatrix = GUI.matrix;
            GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(area.x, area.y, 0f), Quaternion.identity, new Vector3(s, s, 1f));

            var inner = Width - Pad * 2f;
            var y = Pad;

            // Name on the left, the class of thing it is on the right.
            GUI.Label(new Rect(Pad, y, inner, 20f), _name, _stTitle);
            if (_display.Length > 0)
            {
                var nameWidth = _stTitle.CalcSize(new GUIContent(_name)).x;
                GUI.Label(new Rect(Pad + nameWidth + 8f, y + 1f, inner - nameWidth - 8f, 20f), _display, _stSubtitle);
            }
            y += 20f;

            if (showHealth)
            {
                var frac = Mathf.Clamp01(v.healthValue / v.healthMax);
                var colour = frac > 0.6f ? GainColour : frac > 0.3f ? new Color(0.95f, 0.72f, 0.2f) : DangerColour;
                Gauge(ref y, "HEALTH", v.healthValue, v.healthMax, v.healthRegen, frac, colour);
            }
            if (showArmour)
            {
                Gauge(ref y, "ARMOUR", v.armourShieldValue, v.armourShieldMax, v.armourShieldRegen, Mathf.Clamp01(v.armourShieldValue / v.armourShieldMax), UpgradeColour);
            }
            if (showBubble)
            {
                Gauge(ref y, "SHIELD", v.bubbleShieldValue, v.bubbleShieldMax, v.bubbleShieldRegen, Mathf.Clamp01(v.bubbleShieldValue / v.bubbleShieldMax), UpgradeColour);
            }
            if (v.isConstructionPercentEnabled)
            {
                var frac = Mathf.Clamp01(v.constructionPercent);
                GUI.Label(new Rect(Pad, y, inner, 16f), "BUILDING", _stLabel);
                GUI.Label(new Rect(Pad, y, inner, 16f), (frac * 100f).ToString("0") + "%", _stValue);
                Bar(y + 17f, frac, UpgradeColour);
                y += GaugeHeight;
            }

            // What the unit adds to or takes from the economy, per second:
            // just the figures, each in its resource's colour, and only the
            // ones that are not zero (a generator makes energy, not alloy).
            // No build cost: it is already paid by the time this card is
            // about a unit, and the build menu is where it matters.
            if (showIncome)
            {
                Figures(Pad, y, inner, v.alloyNetIncome, v.energyNetIncome);
                y += 20f;
            }

            if (extras != null)
            {
                _stSmall.normal.textColor = new Color(0.85f, 0.9f, 0.97f);
                GUI.Label(new Rect(Pad, y, inner, 16f), extras, _stSmall);
                y += 16f;
            }

            GUI.matrix = previousMatrix;
        }

        /// A labelled gauge: label left, "value / max" right, regen beside
        /// the value when there is any, and the bar underneath.
        private static void Gauge(ref float y, string label, float value, float max, float regen, float frac, Color colour)
        {
            var inner = Width - Pad * 2f;
            GUI.Label(new Rect(Pad, y, inner, 16f), label, _stLabel);
            var text = SanctuaryHudPlugin.Fmt(value) + " / " + SanctuaryHudPlugin.Fmt(max);
            GUI.Label(new Rect(Pad, y, inner, 16f), text, _stValue);
            if (regen >= 0.5f)
            {
                var width = _stValue.CalcSize(new GUIContent(text)).x;
                _stSmall.normal.textColor = GainColour;
                var regenText = "+" + SanctuaryHudPlugin.Fmt(regen) + "/s";
                var regenWidth = _stSmall.CalcSize(new GUIContent(regenText)).x;
                GUI.Label(new Rect(Pad + inner - width - regenWidth - 8f, y + 1f, regenWidth, 16f), regenText, _stSmall);
            }
            Bar(y + 17f, frac, colour);
            y += GaugeHeight;
        }

        private static void Bar(float y, float frac, Color colour)
        {
            var inner = Width - Pad * 2f;
            var track = SanctuaryHudPlugin.GameAccent;
            track.a = 0.14f;
            SanctuaryHudPlugin.Fill(new Rect(Pad, y, inner, 4f), track);
            SanctuaryHudPlugin.Fill(new Rect(Pad, y, inner * frac, 4f), colour);
        }

        /// Alloy then energy rates side by side in their tints, signed, per
        /// second; a zero is left out.
        private static void Figures(float x, float y, float width, float alloy, float energy)
        {
            string Show(float value) => (value > 0f ? "+" : "−") + SanctuaryHudPlugin.Fmt(value) + "/s";
            if (Mathf.Abs(alloy) >= 0.5f)
            {
                var a = Show(alloy);
                _stFigure.normal.textColor = SanctuaryHudPlugin.AlloyTint;
                GUI.Label(new Rect(x, y, width, 18f), a, _stFigure);
                x += _stFigure.CalcSize(new GUIContent(a)).x + 12f;
            }
            if (Mathf.Abs(energy) >= 0.5f)
            {
                _stFigure.normal.textColor = SanctuaryHudPlugin.EnergyTint;
                GUI.Label(new Rect(x, y, width, 18f), Show(energy), _stFigure);
            }
        }

        /// The occasional figures, one line, only the ones that apply.
        private static string Extras(UIInformationValues v)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (v.buildPower > 0f) parts.Add("BUILD POWER " + SanctuaryHudPlugin.Fmt(v.buildPower));
            if (v.veterancy > 0f) parts.Add("VETERANCY " + SanctuaryHudPlugin.Fmt(v.veterancy));
            if (v.transportCapacity > 0f) parts.Add("TRANSPORT " + SanctuaryHudPlugin.Fmt(v.transportCapacity));
            if (v.ammoCapacity > 0f) parts.Add("AMMO " + SanctuaryHudPlugin.Fmt(v.ammoCapacity));
            return parts.Count == 0 ? null : string.Join("    ", parts);
        }

        // ---- diagnostics ------------------------------------------------------

        private static void Describe(InformationPanelUI panel)
        {
            try
            {
                _log?.LogInfo("Information panel concealed; its tree:");
                PanelConceal.DumpSubtree(panel.transform, 0, _log, 2);
            }
            catch (Exception e)
            {
                _log?.LogInfo($"Information panel: could not describe it ({e.Message}).");
            }
        }
    }
}
