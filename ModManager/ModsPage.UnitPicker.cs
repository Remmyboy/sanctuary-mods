using System;
using System.Collections.Generic;
using System.Linq;
using Michsky.UI.Beam;
using Sanctuary.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // A "units" option's picker: the lobby panel's list turns into every
    // unit the match could build, by land, air, naval and structures, then
    // by what the unit is ("Tier 1: Tank"). That row's switch picks the
    // role for every faction; unfolding it shows each faction's own unit.
    // The host switches; everyone else sees the same rows, locked.
    //
    // Opening, closing and folding never rebuild the list from inside a
    // click: the rows are destroyed immediately on a rebuild, so a click
    // only bumps _pickerVersion and the panel's tick rebuilds after it.
    internal sealed partial class ModsPage
    {
        private string _pickerMod;
        private string _pickerKey;
        private int _pickerVersion;
        private bool _lobbyScrollToTop;
        // Roles unfolded, by domain and role.
        private readonly HashSet<string> _pickerOpen = new HashSet<string>(StringComparer.Ordinal);

        private string PickerStructure() => _pickerMod == null ? "" : $"{_pickerMod}/{_pickerKey}/{_pickerVersion}";

        /// The button under a units option that opens its picker.
        private void PickerButton(string modId, ModOption o, bool editable) =>
            ButtonRow(_lobbyList, (editable ? "Choose " : "See ") + o.Label.ToLowerInvariant(), () =>
            {
                _pickerMod = modId;
                _pickerKey = o.Key;
                _pickerOpen.Clear();
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

            // The picked ids, parsed once per value rather than once per row.
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
            void Set(IEnumerable<string> ids, bool on)
            {
                var set = new HashSet<string>(Picked(), StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    if (on) set.Add(id);
                    else set.Remove(id);
                }
                Lobby.SetOption(modId, o.Key, ModOption.JoinUnits(set));
            }

            Heading(_lobbyList, $"{s.Name}: {o.Label}");
            if (o.Description.Length > 0) DescribeNext(o.Label, o.Description);
            var summary = InfoRow(_lobbyList, editable ? "Switch on the units to pick" : "The host's pick", "");
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

            foreach (var (domain, title) in PickerDomains)
            {
                var inDomain = units.Where(u => u.Domain == domain).ToList();
                if (inDomain.Count == 0) continue;
                Line(_lobbyList);
                Heading(_lobbyList, title);
                foreach (var role in inDomain.GroupBy(u => u.Role))
                    RoleRows(domain, role.Key, role.ToList(), editable, Picked, Set);
            }
            return true;
        }

        /// One role: a switch for every faction's unit of it, unfolding to a
        /// switch per faction. A role only one faction has is just that
        /// unit's switch.
        private void RoleRows(string domain, string role, List<UnitEntry> units, bool editable,
            Func<HashSet<string>> picked, Action<IEnumerable<string>, bool> set)
        {
            var ids = units.Select(u => u.Id).ToList();
            if (units.Count == 1)
            {
                var u = units[0];
                var only = SwitchRow(_lobbyList, $"{role}   <alpha=#80>{u.Label}", picked().Contains(u.Id), editable, on => set(ids, on));
                _lobbyUpdaters.Add(() => FollowSwitch(only, picked().Contains(u.Id)));
                return;
            }

            var key = domain + "/" + role;
            Transform group = null;
            string Name()
            {
                var n = ids.Count(picked().Contains);
                return n == 0 || n == ids.Count ? role : $"{role}   ({n} of {ids.Count})";
            }
            TMP_Text label = null;
            label = SectionRow(_lobbyList, Name(), ids.All(picked().Contains), _pickerOpen.Contains(key),
                on => set(ids, on),
                () =>
                {
                    if (!_pickerOpen.Add(key)) _pickerOpen.Remove(key);
                    var open = _pickerOpen.Contains(key);
                    if (open && group == null) group = FactionRows(label.transform.parent, units, editable, picked, set);
                    if (group != null) group.gameObject.SetActive(open);
                    return open;
                },
                heading: false, interactable: editable);
            var sw = label.transform.parent.Find("Switch").GetComponent<SwitchManager>();
            _lobbyUpdaters.Add(() =>
            {
                FollowSwitch(sw, ids.All(picked().Contains));
                SetText(label, SectionLabel(Name(), _pickerOpen.Contains(key)));
            });
            if (_pickerOpen.Contains(key)) group = FactionRows(label.transform.parent, units, editable, picked, set);
        }

        /// The unfolded rows of a role, one per faction's unit, in a group
        /// placed right under the role's row.
        private Transform FactionRows(Transform roleRow, List<UnitEntry> units, bool editable,
            Func<HashSet<string>> picked, Action<IEnumerable<string>, bool> set)
        {
            var group = new GameObject("Units", typeof(RectTransform)).transform;
            var vl = group.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 15f;
            vl.childControlWidth = true;
            vl.childControlHeight = false;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;
            group.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // Right after the role's row, also when it unfolds later.
            group.SetParent(_lobbyList, false);
            group.SetSiblingIndex(roleRow.GetSiblingIndex() + 1);
            foreach (var unit in units)
            {
                var u = unit;
                var sw = SwitchRow(group, OptionIndent + u.Label, picked().Contains(u.Id), editable, on => set(new[] { u.Id }, on));
                _lobbyUpdaters.Add(() => FollowSwitch(sw, picked().Contains(u.Id)));
            }
            return group;
        }

        private static void FollowSwitch(SwitchManager sw, bool on)
        {
            if (sw == null || sw.isOn == on) return;
            sw.isOn = on;
            sw.UpdateUI();
        }
    }
}
