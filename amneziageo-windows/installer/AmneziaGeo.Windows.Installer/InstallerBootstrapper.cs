using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AmneziaGeo.Localization;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// AmneziaGeo WiX v5 bootstrapper application.
/// </summary>
public sealed class InstallerBootstrapper : BootstrapperApplication
{
    private const string MsiPackageId = "AmneziaGeoMsi";

    // What to run after the installed newer bundle is removed by its own uninstaller.
    private enum ChainStep
    {
        None,
        Remove,
        Downgrade,
    }

    private Dispatcher _dispatcher = null!;
    private InstallerViewModel _vm = null!;
    private IBootstrapperCommand _command = null!;
    private Window? _mainWindow;

    private bool _interactive;
    private bool _msiPresent;
    private bool _olderRelated;
    private bool _newerRelated;
    private string? _installedVersion;
    private string? _myVersion;
    private string? _newerVersion;
    private string? _newerBundleId;
    private bool _newerMissingFromCache;
    private ChainStep _chain;

    private InstallerAction _action;
    private LaunchAction _launch;
    private int _result;
    private InstallState _detectedState;

    private volatile bool _engineConnected;

    /// <summary>
    /// True once the Burn engine hosted this BA.
    /// </summary>
    public bool EngineConnected => _engineConnected;

    /// <inheritdoc/>
    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _engineConnected = true;
        _command = args.Command;
        _interactive = _command.Display == Display.Full && _command.Action != LaunchAction.Uninstall;
    }

    /// <inheritdoc/>
    protected override void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _vm = new InstallerViewModel(OnUserAction, OnUserClose);

        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectPackageComplete += OnDetectPackageComplete;
        DetectComplete += OnDetectComplete;
        PlanRelatedBundle += OnPlanRelatedBundle;
        PlanComplete += OnPlanComplete;
        ApplyBegin += OnApplyBegin;
        ElevateComplete += OnElevateComplete;
        Progress += OnProgress;
        ApplyComplete += OnApplyComplete;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AmneziaGeo.Windows.Installer;component/Theme/AmneziaGeo.xaml", UriKind.Absolute),
        });

        var window = new MainWindow { DataContext = _vm };
        new WindowInteropHelper(window).EnsureHandle();
        _mainWindow = window;

        engine.Detect();

        if (_command.Action == LaunchAction.Uninstall)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Run();
        }
        else
        {
            app.Run(window);
        }

        engine.Quit(_result);
    }

    // ---- Detect ----

    private void OnDetectRelatedBundle(object? sender, DetectRelatedBundleEventArgs e)
    {
        if (e.RelationType != RelationType.Upgrade)
        {
            return;
        }

        _myVersion ??= engine.GetVariableVersion("WixBundleVersion");
        _installedVersion = e.Version;
        if (engine.CompareVersions(e.Version, _myVersion) > 0)
        {
            _newerRelated = true;
            // Burn aborts a downgrade before it executes anything, so rolling back means running the installed
            // bundle's own uninstaller first: keep its identity.
            if (_newerVersion is null || engine.CompareVersions(e.Version, _newerVersion) > 0)
            {
                _newerVersion = e.Version;
                _newerBundleId = e.ProductCode;
                _newerMissingFromCache = e.MissingFromCache;
            }
        }
        else
        {
            _olderRelated = true;
        }
    }

    private void OnDetectPackageComplete(object? sender, DetectPackageCompleteEventArgs e)
    {
        if (string.Equals(e.PackageId, MsiPackageId, StringComparison.Ordinal) && e.State == PackageState.Present)
        {
            _msiPresent = true;
        }
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        // Populate for a fresh install too, where no related bundle event ran.
        _myVersion ??= engine.GetVariableVersion("WixBundleVersion");
        var state = _newerRelated
            ? InstallState.NewerInstalled
            : (_msiPresent || _olderRelated) ? InstallState.Installed : InstallState.NotInstalled;
        _detectedState = state;

        if (_chain != ChainStep.None)
        {
            ContinueChain(state);
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            _vm.SetDetected(state, _installedVersion, _myVersion);

            if (!_interactive)
            {
                var mapped = MapCommandAction(_command.Action, state);
                if (state == InstallState.NewerInstalled && mapped is InstallerAction.Install or InstallerAction.Update)
                {
                    // Nobody is here to confirm removing the newer install, and Burn cannot downgrade in one run.
                    Finish(false, Loc.Instance.Get("InstallerBa_DowngradeBlocked"));
                    return;
                }

                OnUserAction(mapped);
            }
            else if (IsUpdateFlow())
            {
                _vm.StageUpdate();
            }
        });

        // A previously-installed machine may have a connectable profile: ask the running agent so the options
        // step can offer the nested auto-connect checkbox (#188).
        if (_interactive && state == InstallState.Installed)
        {
            ProbeAutoConnect();
        }
    }

    private bool IsUpdateFlow()
    {
        return string.Equals(engine.GetVariableString("UPDATEFLOW"), "1", StringComparison.Ordinal);
    }

    private void ProbeAutoConnect()
    {
        _ = Task.Run(async () =>
        {
            var connectable = await AgentPipeClient.HasConnectableProfileAsync(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), CancellationToken.None);
            if (connectable)
            {
                _ = _dispatcher.BeginInvoke(() => _vm.SetCanAutoConnect(true));
            }
        });
    }

    // ---- Rollback over a newer install ----

    // Second detect, after the installed newer bundle was removed by its own uninstaller.
    private void ContinueChain(InstallState state)
    {
        var next = _chain;
        _chain = ChainStep.None;

        _dispatcher.BeginInvoke(() =>
        {
            if (state == InstallState.NewerInstalled)
            {
                Finish(false, Loc.Instance.Get("InstallerBa_RemoveNewerFailed"));
                return;
            }

            if (next == ChainStep.Remove)
            {
                Finish(true, SuccessText(InstallerAction.Remove));
                return;
            }

            _vm.SetDetected(state, _installedVersion, _myVersion);
            OnUserAction(InstallerAction.Install);
        });
    }

    // Runs the installed bundle's own cached setup to remove it, then re-detects.
    private void RemoveNewerThen(ChainStep next)
    {
        var command = FindUninstallCommand();
        if (command is null)
        {
            Finish(false, Loc.Instance.Get("InstallerBa_NewerNotFound"));
            return;
        }

        var exe = command.Value.Exe;
        var args = command.Value.Args;
        var wipe = next == ChainStep.Remove && _vm.DeleteConfig;
        _vm.BeginRemoveNewer();

        _ = Task.Run(() =>
        {
            var ok = RunUninstaller(exe, args, wipe);
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (!ok)
                {
                    Finish(false, Loc.Instance.Get("InstallerBa_RemoveNewerFailed"));
                    return;
                }

                _chain = next;
                ResetDetect();
                engine.Detect();
            });
        });
    }

    // Burn re-raises the whole detect sequence, so the accumulated flags start clean.
    private void ResetDetect()
    {
        _msiPresent = false;
        _olderRelated = false;
        _newerRelated = false;
        _installedVersion = null;
        _newerVersion = null;
        _newerBundleId = null;
        _newerMissingFromCache = false;
    }

    // The uninstall command the installed bundle registered for itself.
    private (string Exe, string Args)? FindUninstallCommand()
    {
        if (string.IsNullOrEmpty(_newerBundleId) || _newerMissingFromCache)
        {
            return null;
        }

        var subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + _newerBundleId;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var found = ReadUninstallCommand(hive, view, subKey);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static (string Exe, string Args)? ReadUninstallCommand(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(subKey);
            var quiet = key?.GetValue("QuietUninstallString") as string;
            var line = string.IsNullOrWhiteSpace(quiet) ? key?.GetValue("UninstallString") as string : quiet;
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var (exe, args) = SplitCommand(line);
            if (!File.Exists(exe))
            {
                return null;
            }

            return (exe, string.IsNullOrWhiteSpace(quiet) ? (args + " /quiet").Trim() : args);
        }
        catch
        {
            return null;
        }
    }

    // "C:\...\setup.exe" /uninstall /quiet -> executable plus the rest.
    private static (string Exe, string Args) SplitCommand(string line)
    {
        line = line.Trim();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            if (end > 0)
            {
                return (line[1..end], line[(end + 1)..].Trim());
            }
        }

        var space = line.IndexOf(' ');
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    private static bool RunUninstaller(string exe, string args, bool wipe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = wipe ? (args + " DELETECONFIG=1").Trim() : args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit();
            return p.ExitCode is 0 or 3010 or 1641;
        }
        catch
        {
            return false;
        }
    }

    // ---- Plan ----

    private void OnPlanRelatedBundle(object? sender, PlanRelatedBundleEventArgs e)
    {
        if (_launch == LaunchAction.Install && _command.Relation == RelationType.None)
        {
            e.State = RequestState.Absent;
        }
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            Finish(false, Loc.Instance.Get("InstallerBa_PlanFailed"));
            return;
        }
        if (!StopRunningApp())
        {
            Finish(false, Loc.Instance.Get("InstallerBa_StopAppFailed"));
            return;
        }
        engine.Apply(WindowHandle);
    }

    // Retire the resident tray before the MSI runs: signal it to quit (its icon is removed and Tray.exe freed),
    // then force any straggler so a major upgrade can replace the file and start a fresh instance.
    private static void StopTray()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\AmneziaGeo.Tray.Quit", out var quit))
            {
                using (quit)
                {
                    quit.Set();
                }
            }
        }
        catch
        {
        }

        for (var i = 0; i < 30 && Process.GetProcessesByName("AmneziaGeo.Windows.Tray").Length > 0; i++)
        {
            Thread.Sleep(100);
        }

        foreach (var p in Process.GetProcessesByName("AmneziaGeo.Windows.Tray"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
            }
        }
    }

    private static bool StopRunningApp()
    {
        StopTray();
        try
        {
            var procs = Process.GetProcessesByName("AmneziaGeo.Windows.Ui");
            if (procs.Length == 0)
            {
                return true;
            }
            foreach (var p in procs)
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(5000))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch
                {
                }
            }
            return Process.GetProcessesByName("AmneziaGeo.Windows.Ui").Length == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- Apply ----

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs e)
    {
        RaiseMainWindow();
    }

    private void OnElevateComplete(object? sender, ElevateCompleteEventArgs e)
    {
        if (e.Status >= 0)
        {
            RaiseMainWindow();
        }
    }

    private void RaiseMainWindow()
    {
        // A silent / passive / uninstall run (incl. a sibling bundle Burn removes on upgrade) shows no window;
        // Activate before the window is shown throws and kills the BA.
        if (!_interactive)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow is null || !_mainWindow.IsVisible)
            {
                return;
            }

            try
            {
                _mainWindow.Topmost = true;
                _mainWindow.Activate();
                _mainWindow.Topmost = false;
            }
            catch (InvalidOperationException)
            {
                // Window not shown yet.
            }
        });
    }

    private void OnProgress(object? sender, ProgressEventArgs e)
    {
        var percent = e.OverallPercentage;
        _dispatcher.BeginInvoke(() => _vm.ReportProgress(percent));
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            Finish(false, Loc.Instance.Get("InstallerBa_ApplyError", e.Status));
            return;
        }

        var isApply = _action is InstallerAction.Install or InstallerAction.Update or InstallerAction.Repair;
        if (!(isApply && _vm.DownloadLists))
        {
            Finish(true, SuccessText(_action));
            return;
        }

        _dispatcher.BeginInvoke(async () =>
        {
            // The continuation is guarded: a throw here (async void) would otherwise leave the UI stuck in the
            // Applying phase and, in silent mode, never call Shutdown. The geo download is best-effort and
            // never fails the run.
            var geo = string.Empty;
            try
            {
                geo = await RunGeoStepAsync();
            }
            catch (Exception ex)
            {
                geo = Loc.Instance.Get("InstallerBa_GeoUpdateFailed", ex.Message);
            }

            _result = 0;
            _vm.CompleteWithGeo(true, SuccessText(_action), geo);
            AfterComplete(true);
        });
    }

    private async Task<string> RunGeoStepAsync()
    {
        try
        {
            if (_detectedState != InstallState.NotInstalled)
            {
                _vm.BeginGeoCheck();
                var check = await AgentPipeClient.SendAsync(
                    "check-sources", [],
                    connectTimeout: TimeSpan.FromSeconds(20),
                    ackTimeout: TimeSpan.FromSeconds(60),
                    CancellationToken.None);

                if (check.GeoUpdatesAvailable == 0)
                {
                    return Loc.Instance.Get("InstallerBa_GeoUpToDate");
                }
            }

            _vm.BeginGeoDownload();
            var progress = new Progress<int>(p => _vm.ReportGeoProgress(p));
            var dl = await AgentPipeClient.SendAsync(
                "download-geo", [],
                connectTimeout: TimeSpan.FromSeconds(20),
                ackTimeout: TimeSpan.FromSeconds(180),
                CancellationToken.None,
                progress);
            return dl.Message;
        }
        catch (Exception ex)
        {
            return Loc.Instance.Get("InstallerBa_GeoUpdateFailed", ex.Message);
        }
    }

    // ---- User intent ----

    private void OnUserAction(InstallerAction action)
    {
        _action = action;

        // A newer install is not ours to plan against: this bundle is not the registered one, so Plan(Uninstall)
        // yields an empty plan and reports a false success, and Plan(Install) is refused as a downgrade. Both
        // buttons run the installed bundle's own uninstaller instead. Only a standalone interactive run does
        // this: when Burn itself launches this bundle to clear a related install, the newer bundle it would
        // find is the one currently installing.
        if (_interactive && _command.Relation == RelationType.None
            && _detectedState == InstallState.NewerInstalled
            && action is InstallerAction.Install or InstallerAction.Remove)
        {
            RemoveNewerThen(action == InstallerAction.Install ? ChainStep.Downgrade : ChainStep.Remove);
            return;
        }

        _launch = action switch
        {
            InstallerAction.Repair => LaunchAction.Repair,
            InstallerAction.Remove => LaunchAction.Uninstall,
            _ => LaunchAction.Install,
        };

        if (string.IsNullOrEmpty(engine.GetVariableString("DELETECONFIG")))
        {
            engine.SetVariableString("DELETECONFIG", _vm.DeleteConfig ? "1" : "0", false);
        }

        // Shortcut choices come from the view model - the checkboxes on an interactive run, or the per-user
        // saved options otherwise (#183) - so a silent/passive update honours the last choice instead of
        // resurrecting a removed shortcut. A command-line value (non-empty) wins.
        if (string.IsNullOrEmpty(engine.GetVariableString("DESKTOPSHORTCUT")))
        {
            engine.SetVariableString("DESKTOPSHORTCUT", _vm.DesktopShortcut ? "1" : "0", false);
        }

        if (string.IsNullOrEmpty(engine.GetVariableString("STARTMENUSHORTCUT")))
        {
            engine.SetVariableString("STARTMENUSHORTCUT", _vm.StartMenuShortcut ? "1" : "0", false);
        }

        _vm.BeginApply(action);
        engine.Plan(_launch);
    }

    private void OnUserClose()
    {
        // The launch-after choice is honoured automatically on a successful install/update (AfterComplete),
        // so the Done screen's Close button only ever needs to shut the installer down.
        Application.Current?.Shutdown();
    }

    /// <summary>
    /// After a completed run: on a successful install/update, honour the launch-after choice by starting the
    /// app and closing the installer without stopping on the Done screen (#165 interactive checkbox, #155
    /// passive update). Otherwise leave the Done screen up; in a non-interactive run there is no one to click
    /// Close, so shut down.
    /// </summary>
    private void AfterComplete(bool ok)
    {
        if (ok && ShouldLaunchAfter())
        {
            WriteResumeOrigin();
            LaunchApp(ShouldAutoConnect(), ShouldShowConsole());
            Application.Current?.Shutdown();
            return;
        }

        if (!_interactive)
        {
            Application.Current?.Shutdown();
        }
    }

    /// <summary>
    /// Whether to start the app after a successful run. Install/update only. An interactive run follows the
    /// options-step checkbox (#165). A non-interactive run launches when the in-app updater asked for it
    /// (LAUNCHAFTER=1) or when it is a passive update - the display level the in-app updater uses - so it works
    /// even for an update kicked off by an app build that predates the flag; a fully silent (/quiet) run never
    /// launches (#155).
    /// </summary>
    private bool ShouldLaunchAfter()
    {
        if (_action is not (InstallerAction.Install or InstallerAction.Update))
        {
            return false;
        }

        // An in-app update always relaunches so the user is not left without a window after it applies.
        if (IsUpdateFlow())
        {
            return true;
        }

        if (_interactive)
        {
            return _vm.LaunchOnClose;
        }

        return string.Equals(engine.GetVariableString("LAUNCHAFTER"), "1", StringComparison.Ordinal)
            || (_action == InstallerAction.Update && _command.Display == Display.Passive);
    }

    /// <summary>
    /// Whether the post-install launch should immediately dial the existing connection (#188). Interactive runs
    /// follow the nested checkbox; a non-interactive run reads the AUTOCONNECT variable. Install/update only.
    /// </summary>
    private bool ShouldAutoConnect()
    {
        if (_action is not (InstallerAction.Install or InstallerAction.Update))
        {
            return false;
        }

        if (_interactive)
        {
            return _vm.EffectiveAutoConnect;
        }

        return string.Equals(engine.GetVariableString("AUTOCONNECT"), "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the post-install launch should reopen the settings console: the in-app updater passes
    /// SHOWCONSOLE=1 because the update was started from there. Install/update only.
    /// </summary>
    private bool ShouldShowConsole()
    {
        if (_action is not (InstallerAction.Install or InstallerAction.Update))
        {
            return false;
        }

        // An in-app update was started from the settings console; reopen it. UPDATEFLOW is the reliable signal
        // even when an older sending build omits SHOWCONSOLE, so it wins over the windowless auto-connect launch.
        return IsUpdateFlow()
            || string.Equals(engine.GetVariableString("SHOWCONSOLE"), "1", StringComparison.Ordinal);
    }

    private static void LaunchApp(bool autoConnect, bool showConsole)
    {
        try
        {
            var baseDir = Environment.GetEnvironmentVariable("ProgramW6432")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dir = Path.Combine(baseDir, "AmneziaGeo");
            // Launch the tray anchor (it opens the GUI), not the GUI directly, so the tunnel-holding tray runs.
            var exe = Path.Combine(dir, "AmneziaGeo.Windows.Tray.exe");
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir };
                if (autoConnect)
                {
                    // The tray dials the active profile straight away, skipping the launcher window (#188).
                    psi.ArgumentList.Add("--connect");
                }

                if (showConsole)
                {
                    // Reopen the settings console the in-app update was started from, instead of the launcher.
                    psi.ArgumentList.Add("--settings");
                }

                Process.Start(psi);
            }
        }
        catch
        {
        }
    }

    // Records the surface the in-app update was started from, so the relaunched tray / UI returns there and
    // announces the install. Written only for an applied in-app update, so a cancelled, declined, or failed run
    // leaves nothing behind and no later launch is mistaken for a post-update one. An older sending build omits
    // UPDATEORIGIN; it could only update from the console, so that falls back to "settings".
    private void WriteResumeOrigin()
    {
        if (!IsUpdateFlow())
        {
            return;
        }

        var origin = engine.GetVariableString("UPDATEORIGIN");
        if (origin is not ("launcher" or "settings" or "none"))
        {
            origin = "settings";
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "update-origin"), origin);
        }
        catch
        {
        }
    }

    private void Finish(bool ok, string message)
    {
        _result = ok ? 0 : 1;
        _dispatcher.BeginInvoke(() =>
        {
            _vm.Complete(ok, message);
            AfterComplete(ok);
        });
    }

    private IntPtr WindowHandle =>
        _dispatcher.Invoke(() => Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero);

    private static InstallerAction MapCommandAction(LaunchAction action, InstallState state) => action switch
    {
        LaunchAction.Uninstall => InstallerAction.Remove,
        LaunchAction.Repair => InstallerAction.Repair,
        LaunchAction.Modify => InstallerAction.Update,
        _ => state == InstallState.Installed ? InstallerAction.Update : InstallerAction.Install,
    };

    private static string SuccessText(InstallerAction action) => action switch
    {
        InstallerAction.Repair => Loc.Instance.Get("InstallerBa_SuccessRepair"),
        InstallerAction.Remove => Loc.Instance.Get("InstallerBa_SuccessRemove"),
        InstallerAction.Update => Loc.Instance.Get("InstallerBa_SuccessUpdate"),
        _ => Loc.Instance.Get("InstallerBa_SuccessInstall"),
    };
}
