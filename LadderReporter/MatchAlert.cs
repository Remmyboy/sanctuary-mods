using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace SanctuaryHud
{
    // Getting the player back when a match is found: the game comes to the
    // front (RestoreWindow, Matchmaking.cs), Windows' notification sound
    // plays, and a Windows notification says who and where. Each one has its
    // own switch, all on.
    //
    // The notification is a tray balloon (Shell_NotifyIcon), which Windows 10
    // and 11 show as an ordinary notification under the game's name: no
    // extra install, and Do Not Disturb applies. The tray icon exists only
    // while the notification does. The balloon is silent; the sound is ours,
    // so switching one off doesn't take the other with it.
    //
    // Windows decides whether a background program may take focus. When it
    // says no, the taskbar button flashes until looked at, and the sound and
    // notification still say it.
    public partial class LadderReporterPlugin
    {
        private ConfigEntry<bool> _cfgAlertRaise, _cfgAlertSound, _cfgAlertNotify;

        private string _alertedMatchId;
        private bool _trayIconUp;
        private float _trayIconUntil;

        private const int TrayIconId = 0x5DB1;

        private void AwakeAlerts()
        {
            _cfgAlertRaise = Config.Bind("Alerts", "BringToFront", true,
                "When a ladder match is found or launches, bring the game to the front (or flash its taskbar " +
                "button when Windows won't allow that).");
            _cfgAlertSound = Config.Bind("Alerts", "Sound", true,
                "When a ladder match is found, play Windows' notification sound.");
            _cfgAlertNotify = Config.Bind("Alerts", "Notification", true,
                "When a ladder match is found while the game isn't the window in front, show a Windows notification.");
        }

        /// A match was found: everything the player has switched on, once per
        /// match however often the site repeats it.
        private void MatchFoundAlert(string matchId, string text)
        {
            if (matchId != null && matchId == _alertedMatchId) return;
            _alertedMatchId = matchId;
            var behind = !GameInFront();
            if (_cfgAlertRaise.Value) RestoreWindow();
            if (_cfgAlertSound.Value) PlayAlertSound();
            if (_cfgAlertNotify.Value && behind) ShowNotification("Ladder match found", text);
            Logger.LogInfo($"Matchmaking: match found alert ({(behind ? "game was behind" : "game in front")}; " +
                           $"raise {_cfgAlertRaise.Value}, sound {_cfgAlertSound.Value}, notification {_cfgAlertNotify.Value && behind}).");
        }

        // The launch itself also brings the game back, on the same switch.
        private void RaiseForLaunch()
        {
            if (_cfgAlertRaise.Value) RestoreWindow();
        }

        /// Every frame: the tray icon goes once its notification has had its time.
        private void UpdateAlerts()
        {
            if (_trayIconUp && Time.realtimeSinceStartup > _trayIconUntil) RemoveTrayIcon();
        }

        private void DestroyAlerts() => RemoveTrayIcon();

        // ---- Windows ---------------------------------------------------------

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        private const int NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
        private const int NIIF_USER = 0x4, NIIF_NOSOUND = 0x10, NIIF_LARGE_ICON = 0x20;
        private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_ALIAS = 0x10000;

        private IntPtr _trayIcon, _balloonIcon, _trayWindow;

        private static IntPtr GameWindow()
        {
            try { return Process.GetCurrentProcess().MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }

        private static bool GameInFront()
        {
            var hwnd = GameWindow();
            return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd && !IsIconic(hwnd);
        }

        private void PlayAlertSound()
        {
            try
            {
                // The sound the player's own Windows scheme gives notifications.
                PlaySound("Notification.Default", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: alert sound failed: {e.Message}");
            }
        }

        private void ShowNotification(string title, string text)
        {
            try
            {
                var hwnd = GameWindow();
                if (hwnd == IntPtr.Zero) return;
                if (_trayIcon == IntPtr.Zero)
                {
                    // The game's own icon, from its exe; Windows' generic one if not.
                    var large = new IntPtr[1];
                    var small = new IntPtr[1];
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (exe != null && ExtractIconEx(exe, 0, large, small, 1) > 0)
                    {
                        _trayIcon = small[0] != IntPtr.Zero ? small[0] : large[0];
                        _balloonIcon = large[0] != IntPtr.Zero ? large[0] : small[0];
                    }
                    if (_trayIcon == IntPtr.Zero) _trayIcon = _balloonIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);   // IDI_APPLICATION
                }

                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = hwnd,
                    uID = TrayIconId,
                    uFlags = NIF_ICON | NIF_TIP,
                    hIcon = _trayIcon,
                    szTip = "Sanctuary: ladder match found",
                };
                // No NIF_MESSAGE: the shell sends the game's window nothing.
                if (!_trayIconUp)
                {
                    if (!Shell_NotifyIcon(NIM_ADD, ref data))
                    {
                        Logger.LogWarning("Matchmaking: the notification could not be shown (tray icon refused).");
                        return;
                    }
                    _trayIconUp = true;
                    _trayWindow = hwnd;
                }
                data.uFlags = NIF_INFO;
                data.szInfoTitle = Truncate(title, 60);
                data.szInfo = Truncate(text, 250);
                data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON | NIIF_NOSOUND;
                data.hBalloonIcon = _balloonIcon;
                if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
                {
                    Logger.LogWarning("Matchmaking: the notification could not be shown.");
                }
                _trayIconUntil = Time.realtimeSinceStartup + 30f;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: notification failed: {e.Message}");
            }
        }

        private void RemoveTrayIcon()
        {
            if (!_trayIconUp) return;
            _trayIconUp = false;
            try
            {
                var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _trayWindow, uID = TrayIconId };
                Shell_NotifyIcon(NIM_DELETE, ref data);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Matchmaking: tray icon could not be removed: {e.Message}");
            }
        }
    }
}
