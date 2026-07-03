using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AmneziaGeo.Localization;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// The AmneziaGeo bootstrapper application (WiX v5, out-of-process, managed WPF).
///
/// Lifecycle: <see cref="Run"/> builds the WPF UI on the BA thread, kicks <c>engine.Detect()</c>,
/// then pumps the message loop. Engine callbacks arrive on a Burn thread and are marshalled to the
/// UI dispatcher. User buttons map to a Burn <see cref="LaunchAction"/> (Plan → Apply). Downgrade is
/// permitted by removing the newer related bundle during planning.
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
    private bool _olderRelated;     // an older bundle is installed (we are an upgrade)
    private bool _newerRelated;     // a newer bundle is installed (we would be a downgrade)
    private string? _installedVersion;
    private string? _myVersion;

    private InstallerAction _action;
    private LaunchAction _launch;
    private int _result;
    private InstallState _detectedState;

    private volatile bool _engineConnected;

    /// <summary>True once the Burn engine has hosted us (OnCreate fired). Used by the standalone-launch
    /// watchdog in <see cref="Program"/> to tell a real engine launch apart from a direct double-click.</summary>
    public bool EngineConnected => _engineConnected;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _engineConnected = true;
        _command = args.Command;
        _interactive = _command.Display == Display.Full && _command.Action != LaunchAction.Uninstall;
    }

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
        new WindowInteropHelper(window).EnsureHandle();   // realise the HWND for engine.Apply
        _mainWindow = window;

        engine.Detect();

        // A bundle launched for uninstall (e.g. the ARP "Uninstall" entry, which runs us with
        // LaunchAction.Uninstall) drives the removal headlessly: the realised HWND above is enough for
        // engine.Apply and detection auto-runs the remove (OnDetectComplete, treated non-interactive), so
        // the maintenance window never shows - this is what stops the installer window(s) appearing on
        // removal. Re-running setup.exe and clicking "Remove" is a different launch (Action=Install/Modify)
        // and keeps its window.
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
        // Burn reports same-UpgradeCode bundles here (there is no "Downgrade" relation type - direction
        // is decided by comparing versions with the engine's own comparer). A newer installed version
        // means installing over it would be a downgrade (allowed, see OnPlanRelatedBundle).
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
        // When installing, always clear any related bundle (older OR newer) so only our version
        // remains - this is what makes a downgrade go through instead of Burn refusing it.
        if (_launch == LaunchAction.Install)
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

    /// <summary>Kills any running AmneziaGeo.Windows.Ui.exe so the MSI can replace the file.
    /// Returns true if no process is running or it was stopped; false if it could not be killed.</summary>
    private static bool StopRunningApp()
    {
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
                    // best effort
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
        // Covers the no-elevation path (already elevated / silent): there is no UAC prompt to steal
        // focus, so raising here is enough. When elevation IS required the prompt appears after this,
        // so the effective raise happens in OnElevateComplete once the UAC dialog has closed.
        RaiseMainWindow();
    }

    private void OnElevateComplete(object? sender, ElevateCompleteEventArgs e)
    {
        // The UAC prompt runs on the secure desktop and, on dismissal, leaves our window behind others -
        // the user then sees no progress and may think the install hung. Once elevation has succeeded,
        // bring the installer window back to the front.
        if (e.Status >= 0)
        {
            RaiseMainWindow();
        }
    }

    /// <summary>Brings the installer window to the top of the z-order and gives it foreground. Toggling
    /// Topmost forces the z-order change even when SetForegroundWindow is blocked by the foreground lock
    /// (as it is right after the UAC secure-desktop transition), without leaving the window always-on-top.</summary>
    private void RaiseMainWindow()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            _mainWindow.Topmost = true;
            _mainWindow.Activate();
            _mainWindow.Topmost = false;
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

        var downloadGeo = (_action == InstallerAction.Install || _action == InstallerAction.Update) && _vm.DownloadLists;
        if (!downloadGeo)
        {
            Finish(true, SuccessText(_action));
            return;
        }

        // The MSI installed and started the agent service. Drive the geo step against the privileged agent
        // and report the outcome - a failure here is non-fatal, the install already succeeded.
        _dispatcher.BeginInvoke(async () =>
        {
            var geo = await RunGeoStepAsync();
            _result = 0;
            _vm.CompleteWithGeo(SuccessText(_action), geo);
            if (!_interactive)
            {
                Application.Current?.Shutdown();
            }
        });
    }

    /// <summary>
    /// Runs the geo-data step against the running agent. On a fresh install the bases do not exist yet, so
    /// it always downloads (showing live percent). On update/repair it first asks the agent whether anything
    /// is actually out of date (and reachable) and skips the download when nothing needs updating.
    /// </summary>
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

                // Definite "nothing to update" (0) skips the download; -1 (no snapshot / unknown) or >0
                // falls through and downloads, since skipping when unsure would leave bases stale.
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

        if (action is InstallerAction.Install or InstallerAction.Update)
        {
            ResolveSeedReplace();

            // #55: a default-settings DB the user picked in the BA takes priority over a bundled default and
            // is recorded by the MSI as the seed source. A SEEDDBPATH=... command-line argument is left as
            // given when the user picked nothing.
            if (!string.IsNullOrEmpty(_vm.SeedDbPath))
            {
                engine.SetVariableString("SEEDDBPATH", _vm.SeedDbPath, false);
            }
        }
        else if (action is InstallerAction.Remove)
        {
            // #105: pass the "delete configuration" choice to the MSI (DELETECONFIG -> the AgWipeConfig deferred
            // action wipes ProgramData\AmneziaGeo). An explicit DELETECONFIG=1/0 on the command line wins (the
            // variable is then already non-empty); otherwise take the checkbox, which defaults off. A headless
            // ARP uninstall reaches this with the default-off checkbox and no command-line value, so it keeps the
            // configuration.
            if (string.IsNullOrEmpty(engine.GetVariableString("DELETECONFIG")))
            {
                engine.SetVariableString("DELETECONFIG", _vm.DeleteConfig ? "1" : "0", false);
            }
        }

        _vm.BeginApply(action);
        engine.Plan(_launch);
    }

    /// <summary>
    /// Resolves the replace-on-conflict choice for a bundled default config DB (#54) before planning, by
    /// setting the REPLACEDB engine variable (it flows into the MSI as the SEEDREPLACE property). Only
    /// relevant when this bundle carries a default DB AND a state.db already exists at the destination - a
    /// fresh machine is just seeded by the agent. An explicit command-line REPLACEDB=1/0 is honored as-is
    /// (no dialog); otherwise an interactive run asks the user, and a silent run keeps the existing DB.
    /// </summary>
    private void ResolveSeedReplace()
    {
        if (!string.Equals(engine.GetVariableString("HasDefaultDb"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo", "state.db");
        if (!File.Exists(target))
        {
            return; // nothing to overwrite - the agent just seeds the default DB
        }

        // An explicit REPLACEDB=1/0 on the command line pre-answers the conflict: leave it as given (it
        // already drives [REPLACEDB] -> SEEDREPLACE) and show no dialog.
        if (!string.IsNullOrEmpty(engine.GetVariableString("REPLACEDB")))
        {
            return;
        }

        // Silent / non-interactive with no parameter: keep the existing database (skip).
        if (!_interactive)
        {
            engine.SetVariableString("REPLACEDB", "0", false);
            return;
        }

        // Interactive: ask whether to replace the existing settings with the bundled defaults.
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            Loc.Instance.Get("InstallerBa_ReplaceDbPrompt"),
            "AmneziaGeo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        engine.SetVariableString("REPLACEDB", answer == MessageBoxResult.Yes ? "1" : "0", false);
    }

    private void OnUserClose()
    {
        if (_vm.LaunchOnClose && _vm.ShowLaunchOption)
        {
            LaunchApp();
        }
        Application.Current?.Shutdown();
    }

    /// <summary>Starts AmneziaGeo.UI.exe from the install folder as the current user.</summary>
    private static void LaunchApp()
    {
        try
        {
            var baseDir = Environment.GetEnvironmentVariable("ProgramW6432")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dir = Path.Combine(baseDir, "AmneziaGeo");
            var exe = Path.Combine(dir, "AmneziaGeo.Windows.Ui.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            }
        }
        catch
        {
            // best effort - the install already succeeded
        }
    }

    private void Finish(bool ok, string message)
    {
        _result = ok ? 0 : 1;
        _dispatcher.BeginInvoke(() =>
        {
            _vm.Complete(ok, message);
            if (!_interactive)
            {
                Application.Current?.Shutdown();
            }
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
