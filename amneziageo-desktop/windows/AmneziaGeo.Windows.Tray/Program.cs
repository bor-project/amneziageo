using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The tray anchor entry point: a hidden owner window drives a Shell notify icon, a background link keeps the
/// agent tunnel alive and feeds the icon its colour and tooltip, balloons announce the connect start,
/// completion, and failure while the GUI is not in front. A left click (or the menu's Open) surfaces the
/// lightweight launcher, whose More button expands it to the full configuration console; the menu also
/// connects, disconnects, or exits. A cold launch opens the launcher; the right-click menu is never
/// auto-shown (#187). Launched with --connect (post-install auto-connect), it dials the active profile
/// straight away and stays resident, no window (#188).
/// </summary>
internal static unsafe class Program
{
    // Matches the GUI's AppUserModelID so a taskbar pin groups with the launched window.
    private const string AppUserModelId = "AmneziaGeo.AmneziaGeo";
    private const string ClassName = "AmneziaGeoTray";

    // Fallback so the cold-launch popup still shows if no snapshot arrives.
    private const uint PopupTimerId = 1;
    private const uint PopupTimeoutMs = 1200;

    // Uptime ceiling for treating a tray start as a post-reboot logon autostart.
    private const long BootConnectWindowMs = 300_000;

    private static nint _hwnd;
    private static nint[] _icons = [];
    private static int _current;
    private static uint _taskbarCreatedMsg;
    private static nint _classNamePtr;
    private static bool _popupPending;
    private static bool _autoConnect;
    private static bool _showConsole;

    // App-update notifications: the last found / downloaded versions announced (so a transient re-report does
    // not re-notify), and which action a click on the most recent balloon takes.
    private static string _lastNotifiedUpdateVersion = string.Empty;
    private static string _lastDownloadedNotifiedVersion = string.Empty;
    private static BalloonAction _lastBalloonAction;

    // What a click on the most recent balloon does.
    private enum BalloonAction
    {
        None,
        Download,
        Install,
    }

    // Post-update install balloon deferred until the first snapshot reports the notifications flag.
    private static bool _pendingInstalledBalloon;

    // The post-update origin marker (settings / launcher / none), read at startup; null on a plain launch.
    private static string? _updateOrigin;

    // Logon autostart: resident tray icon only, no launcher window.
    private static bool _autostart;
    private static bool _stateInitialized;

    // Set when the tray starts shortly after OS boot: lets the first snapshot announce a tunnel that
    // survive-reboot brought up before the user session began.
    private static bool _startedDuringBoot;

    // State the current transition started from; -1 until one is seen.
    private static int _transitionFrom = -1;

    // Whether the current transition is a user disconnect (the agent's "disconnecting"), so its completion is a
    // clean disconnect (#13) rather than a dropped or failed tunnel.
    private static bool _transitionIsDisconnect;

    // Whether a disconnect failure is currently latched, so its rising edge fires the balloon just once (#14).
    private static bool _disconnectFailedActive;

    // GUI process ids targeted by the current Exit close-sweep.
    private static HashSet<uint> _uiPids = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // Post-install auto-connect (#188): dial the active profile on cold launch instead of showing the launcher.
        _autoConnect = Array.IndexOf(args, "--connect") >= 0;

        // After an in-app update the installer passes --settings so the cold launch reopens the settings console
        // the update was started from, instead of the launcher.
        _showConsole = Array.IndexOf(args, "--settings") >= 0;

        // Logon autostart (survive-reboot): bring up the resident tray icon only, no launcher window.
        _autostart = Array.IndexOf(args, "--autostart") >= 0;

        // A tray that comes up within minutes of boot is a post-reboot logon autostart: an already-up tunnel is
        // still news, so its first snapshot earns a connected balloon.
        _startedDuringBoot = Environment.TickCount64 < BootConnectWindowMs;

        Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // A second launch (shortcut clicked again) hands the running tray the "open GUI" nudge and exits.
        if (!SingleInstance.TryAcquire())
        {
            return 0;
        }

        Labels.Load();

        // Read the post-update origin before the icon can be clicked: a click launches the GUI, which consumes
        // the marker, and the cold-launch resolve below would then miss the install announcement.
        _updateOrigin = ReadUpdateOrigin();

        _classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var hInstance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)sizeof(Native.WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
            hInstance = hInstance,
            lpszClassName = _classNamePtr,
        };
        Native.RegisterClassExW(ref wc);

        // Re-add the icon if Explorer restarts.
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

        // A normal (never shown) window: it can take the foreground for the menu and receive the broadcast.
        _hwnd = Native.CreateWindowExW(0, ClassName, "AmneziaGeo", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);

        _icons =
        [
            MakeStateIcon(0x7B, 0x81, 0x8D), // disconnected: grey
            MakeStateIcon(0xE0, 0x90, 0x2F), // transitioning: orange
            MakeStateIcon(0x2A, 0x6F, 0xDB), // connected: blue
        ];
        AddOrModifyIcon(Native.NIM_ADD);

        AgentLink.Start(
            (state, connectFailed, disconnecting, disconnectFailed, linkDown) => Native.PostMessageW(_hwnd, Native.WM_STATE, (nuint)state, (connectFailed ? 1 : 0) | (disconnecting ? 2 : 0) | (disconnectFailed ? 4 : 0) | (linkDown ? 8 : 0)),
            (available, _) => Native.PostMessageW(_hwnd, Native.WM_UPDATE, (nuint)(available ? 1 : 0), 0),
            () => Native.PostMessageW(_hwnd, Native.WM_UPDATEDOWNLOADED, 0, 0),
            () => Native.PostMessageW(_hwnd, Native.WM_UPDATEFAILED, 0, 0),
            () => Native.PostMessageW(_hwnd, Native.WM_CHECKDONE, (nuint)(AgentLink.CheckFailed ? 2 : AgentLink.UpdateAvailable ? 1 : 0), 0));
        SingleInstance.ListenForActivation(_hwnd, Native.WM_OPENUI);
        SingleInstance.ListenForQuit(_hwnd, Native.WM_QUITTRAY);

        // Cold launch: bring up only the tray, then resolve once the first snapshot lands (or after a short
        // fallback) - open the launcher with an active profile, or the full GUI without one.
        _popupPending = true;
        Native.SetTimer(_hwnd, PopupTimerId, PopupTimeoutMs, 0);

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        foreach (var icon in _icons)
        {
            Native.DestroyIcon(icon);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg == Native.WM_TRAY)
            {
                var ev = (uint)(lParam & 0xFFFF);
                if (ev == Native.WM_LBUTTONUP)
                {
                    LaunchUi("--launcher");
                }
                else if (ev == Native.WM_RBUTTONUP)
                {
                    ShowMenuAtCursor();
                }
                else if (ev == Native.NIN_BALLOONUSERCLICK)
                {
                    // The click consumes the balloon, and no timeout follows one, so clear the action here too.
                    var action = _lastBalloonAction;
                    _lastBalloonAction = BalloonAction.None;
                    switch (action)
                    {
                        case BalloonAction.Download:
                            LaunchDownload();
                            break;
                        case BalloonAction.Install:
                            LaunchInstall();
                            break;
                        default:
                            LaunchUi("--launcher");
                            break;
                    }
                }
                else if (ev == Native.NIN_BALLOONTIMEOUT)
                {
                    // The balloon left the screen, so this flag no longer tells which one a click refers to: on
                    // Win10 the toast is parked in the Action Center and can be clicked hours later (Win11 drops
                    // it). Forget it, and such a click opens the launcher - whose update section is one click from
                    // the same place - rather than silently downloading or installing without the user asking.
                    _lastBalloonAction = BalloonAction.None;
                }

                return 0;
            }

            if (msg == Native.WM_COMMAND)
            {
                OnCommand((int)(wParam & 0xFFFF), hWnd);
                return 0;
            }

            if (msg == Native.WM_STATE)
            {
                SetState((int)wParam, (lParam & 1) != 0, (lParam & 2) != 0, (lParam & 4) != 0, (lParam & 8) != 0);
                if (_popupPending)
                {
                    ResolveColdLaunch();
                }

                return 0;
            }

            if (msg == Native.WM_UPDATE)
            {
                OnUpdateSignal(wParam != 0);
                return 0;
            }

            if (msg == Native.WM_UPDATEDOWNLOADED)
            {
                OnDownloadedSignal();
                return 0;
            }

            if (msg == Native.WM_UPDATEFAILED)
            {
                OnDownloadFailedSignal();
                return 0;
            }

            if (msg == Native.WM_CHECKDONE)
            {
                OnCheckDoneSignal((int)wParam);
                return 0;
            }

            if (msg == Native.WM_TIMER && wParam == PopupTimerId)
            {
                if (_popupPending)
                {
                    ResolveColdLaunch();
                }

                return 0;
            }

            if (msg == Native.WM_OPENUI)
            {
                LaunchUi("--launcher");
                return 0;
            }

            if (msg == Native.WM_QUITTRAY)
            {
                // The installer is retiring this tray for an upgrade: tear down cleanly (WM_DESTROY removes the icon).
                Native.DestroyWindow(hWnd);
                return 0;
            }

            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                AddOrModifyIcon(Native.NIM_ADD);
                return 0;
            }

            if (msg == Native.WM_DESTROY)
            {
                RemoveIcon();
                Native.PostQuitMessage(0);
                return 0;
            }
        }
        catch
        {
            // A message handler must never throw across the native boundary.
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void OnCommand(int id, nint hWnd)
    {
        switch (id)
        {
            case Native.ID_OPEN:
                LaunchUi("--launcher");
                break;
            case Native.ID_CONNECT:
                AgentLink.SendConnect();
                break;
            case Native.ID_DISCONNECT:
                // Disconnect leaves the tray resident.
                AgentLink.SendDisconnect();
                break;
            case Native.ID_CHECKUPDATE:
                AgentLink.SendCheckUpdate();
                break;
            case Native.ID_UPDATE:
                LaunchDownload();
                break;
            case Native.ID_INSTALL:
                LaunchInstall();
                break;
            case Native.ID_EXIT:
                // A download in flight is cancelled cleanly (its partial deleted) only on a confirmed exit;
                // declining keeps the tray resident so the download keeps running (#21).
                if (AgentLink.DownloadInProgress)
                {
                    if (!ConfirmExitDuringDownload())
                    {
                        break;
                    }

                    AgentLink.SendCancelDownload();
                    WaitForDownloadToStop();
                }

                // Exit tears the tunnel down (if up), closes any open GUI window, and unloads the tray.
                if (_current != 0)
                {
                    AgentLink.SendDisconnect();
                }

                CloseUi();
                Native.DestroyWindow(hWnd);
                break;
        }
    }

    // A modal Yes/No before an exit that would cancel a running download; returns whether the user confirmed.
    private static bool ConfirmExitDuringDownload()
    {
        Native.SetForegroundWindow(_hwnd);
        return Native.MessageBoxW(_hwnd, Labels.ExitDownloadPrompt, "AmneziaGeo", Native.MB_YESNO | Native.MB_ICONWARNING | Native.MB_TOPMOST) == Native.IDYES;
    }

    // Gives the UI byte-pump a moment to observe the cancel, delete its partial, and report the download stopped
    // before the exit force-closes it; a leftover partial is dropped on the next UI start regardless.
    private static void WaitForDownloadToStop()
    {
        for (var i = 0; i < 30 && AgentLink.DownloadInProgress; i++)
        {
            Thread.Sleep(100);
        }
    }

    private static void SetState(int state, bool connectFailed, bool disconnecting, bool disconnectFailed, bool linkDown)
    {
        if (state < 0 || state > 2 || _icons.Length != 3)
        {
            return;
        }

        // Balloon on real transitions only, never on the first snapshot (a tray restart over a live tunnel must
        // not fire a spurious notice). Connect start is the 0->transitioning edge; disconnect start is the
        // connected->transitioning edge, but only when the agent reports "disconnecting" - a liveness re-dial
        // uses the same transitioning state under "connecting", so the flag tells a user teardown from a
        // reconnect. A 1->0 edge is a failed dial when it opened at disconnected, and when it opened at connected
        // it is a clean user disconnect (#13, "disconnecting" seen) or a dropped tunnel (#192, connect failure
        // latched).
        var justConnecting = _stateInitialized && state == 1 && _current == 0;
        var justConnected = _stateInitialized && state == 2 && _current != 2;
        var justDisconnecting = _stateInitialized && state == 1 && _current == 2 && disconnecting;
        var justFailed = _stateInitialized && state == 0 && _current == 1 && _transitionFrom == 0 && connectFailed;
        var justDropped = _stateInitialized && state == 0 && _current == 1 && _transitionFrom == 2 && connectFailed;
        var justDisconnected = _stateInitialized && state == 0 && _current == 1 && _transitionFrom == 2 && _transitionIsDisconnect;

        // A stalled teardown latches the disconnect-failed flag while the state holds (connected), so this rides
        // the flag's rising edge rather than a state transition (#14).
        var justDisconnectFailed = _stateInitialized && disconnectFailed && !_disconnectFailedActive;

        // A survive-reboot tunnel that came up before this post-boot tray started: the first snapshot is already
        // connected, and that is still the news. A later tray start (upgrade, re-logon on a long-lived session)
        // has a high uptime, so its first connected snapshot stays silent.
        var bootConnected = !_stateInitialized && state == 2 && _startedDuringBoot;

        // Records which edge opened the transition and whether it is a user disconnect; skipped on the first
        // snapshot, whose origin is unknown. A "disconnecting" arriving after a re-dial already opened the
        // transition still marks it a disconnect.
        if (_stateInitialized && state == 1 && _current != 1)
        {
            _transitionFrom = _current;
            _transitionIsDisconnect = disconnecting;
        }
        else if (state == 1 && disconnecting)
        {
            _transitionIsDisconnect = true;
        }

        // A lost agent link is synthetic, not an observed transition, so forget the open transition and never
        // announce a completion that was not seen.
        if (linkDown)
        {
            _transitionFrom = -1;
            _transitionIsDisconnect = false;
        }

        _current = state;
        _stateInitialized = true;
        // Re-arm the disconnect-failed edge once the failure clears; a shown balloon marks it below.
        if (!disconnectFailed)
        {
            _disconnectFailedActive = false;
        }

        AddOrModifyIcon(Native.NIM_MODIFY);

        // The post-update install balloon waited to learn the notifications flag; a synthetic disconnected state
        // carries no snapshot, so keep holding until a real one lands.
        if (_pendingInstalledBalloon && AgentLink.SnapshotSeen)
        {
            _pendingInstalledBalloon = false;
            ShowBalloon("AmneziaGeo", Labels.UpdateInstalledInfo);
        }

        // A visible launcher or settings window already reflects the change, so surface a connection balloon only
        // when no GUI window is on screen (auto-connect with no window, tray menu, after the launcher has closed,
        // or while it sits minimized). Per the shared notification rules (#5), visibility, not foreground.
        if (!linkDown && !NotificationGate.IsUiVisible())
        {
            // A stalled teardown warns first: its flag can coincide with a re-latch right after an agent-link
            // drop (state 1 from _current 0), so it must win over the connect edges (#14). Marked shown here so
            // it fires once per latch, even when it was suppressed while the GUI was visible.
            if (justDisconnectFailed)
            {
                _disconnectFailedActive = true;
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.DisconnectFailedInfo, Native.NIIF_WARNING);
            }
            // A connection balloon supersedes the update one, so a later balloon click opens the launcher.
            else if (justConnected || bootConnected)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.ConnectedInfo);
            }
            else if (justConnecting)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.ConnectingInfo);
            }
            else if (justDisconnecting)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.DisconnectingInfo);
            }
            else if (justDisconnected)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.DisconnectedInfo);
            }
            else if (justFailed)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.ConnectFailedInfo, Native.NIIF_WARNING);
            }
            else if (justDropped)
            {
                _lastBalloonAction = BalloonAction.None;
                ShowBalloon("AmneziaGeo", Labels.ConnectionLostInfo, Native.NIIF_WARNING);
            }
        }
    }

    // Whether the foreground window belongs to the GUI process; when it does, its own animation covers the state
    // change and a balloon would be noise.
    private static bool IsUiForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == 0)
        {
            return false;
        }

        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "AmneziaGeo.Windows.Ui", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Cold launch, once: open the lightweight launcher (More expands it to the full console). Post-install
    // auto-connect with an active profile dials straight away and stays resident, no window (#188).
    private static void ResolveColdLaunch()
    {
        _popupPending = false;
        Native.KillTimer(_hwnd, PopupTimerId);

        var autoDial = _autoConnect && AgentLink.HasActiveProfile;
        if (autoDial)
        {
            AgentLink.SendConnect();
        }

        // A relaunch right after a self-update carries an origin marker: announce the install and return to the
        // surface the update was started from (settings / launcher); "none" stays windowless. The UI clears the
        // marker on its own read; the windowless case has no reader, so clear it here.
        var origin = _updateOrigin;
        if (origin is not null)
        {
            // Hold the balloon until a snapshot has told us whether notifications are on: resolving off the
            // fallback timer (agent still starting) would otherwise announce on the default-true flag.
            if (AgentLink.SnapshotSeen)
            {
                ShowBalloon("AmneziaGeo", Labels.UpdateInstalledInfo);
            }
            else
            {
                _pendingInstalledBalloon = true;
            }

            if (origin == "settings")
            {
                LaunchUi();
            }
            else if (origin == "launcher")
            {
                LaunchUi("--launcher");
            }
            else
            {
                DeleteUpdateOrigin();
            }

            return;
        }

        // An update applied from the settings console reopens the console (launched without --launcher) so the
        // user lands back where they started; a plain cold launch opens the launcher; a post-install auto-connect
        // (#188) or a logon autostart (--autostart) otherwise stays windowless, resident icon only.
        if (_showConsole)
        {
            LaunchUi();
        }
        else if (!autoDial && !_autostart)
        {
            LaunchUi("--launcher");
        }
    }

    // An update-availability change from the agent link: balloon once per newly-available version (when
    // notifications are on and the GUI is not in front), remembering it so a balloon click starts the update.
    // Dedup is by version alone and never reset, so a check that transiently fails (availability flapping off
    // and back on) cannot re-announce the same version.
    private static void OnUpdateSignal(bool available)
    {
        if (!available)
        {
            return;
        }

        var version = AgentLink.UpdateVersion;
        if (string.Equals(version, _lastNotifiedUpdateVersion, StringComparison.Ordinal))
        {
            return;
        }

        if (NotificationGate.CanNotify() && !IsUiForeground())
        {
            _lastNotifiedUpdateVersion = version;
            _lastBalloonAction = BalloonAction.Download;
            ShowBalloon("AmneziaGeo", string.Format(Labels.UpdateFoundInfo, version));
        }
    }

    // A download-completed edge from the agent link: balloon once per newly-downloaded version (when
    // notifications are on and the GUI is not in front), so a click starts the install. Dedup is by version so
    // a reconnect over a still-ready download cannot re-announce it.
    private static void OnDownloadedSignal()
    {
        if (!AgentLink.UpdateDownloaded)
        {
            return;
        }

        var version = AgentLink.UpdateVersion;
        if (string.Equals(version, _lastDownloadedNotifiedVersion, StringComparison.Ordinal))
        {
            return;
        }

        if (NotificationGate.CanNotify() && !IsUiForeground())
        {
            _lastDownloadedNotifiedVersion = version;
            _lastBalloonAction = BalloonAction.Install;
            ShowBalloon("AmneziaGeo", string.Format(Labels.UpdateDownloadedInfo, version));
        }
    }

    // A download-failure edge from the agent link: warn once per failure (when notifications are on and the GUI
    // is not in front, which already shows the error inline). The link re-arms the edge on the next attempt.
    private static void OnDownloadFailedSignal()
    {
        if (NotificationGate.CanNotify() && !IsUiForeground())
        {
            _lastBalloonAction = BalloonAction.None;
            ShowBalloon("AmneziaGeo", Labels.UpdateDownloadFailedInfo, Native.NIIF_WARNING);
        }
    }

    // A manual update check finished (the agent link reports it). Announce the up-to-date result when the check
    // found no update (outcome 0); an available update rides the WM_UPDATE "found" balloon, and a failed check
    // just restores the check action in the menu, per the issue (#15).
    private static void OnCheckDoneSignal(int outcome)
    {
        if (outcome != 0)
        {
            return;
        }

        if (NotificationGate.CanNotify() && !IsUiForeground())
        {
            _lastBalloonAction = BalloonAction.None;
            ShowBalloon("AmneziaGeo", Labels.UpToDateInfo);
        }
    }

    // Starts a background download in the GUI process (windowless); the tray announces "ready to install" when
    // it completes.
    private static void LaunchDownload()
    {
        LaunchUi("--update");
    }

    // Installs the already-downloaded setup in the GUI process (verify + launch, windowless).
    private static void LaunchInstall()
    {
        LaunchUi("--apply");
    }

    // Reads the post-update origin marker (settings / launcher / none) without clearing it, so the UI can also
    // consume it when a stale resident tray surfaces the launcher instead.
    private static string? ReadUpdateOrigin()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo",
                "update-origin");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteUpdateOrigin()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo",
                "update-origin");
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void ShowMenuAtCursor()
    {
        Native.GetCursorPos(out var pt);
        ShowMenuAt(pt.x, pt.y, Native.TPM_RIGHTBUTTON);
    }

    private static void ShowMenuAt(int x, int y, uint alignFlags)
    {
        Native.SetForegroundWindow(_hwnd);

        var menu = BuildMenu();
        Native.TrackPopupMenu(menu, alignFlags, x, y, 0, _hwnd, 0);
        // Classic dismissal fix: post a benign message so the menu closes when clicked away.
        Native.PostMessageW(_hwnd, Native.WM_NULL, 0, 0);
        Native.DestroyMenu(menu);
    }

    // Builds the menu for the current state: Open (launcher) always; Connect (grey without an active profile)
    // when down, Disconnect when up, an inactive "Connecting…/Disconnecting…" while a transition runs
    // (#18/#19); Exit always.
    private static nint BuildMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_OPEN, Labels.Open);
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);

        if (_current == 0)
        {
            var connectFlags = AgentLink.HasActiveProfile ? Native.MF_STRING : Native.MF_STRING | Native.MF_GRAYED;
            Native.AppendMenuW(menu, connectFlags, (nuint)Native.ID_CONNECT, Labels.Connect);
        }
        else if (_current == 2)
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_DISCONNECT, Labels.Disconnect);
        }
        else if (_transitionIsDisconnect)
        {
            // Disconnecting: an inactive item names the direction; a second disconnect is not offered (#19).
            Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, 0, Labels.StatusDisconnecting);
        }
        else
        {
            // Connecting: an inactive "Connecting…" blocks a second connect (#18), while an active Disconnect
            // stays available to abort the dial - important during a long auto-reconnect retry, which also
            // reports the transitioning state.
            Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, 0, Labels.StatusConnecting);
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_DISCONNECT, Labels.Disconnect);
        }

        // Update items only on builds with an update channel configured, matching the console and launcher,
        // which hide their whole update section without one. A single item reflects the update state: an
        // inactive "Checking…" while a check runs (#15), Install once a setup is downloaded, Download when an
        // update is available - replacing the check action (#16) and inactive while a download runs so a second
        // cannot start - else Check.
        if (AgentLink.HasUpdateUrl)
        {
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            if (AgentLink.CheckInProgress)
            {
                Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, (nuint)Native.ID_CHECKUPDATE, Labels.CheckingUpdate);
            }
            else if (AgentLink.UpdateDownloaded)
            {
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_INSTALL, Labels.InstallUpdate);
            }
            else if (AgentLink.UpdateAvailable)
            {
                var downloadFlags = AgentLink.DownloadInProgress ? Native.MF_STRING | Native.MF_GRAYED : Native.MF_STRING;
                Native.AppendMenuW(menu, downloadFlags, (nuint)Native.ID_UPDATE, Labels.DownloadUpdate);
            }
            else
            {
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_CHECKUPDATE, Labels.CheckUpdate);
            }
        }

        Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
        // Signpost that quitting drops a live tunnel, so protection is never lost without notice.
        Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_EXIT, _current != 0 ? Labels.ExitConnected : Labels.Exit);
        return menu;
    }

    private static void AddOrModifyIcon(uint dwMessage)
    {
        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = Native.WM_TRAY,
            hIcon = _icons.Length == 3 ? _icons[_current] : 0,
        };
        SetTip(&nid, TipForState());
        Native.Shell_NotifyIconW(dwMessage, ref nid);
    }

    private static void RemoveIcon()
    {
        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
        };
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref nid);
    }

    private static void SetTip(Native.NOTIFYICONDATAW* nid, string text)
    {
        var count = Math.Min(text.Length, 127);
        for (var i = 0; i < count; i++)
        {
            nid->szTip[i] = text[i];
        }

        nid->szTip[count] = '\0';
    }

    // Pops a system balloon (a toast on Win10/11) on the existing icon. Gated centrally by the notifications
    // setting and the OS permission; a show failure is swallowed so it never disturbs the underlying operation.
    private static void ShowBalloon(string title, string text, uint infoFlags = Native.NIIF_INFO)
    {
        if (!NotificationGate.CanNotify())
        {
            return;
        }

        if (_icons.Length != 3)
        {
            return;
        }

        try
        {
            var nid = new Native.NOTIFYICONDATAW
            {
                cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = 1,
                uFlags = Native.NIF_INFO,
                dwInfoFlags = infoFlags,
            };
            SetInfo(&nid, title, text);
            Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
        }
        catch
        {
        }
    }

    private static void SetInfo(Native.NOTIFYICONDATAW* nid, string title, string text)
    {
        var titleCount = Math.Min(title.Length, 63);
        for (var i = 0; i < titleCount; i++)
        {
            nid->szInfoTitle[i] = title[i];
        }

        nid->szInfoTitle[titleCount] = '\0';

        var infoCount = Math.Min(text.Length, 255);
        for (var i = 0; i < infoCount; i++)
        {
            nid->szInfo[i] = text[i];
        }

        nid->szInfo[infoCount] = '\0';
    }

    // Tooltip tracks the tunnel state; the transitional state names the direction from the edge that opened it.
    private static string TipForState()
    {
        return _current switch
        {
            2 => $"AmneziaGeo - {Labels.StatusConnected}",
            0 => $"AmneziaGeo - {Labels.StatusDisconnected}",
            _ => $"AmneziaGeo - {(_transitionIsDisconnect ? Labels.StatusDisconnecting : Labels.StatusConnecting)}",
        };
    }

    // Mirrors the installer's own GUI shutdown (StopRunningApp): WM_CLOSE every open GUI window - both the
    // launcher and the full window (#189) - then a forced kill only if a process hangs, so no window left by
    // --launcher or the full GUI outlives the tray it was supposed to exit with.
    private static void CloseUi()
    {
        var processes = Process.GetProcessesByName("AmneziaGeo.Windows.Ui");
        if (processes.Length == 0)
        {
            return;
        }

        _uiPids = new HashSet<uint>();
        foreach (var p in processes)
        {
            try
            {
                _uiPids.Add((uint)p.Id);
            }
            catch
            {
            }
        }

        Native.EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumCloseUiWindow, 0);

        foreach (var p in processes)
        {
            try
            {
                if (!p.WaitForExit(5000))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    // Closes every visible top-level window owned by a targeted GUI process.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumCloseUiWindow(nint hWnd, nint lParam)
    {
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if (_uiPids.Contains(pid) && Native.IsWindowVisible(hWnd))
        {
            Native.PostMessageW(hWnd, Native.WM_CLOSE, 0, 0);
        }

        return 1;
    }

    private static void LaunchUi(string? arg = null)
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.Ui.exe");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (!string.IsNullOrEmpty(arg))
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
        }
        catch
        {
        }
    }

    // Draws the app glyph (white power symbol on a state-coloured disc) as an HICON: a 32bpp colour bitmap plus
    // a 1bpp mask that carves the disc shape.
    private static nint MakeStateIcon(byte r, byte g, byte b)
    {
        const int size = 32;
        const double c = 15.5;
        const double discR = 15.0;
        // Power glyph: an open ring (gap at top) plus a vertical stroke through the gap.
        const double ringOuter = 7.5;
        const double ringInner = 5.0;
        const double gapHalf = 2.3;
        const double barHalf = 1.35;
        const double barTop = -9.0;
        const double barBottom = 0.5;

        var color = new uint[size * size];
        var mask = new byte[size * (size / 8)];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d > discR)
                {
                    mask[(y * (size / 8)) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    continue;
                }

                var inRing = d >= ringInner && d <= ringOuter && !(dy < 0 && Math.Abs(dx) < gapHalf);
                var inBar = Math.Abs(dx) <= barHalf && dy >= barTop && dy <= barBottom;
                var white = inRing || inBar;
                var pr = white ? (byte)255 : r;
                var pg = white ? (byte)255 : g;
                var pb = white ? (byte)255 : b;
                color[(y * size) + x] = 0xFF000000u | ((uint)pr << 16) | ((uint)pg << 8) | pb;
            }
        }

        nint hColor;
        nint hMask;
        fixed (uint* colorBits = color)
        {
            hColor = Native.CreateBitmap(size, size, 1, 32, (nint)colorBits);
        }

        fixed (byte* maskBits = mask)
        {
            hMask = Native.CreateBitmap(size, size, 1, 1, (nint)maskBits);
        }

        var info = new Native.ICONINFO { fIcon = 1, hbmMask = hMask, hbmColor = hColor };
        var hIcon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(hColor);
        Native.DeleteObject(hMask);
        return hIcon;
    }
}
