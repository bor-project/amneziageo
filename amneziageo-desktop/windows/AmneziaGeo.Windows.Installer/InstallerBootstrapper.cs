using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AmneziaGeo.Localization;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// AmneziaGeo WiX v5 bootstrapper application.
/// </summary>
public sealed class InstallerBootstrapper : BootstrapperApplication
{
    private const string MsiPackageId = "AmneziaGeoMsi";

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
        var state = _newerRelated
            ? InstallState.NewerInstalled
            : (_msiPresent || _olderRelated) ? InstallState.Installed : InstallState.NotInstalled;
        _detectedState = state;

        _dispatcher.BeginInvoke(() =>
        {
            _vm.SetDetected(state, _installedVersion);

            if (!_interactive)
            {
                OnUserAction(MapCommandAction(_command.Action, state));
            }
        });
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
            LaunchApp();
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

        if (_interactive)
        {
            return _vm.LaunchOnClose;
        }

        return string.Equals(engine.GetVariableString("LAUNCHAFTER"), "1", StringComparison.Ordinal)
            || (_action == InstallerAction.Update && _command.Display == Display.Passive);
    }

    private static void LaunchApp()
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
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            }
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
