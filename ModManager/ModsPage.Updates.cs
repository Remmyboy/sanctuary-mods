using System;
using System.Collections.Generic;
using System.Linq;
using Michsky.UI.Beam;
using UnityEngine;
using UnityEngine.UI;

namespace SanctuaryHud
{
    // Updates on the page: a "Check for Updates" button beside Rescan, and
    // under each mod with a newer release an Update button, which fetches and
    // installs it (see Updates). The row under the mod then follows the
    // update through (downloading, reloading, done) and stays until the page
    // is next opened. The tabs rebuild as an update moves along, keeping
    // their scroll position.
    internal sealed partial class ModsPage
    {
        private ButtonManager _checkButton;
        private string _checkLabel;
        private int _updatesSeen = -1;
        // Opened over a match (or replay) rather than from the front menu:
        // mods only update from the menu, so a match never has a mod
        // reloading under it.
        private bool _openedInMatch;
        // Installed mods whose new DLL the loader hasn't reloaded yet, looked
        // at twice a second while the page is up.
        private readonly HashSet<string> _awaitingReload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float _reloadPolledAt;

        /// Called by Open: the page starts from what is installed now.
        private void ResetUpdateRows()
        {
            _owner.Updates.ForgetJustInstalled();
            _awaitingReload.Clear();
            _updatesSeen = _owner.Updates.Changes;
            ShowCheckState();
        }

        /// Called every frame the page is up.
        private void TickUpdates()
        {
            if (_owner.Updates.Changes != _updatesSeen) RebuildForUpdates();
            else if (_awaitingReload.Count > 0 && Time.unscaledTime - _reloadPolledAt > 0.5f)
            {
                _reloadPolledAt = Time.unscaledTime;
                if (_awaitingReload.Any(Reloaded)) RebuildForUpdates();
            }
        }

        private void RebuildForUpdates()
        {
            _updatesSeen = _owner.Updates.Changes;
            _awaitingReload.Clear();
            // The plugin list too: a reloaded mod's summary reads its version
            // from the new copy.
            _owner.RefreshPluginsNow();
            ShowCheckState();
            KeepingScroll(_uiList, RebuildUiTab);
            KeepingScroll(_gameList, RebuildGameplayTab);
        }

        private bool Reloaded(string folder)
        {
            var done = _owner.Updates.JustInstalled(folder);
            var now = Updates.ParseVersion(_owner.InstalledVersion(folder));
            return done == null || (now != null && now >= done.Version);
        }

        /// The mods the bottom button would update: everything with a newer
        /// release, from the front menu only.
        private List<string> UpdatableFolders() =>
            _openedInMatch || _owner.Updates.CheckResult != "Checked"
                ? new List<string>()
                : _owner.Updates.AvailableFolders(_owner.InstalledVersion);

        /// The bottom button: "Check for Updates" until a check finds some,
        /// then "Update All (n)", which installs them all. Clicking it any
        /// other time checks again.
        private void CheckButtonClicked()
        {
            var u = _owner.Updates;
            if (u.Checking || u.Installing != null) return;
            var folders = UpdatableFolders();
            if (folders.Count == 0) u.Check();
            else u.InstallAll(folders.Select(f => (f, ModManagerPlugin.FolderPath(f))));
        }

        private void ShowCheckState()
        {
            if (_checkButton == null) return;
            var u = _owner.Updates;
            string label;
            if (u.Checking) label = "Checking…";
            else if (u.Installing != null) label = "Updating…";
            else if (u.CheckResult == null) label = "Check for Updates";
            else if (u.CheckResult != "Checked") label = u.CheckResult;
            else
            {
                var n = UpdatableFolders().Count;
                var inMatch = _openedInMatch ? u.AvailableFolders(_owner.InstalledVersion).Count : 0;
                label = n > 0 ? $"Update All ({n})" : inMatch > 0 ? (inMatch == 1 ? "1 Update" : $"{inMatch} Updates") : "Up to Date";
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
        /// Update button when a newer release is out, then the update's
        /// progress, or a restart to finish one. Nothing when it's current
        /// or no check has run. True when a row was added.
        private bool UpdateRow(Transform list, string folder, string name, bool gameplay)
        {
            var u = _owner.Updates;
            if (u.RestartPending(folder))
            {
                DescribeNext(name, "The new loader and mod API are in place for the next launch; the rest of the update " +
                                   "is put in when the game starts again.");
                InfoRow(list, $"{name} updated: restart the game to finish", "restart needed");
                return true;
            }
            var installing = u.InstallingRelease(folder);
            if (installing != null)
            {
                InfoRow(list, $"Updating {name} to {installing.Display}", "downloading…");
                return true;
            }
            var done = u.JustInstalled(folder);
            if (done != null)
            {
                var reloaded = Reloaded(folder);
                if (!reloaded) _awaitingReload.Add(folder);
                DescribeNext(name, reloaded
                    ? $"Version {done.Display} is installed and running."
                    : $"Version {done.Display} is installed; the mod loader picks it up in a moment.");
                InfoRow(list, $"{name} updated to {done.Display}", reloaded ? "done" : "reloading…");
                return true;
            }
            var installed = _owner.InstalledVersion(folder);
            var release = u.Available(folder, installed);
            if (release == null) return false;
            if (_openedInMatch)
            {
                DescribeNext(name, "Mods update from the main menu, not during a match. Open this page there to update it.");
                InfoRow(list, $"{name} {release.Display} is out", "update from the main menu");
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
