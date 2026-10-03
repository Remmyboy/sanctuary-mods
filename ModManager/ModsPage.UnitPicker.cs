using System;
using System.Collections.Generic;
using System.Linq;
using EM.Gamedata;
using Sanctuary.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // A "units" option's picker: the lobby panel's list turns into a grid
    // per section (land, air, naval, structures). Columns are factions, rows
    // are kinds of unit under a tech-level header, and each cell is the
    // faction's unit of that kind: its strategic symbol (the sprite the
    // game's own UI draws) and its name. A click on a cell picks that unit;
    // on a kind, every faction's; on a faction's header, that faction's
    // whole section; on a tech header, the whole tier. Picked cells are red.
    // The host clicks; everyone else sees the same grid, locked.
    //
    // The grid is plain uGUI rather than the page's settings rows: those
    // hold one switch each, and a grid of them reads as a list.
    //
    // Opening and closing never rebuild the list from inside a click: the
    // rows are destroyed immediately on a rebuild, so a click only bumps
    // _pickerVersion and the panel's tick rebuilds after it.
    internal sealed partial class ModsPage
    {
        private string _pickerMod;
        private string _pickerKey;
        private int _pickerVersion;
        private bool _lobbyScrollToTop;
        // Symbol name to its art, misses included (as null).
        private readonly Dictionary<string, SymbolArt> _symbolArt = new Dictionary<string, SymbolArt>(StringComparer.Ordinal);

        /// A strategic symbol split the way the game's UI shader reads it:
        /// the sprite is an opaque square with the symbol in red and its glow
        /// in green. Each becomes a white mask, so the glow can be tinted.
        private sealed class SymbolArt
        {
            internal Sprite Symbol;
            internal Sprite Glow;
        }

        private const float PickerGap = 8f;

        // The game's UI glow colours for strategic symbols (common/colors.lua).
        private static readonly Color GlowBlue = new Color(91 / 255f, 148 / 255f, 1f);
        private static readonly Color GlowRed = new Color(1f, 33 / 255f, 13 / 255f);
        private static readonly Color CellAllowed = new Color(0.09f, 0.12f, 0.17f, 0.95f);
        private static readonly Color CellPicked = new Color(0.36f, 0.06f, 0.05f, 0.95f);
        private static readonly Color HeaderAllowed = new Color(0.14f, 0.17f, 0.23f, 0.95f);
        private static readonly Color HeaderPicked = new Color(0.45f, 0.08f, 0.06f, 0.95f);
        private static readonly Color TextAllowed = new Color(0.9f, 0.92f, 0.95f);
        private static readonly Color TextPicked = new Color(1f, 0.72f, 0.68f);

        // The stock factions' army colours (common/colors.lua), lifted so
        // they read as text on the dark panel.
        private static readonly Dictionary<string, Color> FactionColours = new Dictionary<string, Color>
        {
            ["EDA"] = Color.Lerp(new Color(0.078f, 0.329f, 0.196f), Color.white, 0.45f),
            ["CHOSEN"] = Color.Lerp(new Color(0.412f, 0.008f, 0.008f), Color.white, 0.45f),
            ["GUARD"] = new Color(216 / 255f, 184 / 255f, 90 / 255f),
        };
        private static readonly string[] StockFactionOrder = { "EDA", "CHOSEN", "GUARD" };

        private string PickerStructure() => _pickerMod == null ? "" : $"{_pickerMod}/{_pickerKey}/{_pickerVersion}";

        /// The button under a units option that opens its picker.
        private void PickerButton(string modId, ModOption o, bool editable) =>
            ButtonRow(_lobbyList, (editable ? "Choose " : "See ") + o.Label.ToLowerInvariant(), () =>
            {
                _pickerMod = modId;
                _pickerKey = o.Key;
                _lobbyScrollToTop = true;
                _pickerVersion++;
            });

        private void ClosePicker()
        {
            _pickerMod = _pickerKey = null;
            _lobbyScrollToTop = true;
            _pickerVersion++;
        }

        private static readonly (string domain, string title)[] PickerDomains =
        {
            ("land", "Land units"), ("air", "Air units"), ("naval", "Naval units"), ("structure", "Structures"),
        };

        /// What a grid cell, header or row label shows and does.
        private sealed class PickerBox
        {
            internal Image Back;
            internal Image Icon;   // the glow, tinted by state
            internal Image Symbol;
            internal TMP_Text Text;
            internal Color Accent; // a header's own text colour (factions)
            internal bool IsCell;  // one unit, rather than a group header
        }

        /// The picker's rows, or false when the option is gone (the mod was
        /// dropped) and the panel should show the mods again.
        private bool FillUnitPicker()
        {
            var s = Lobby.Selection.FirstOrDefault(x => x.Id == _pickerMod);
            var o = s?.OptionDefinitions.FirstOrDefault(x => x.Key == _pickerKey && x.Type == ModOptionType.Units);
            if (o == null)
            {
                _pickerMod = _pickerKey = null;
                return false;
            }
            var modId = s.Id;
            var editable = Lobby.IsHost && Lobby.CanChangeSelection;

            // The picked ids, parsed once per value rather than once per cell.
            string parsedFrom = null;
            HashSet<string> parsed = null;
            HashSet<string> Picked()
            {
                var now = CurrentOption(modId, o.Key) ?? "";
                if (now != parsedFrom)
                {
                    parsedFrom = now;
                    parsed = new HashSet<string>(ModOption.SplitUnits(now), StringComparer.Ordinal);
                }
                return parsed;
            }
            // A group click: all of them picked already lets them all go,
            // otherwise picks them all.
            Action Toggle(IReadOnlyCollection<string> ids) => !editable ? (Action)null : () =>
            {
                var set = new HashSet<string>(Picked(), StringComparer.Ordinal);
                var all = ids.All(set.Contains);
                foreach (var id in ids)
                {
                    if (all) set.Remove(id);
                    else set.Add(id);
                }
                Lobby.SetOption(modId, o.Key, ModOption.JoinUnits(set));
            };

            Heading(_lobbyList, $"{s.Name}: {o.Label}");
            if (o.Description.Length > 0) DescribeNext(o.Label, o.Description);
            var summary = InfoRow(_lobbyList, editable
                ? "Click a unit, a kind, a faction or a tech level; red is restricted"
                : "The host's pick; red is restricted", "");
            _lobbyUpdaters.Add(() => SetText(summary.value, o.Display(CurrentOption(modId, o.Key))));
            ButtonRow(_lobbyList, "Back to the mods", ClosePicker);
            if (editable) ButtonRow(_lobbyList, "Clear the list", () => Lobby.SetOption(modId, o.Key, ""));

            var units = UnitCatalog.Buildable(Lobby.Selection.Where(x => x.Local != null).Select(x => x.Local));
            var known = new HashSet<string>(units.Select(u => u.Id), StringComparer.Ordinal);
            var elsewhere = InfoRow(_lobbyList, "Picked, but not in this match's units", "");
            elsewhere.label.transform.parent.gameObject.SetActive(false);
            _lobbyUpdaters.Add(() =>
            {
                var n = Picked().Count(id => !known.Contains(id));
                elsewhere.label.transform.parent.gameObject.SetActive(n > 0);
                SetText(elsewhere.value, n.ToString());
            });

            var factions = units.Select(u => (tag: u.FactionTag, name: u.FactionName)).Distinct()
                .OrderBy(f => Array.IndexOf(StockFactionOrder, f.tag) is var i && i >= 0 ? i : 99).ToList();
            var template = _tSwitchRow.transform.Find("Text").GetComponent<TMP_Text>();
            var rowHeight = Mathf.Clamp(((RectTransform)_tSwitchRow.transform).sizeDelta.y * 0.8f, 40f, 90f);
            var fontSize = template.fontSize * 0.8f;

            void Paint(PickerBox box, IReadOnlyCollection<string> ids, string label)
            {
                _lobbyUpdaters.Add(() =>
                {
                    var set = Picked();
                    var n = ids.Count(set.Contains);
                    var all = n == ids.Count && n > 0;
                    var isCell = box.IsCell;
                    box.Back.color = all ? (isCell ? CellPicked : HeaderPicked) : (isCell ? CellAllowed : HeaderAllowed);
                    if (box.Icon != null) box.Icon.color = all ? GlowRed : GlowBlue;
                    box.Text.color = all ? TextPicked : box.Accent;
                    var text = all && isCell ? "<s>" + label + "</s>" : label;
                    if (!isCell && n > 0 && !all) text += $"  <alpha=#99>({n} of {ids.Count})";
                    SetText(box.Text, text);
                });
            }

            foreach (var (domain, title) in PickerDomains)
            {
                var inDomain = units.Where(u => u.Domain == domain).ToList();
                if (inDomain.Count == 0) continue;
                Line(_lobbyList);

                // The section bar: the whole section.
                var bar = PickerRow(_lobbyList, rowHeight);
                var sectionIds = inDomain.Select(u => u.Id).ToList();
                Paint(Box(Cell(bar, 1f), title.ToUpperInvariant(), null, Toggle(sectionIds), rowHeight, fontSize * 1.15f, TextAllowed, true),
                    sectionIds, title.ToUpperInvariant());

                // Faction headers: each faction's part of the section.
                var head = PickerRow(_lobbyList, rowHeight);
                Cell(head, 1.15f);
                foreach (var (tag, name) in factions)
                {
                    var ids = inDomain.Where(u => u.FactionTag == tag).Select(u => u.Id).ToList();
                    var cell = Cell(head, 1f);
                    if (ids.Count == 0) continue;
                    var colour = FactionColours.TryGetValue(tag, out var c) ? c : TextAllowed;
                    Paint(Box(cell, name, null, Toggle(ids), rowHeight, fontSize, colour, true), ids, name);
                }

                foreach (var tier in inDomain.GroupBy(u => u.Tech))
                {
                    var tierIds = tier.Select(u => u.Id).ToList();
                    var tierName = tier.Key > 0 ? $"Tech {tier.Key}" : "Other";
                    var tierRow = PickerRow(_lobbyList, rowHeight * 0.75f);
                    Paint(Box(Cell(tierRow, 1f), tierName, null, Toggle(tierIds), rowHeight * 0.75f, fontSize * 0.9f, TextAllowed, false),
                        tierIds, tierName);

                    foreach (var role in tier.GroupBy(u => u.Role))
                    {
                        var byFaction = factions.Select(f => role.Where(u => u.FactionTag == f.tag).ToList()).ToList();
                        var stack = Mathf.Max(1, byFaction.Max(l => l.Count));
                        var height = stack * rowHeight + (stack - 1) * PickerGap;
                        var row = PickerRow(_lobbyList, height);
                        var roleIds = role.Select(u => u.Id).ToList();
                        var kind = KindLabel(role.Key, tier.Key);
                        Paint(Box(Cell(row, 1.15f), kind, null, Toggle(roleIds), height, fontSize, TextAllowed, false), roleIds, kind);
                        foreach (var list in byFaction)
                        {
                            var cell = Cell(row, 1f);
                            if (list.Count == 0) continue;
                            var v = cell.gameObject.AddComponent<VerticalLayoutGroup>();
                            v.spacing = PickerGap;
                            v.childControlWidth = v.childControlHeight = true;
                            v.childForceExpandWidth = true;
                            v.childForceExpandHeight = false;
                            foreach (var u in list)
                            {
                                var name = u.Name.Length > 0 ? u.Name : kind;
                                var box = Box(cell, name, SymbolArtFor(u.IconSymbol), Toggle(new[] { u.Id }), rowHeight, fontSize, TextAllowed, false);
                                box.IsCell = true;
                                box.Back.gameObject.AddComponent<LayoutElement>().preferredHeight = rowHeight;
                                Paint(box, new[] { u.Id }, name);
                            }
                        }
                    }
                }
            }
            return true;
        }

        /// "Tier 1: Tank" under the Tech 1 header reads "Tank".
        private static string KindLabel(string role, int tech)
        {
            var prefix = $"Tier {tech}: ";
            return role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? role.Substring(prefix.Length) : role;
        }

        /// One line of the grid, cells side by side, sized by their weights.
        private static RectTransform PickerRow(Transform list, float height)
        {
            var go = new GameObject("Picker Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            var rt = (RectTransform)go.transform;
            rt.SetParent(list, false);
            rt.sizeDelta = new Vector2(0f, height);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = PickerGap;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = true;
            return rt;
        }

        private static RectTransform Cell(Transform row, float weight)
        {
            var go = new GameObject("Cell", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var le = go.GetComponent<LayoutElement>();
            le.flexibleWidth = weight;
            le.preferredWidth = 0f;
            le.minWidth = 0f;
            return (RectTransform)go.transform;
        }

        /// A filled box with a label, and a symbol on its left when given
        /// one; a button when it does something.
        private PickerBox Box(Transform parent, string text, SymbolArt icon, Action onClick, float height, float fontSize, Color accent, bool centred)
        {
            var go = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var box = new PickerBox { Back = go.GetComponent<Image>(), Accent = accent };
            box.Back.color = HeaderAllowed;
            if (onClick != null)
            {
                var button = go.AddComponent<Button>();
                button.targetGraphic = box.Back;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                var colours = button.colors;
                colours.normalColor = new Color(0.85f, 0.85f, 0.85f);
                colours.highlightedColor = Color.white;
                colours.pressedColor = new Color(0.65f, 0.65f, 0.65f);
                colours.selectedColor = colours.normalColor;
                colours.fadeDuration = 0.05f;
                button.colors = colours;
                button.onClick.AddListener(() => onClick());
            }

            var pad = Mathf.Round(height * 0.15f);
            var left = pad * 1.5f;
            var iconSize = Mathf.Min(height - 2f * pad, 64f);
            if (icon != null)
            {
                Image Layer(string name, Sprite sprite, Color colour)
                {
                    var ig = new GameObject(name, typeof(RectTransform), typeof(Image));
                    var irt = (RectTransform)ig.transform;
                    irt.SetParent(rt, false);
                    irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                    irt.pivot = new Vector2(0f, 0.5f);
                    irt.sizeDelta = new Vector2(iconSize, iconSize);
                    irt.anchoredPosition = new Vector2(left, 0f);
                    var image = ig.GetComponent<Image>();
                    image.sprite = sprite;
                    image.preserveAspect = true;
                    image.raycastTarget = false;
                    image.color = colour;
                    return image;
                }
                box.Icon = Layer("Glow", icon.Glow, GlowBlue);
                box.Symbol = Layer("Symbol", icon.Symbol, Color.white);
                left += iconSize + pad;
            }

            var tg = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            var trt = (RectTransform)tg.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(left, 0f);
            trt.offsetMax = new Vector2(-pad, 0f);
            var tmp = tg.GetComponent<TextMeshProUGUI>();
            var template = _tSwitchRow.transform.Find("Text").GetComponent<TMP_Text>();
            tmp.font = template.font;
            tmp.fontSharedMaterial = template.fontSharedMaterial;
            tmp.fontSize = fontSize;
            tmp.color = accent;
            tmp.alignment = centred ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            tmp.richText = true;
            tmp.text = text;
            box.Text = tmp;
            return box;
        }

        /// The strategic symbol the game's UI draws for a unit
        /// (UI/Sprites/Icons/UnitSymbols), loaded from the game's data the
        /// way its Engine.LoadSprite does, so it works in the lobby too, and
        /// split into symbol and glow masks. Null when there's no such
        /// sprite: the cell is then text only.
        private SymbolArt SymbolArtFor(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) symbol = "none";
            if (_symbolArt.TryGetValue(symbol, out var cached)) return cached;
            SymbolArt art = null;
            try
            {
                var entry = Load.GetFileEntryFromPath("UI/Sprites/Icons/UnitSymbols/icon_unit_symbol_" + symbol + ".sansprite");
                var sprite = entry == null ? null : Load.LoadGamedataSprite(entry)?.GetSprite();
                if (sprite == null) _log.LogInfo($"Unit picker: no symbol sprite for '{symbol}'.");
                else art = Split(sprite);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Unit picker: symbol '{symbol}' failed to load: {e.Message}");
            }
            _symbolArt[symbol] = art;
            return art;
        }

        /// Reads the sprite back through a render texture (the game's copy
        /// isn't CPU-readable) and makes a white mask of each channel.
        private static SymbolArt Split(Sprite sprite)
        {
            var tex = sprite.texture;
            var r = sprite.textureRect;
            // The game's sprites are stored upside down, with a negative
            // rect height: read the rows they cover, then flip them back.
            var flipped = r.height < 0f;
            int w = Mathf.RoundToInt(Mathf.Abs(r.width)), h = Mathf.RoundToInt(Mathf.Abs(r.height));
            float x0 = Mathf.Min(r.x, r.x + r.width), y0 = Mathf.Min(r.y, r.y + r.height);
            if (w <= 0 || h <= 0) return null;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var was = RenderTexture.active;
            Color32[] pixels;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(x0, y0, w, h), 0, 0);
                read.Apply();
                pixels = read.GetPixels32();
                if (flipped)
                {
                    var rows = new Color32[pixels.Length];
                    for (var y = 0; y < h; y++) Array.Copy(pixels, y * w, rows, (h - 1 - y) * w, w);
                    pixels = rows;
                }
                UnityEngine.Object.Destroy(read);
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
            }

            Sprite Mask(Func<Color32, byte> channel)
            {
                var mask = new Color32[pixels.Length];
                for (var i = 0; i < pixels.Length; i++) mask[i] = new Color32(255, 255, 255, channel(pixels[i]));
                var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                t.SetPixels32(mask);
                t.Apply(true, true);
                return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
            }
            return new SymbolArt { Symbol = Mask(c => c.r), Glow = Mask(c => c.g) };
        }
    }
}
