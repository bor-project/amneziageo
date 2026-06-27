using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
        Progress += OnProgress;
        ApplyComplete += OnApplyComplete;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AmneziaGeo.Windows.Installer;component/Theme/AmneziaGeo.xaml", UriKind.Absolute),
        });

        var window = new MainWindow { DataContext = _vm };
        new WindowInteropHelper(window).EnsureHandle();   // realise the HWND for engine.Apply

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
        if (e.Status >= 0)
        {
            engine.Apply(WindowHandle);
        }
        else
        {
            Finish(false, "Не удалось спланировать операцию.");
        }
    }

    // ---- Apply ----

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs e)
    {
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
            Finish(false, $"Ошибка (0x{e.Status:X8}).");
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
                    return "Базы гео актуальны - загрузка не требуется.";
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
            return $"Не удалось обновить базы гео ({ex.Message}). Можно сделать это позже в приложении.";
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
            "В системе уже есть сохранённые настройки AmneziaGeo (state.db).\n\n" +
            "Заменить их рекомендованными настройками из установщика?\n\n" +
            "Да - заменить (текущие настройки будут потеряны).\n" +
            "Нет - оставить текущие настройки.",
            "AmneziaGeo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        engine.SetVariableString("REPLACEDB", answer == MessageBoxResult.Yes ? "1" : "0", false);
    }

    private void OnUserClose()
    {
        Application.Current?.Shutdown();
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
        InstallerAction.Repair => "Восстановление завершено.",
        InstallerAction.Remove => "AmneziaGeo удалён.",
        InstallerAction.Update => "Обновление завершено.",
        _ => "Установка завершена.",
    };
}
