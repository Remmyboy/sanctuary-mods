using System;
using Michsky.UI.Beam;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // Updates on the page: a "Check for Updates" button beside Rescan, and
    // under each mod with a newer release an Update button, which fetches and
    // installs it (see Updates). The tabs rebuild as an update moves along,
    // keeping their scroll position.
    internal sealed partial class ModsPage
    {
        private ButtonManager _checkButton;
        private string _checkLabel;
        private int _updatesSeen = -1;
        // Opened over a match rather than from the front menu: gameplay mods
        // can't change under a running match.
        private bool _openedInMatch;

        /// The check button says how the last check went.
        private void ShowCheckState()
        {
            if (_checkButton == null) return;
            var u = _owner.Updates;
            string label;
            if (u.Checking) label = "Checking…";
            else if (u.CheckResult == null) label = "Check for Updates";
            else if (u.CheckResult != "Checked") label = u.CheckResult;
            else
            {
                var n = u.AvailableCount(_owner.InstalledVersion);
                label = n == 0 ? "Up to Date" : n == 1 ? "1 Update" : $"{n} Updates";
            }
            if (label == _checkLabel) return;
            _checkLabel = label;
            SetButtonText(_checkButton, label);
        }

        /// Rebuilds a tab where it was scrolled to, rather than at the top:
        /// the rows change under the pointer as an update goes through.
        private static void KeepingScroll(Transform list, Action rebuild)
        {
            var scroll = list != null ? list.GetComponentInParent<ScrollRect>() : null;
            var at = scroll != null ? scroll.verticalNormalizedPosition : 1f;
            rebuild();
            if (scroll == null) return;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = at;
        }

        /// The row under a mod saying what an update can do for it: an
        /// Update button when a newer release is out, its progress while it
        /// installs, or a restart to finish one. Nothing when it's current
        /// or no check has run. True when a row was added.
        private bool UpdateRow(Transform list, string folder, string name, bool gameplay)
        {
            var u = _owner.Updates;
            if (u.RestartPending(folder))
            {
                DescribeNext(name, "The new loader and mod API are in place for the next launch; the rest of the update " +
                                   "is put in when the game starts again.");
                InfoRow(list, $"Restart the game to finish updating {name}");
                return true;
            }
            if (string.Equals(u.Installing, folder, StringComparison.OrdinalIgnoreCase))
            {
                InfoRow(list, $"Updating {name}…");
                return true;
            }
            var installed = _owner.InstalledVersion(folder);
            var release = u.Available(folder, installed);
            if (release == null) return false;
            if (gameplay && _openedInMatch)
            {
                DescribeNext(name, "A gameplay mod can't change under a running match; update it from the front menu.");
                InfoRow(list, $"{name} {release.Display} is out", "update after the match");
                return true;
            }
            var failed = u.FailureOf(folder);
            DescribeNext($"Update {name}",
                $"Version {release.Display} is out (you have {installed ?? "an unknown version"}). " +
                "This downloads it from the mod's GitHub release and installs it over your copy; your settings stay. " +
                (gameplay ? "Everyone in a lobby needs the same version, so the others will want it too."
                          : "The mod reloads straight away.") +
                (failed != null ? $"\n\nThe last try failed: {failed}" : ""));
            var f = folder;
            ButtonRow(list, $"Update {name} to {release.Display}" + (failed != null ? " (failed, try again)" : ""),
                () => u.Install(f, ModManagerPlugin.FolderPath(f)));
            return true;
        }
    }
}
