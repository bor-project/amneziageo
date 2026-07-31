using System.Windows.Input;
using System.Windows.Threading;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Installer session phase.
/// </summary>
public enum Phase
{
    Detecting,
    Ready,
    Applying,
    Done,
}

/// <summary>
/// Step of the apply the engine is on. Each one runs the bar from zero and is named on the progress screen.
/// </summary>
public enum ApplyStep
{
    Preparing,
    Extracting,
    RemovingPrevious,
    Removing,
    StoppingService,
    CopyingFiles,
    StartingService,
    Registering,
    Finishing,
}

/// <summary>
/// Detected install state on the machine.
/// </summary>
public enum InstallState
{
    Unknown,
    NotInstalled,
    Installed,
    NewerInstalled,
}

/// <summary>
/// Maintenance action chosen by the user.
/// </summary>
public enum InstallerAction
{
    Install,
    Update,
    Repair,
    Remove,
}

/// <summary>
/// Installer window view model.
/// </summary>
public sealed class InstallerViewModel : ObservableObject
{
    private readonly Action<InstallerAction> _invoke;
    private readonly Action _close;

    // One bar, run again for every step of the apply: it fills as the step works, is carried to the end when
    // the step is done, and starts over from zero on the next one. A step only reports a real percentage while
    // a package is being unpacked; the rest is a curve that keeps moving and eases off, so a long step reads as
    // work rather than a stalled bar. A step is held for a moment before giving way, so the run of one-tick MSI
    // actions collapses into the one that follows instead of flashing past.
    private const double TickMs = 33;
    private const int MinStepTicks = 12;
    private const double TimeWork = 0.016;
    private const double EngineWork = 0.02;

    private readonly DispatcherTimer _bar = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    private double _shown;
    private double _work;
    private int _target = -1;
    private int _stepTicks;
    private int _enginePercent;
    private bool _closing;
    private ApplyStep _step;
    private ApplyStep? _nextStep;

    private Phase _phase = Phase.Detecting;
    private InstallState _state = InstallState.Unknown;
    private InstallerAction _action;
    private string _subText = Loc.Instance.Get("InstallerVm_CheckingInstall");
    private string _versionText = string.Empty;
    private string _stepText = string.Empty;
    private int _progress;
    private bool _success;
    private bool _downloadLists = true;
    private bool _deleteConfig;
    private bool _indeterminate;
    private bool _launchOnClose = true;
    private bool _desktopShortcut = true;
    private bool _startMenuShortcut = true;
    private bool _autoConnect;
    private bool _canAutoConnect;
    private string _geoResult = string.Empty;
    private InstallerAction? _pendingAction;

    /// <summary>
    /// ctor
    /// </summary>
    public InstallerViewModel(Action<InstallerAction> invoke, Action close)
    {
        _invoke = invoke;
        _close = close;
        _bar.Tick += (_, _) => DrawBar();

        // Seed from the per-user saved options so choices carry across installs (#183).
        var opts = InstallerOptions.Load();
        _downloadLists = opts.DownloadLists;
        _launchOnClose = opts.LaunchAfter;
        _desktopShortcut = opts.DesktopShortcut;
        _startMenuShortcut = opts.StartMenuShortcut;
        _autoConnect = opts.AutoConnect;

        InstallCommand = new RelayCommand(() => PendingAction = InstallerAction.Install);
        UpdateCommand = new RelayCommand(() => PendingAction = InstallerAction.Update);
        RepairCommand = new RelayCommand(() => PendingAction = InstallerAction.Repair);
        RemoveCommand = new RelayCommand(() => PendingAction = InstallerAction.Remove);
        ConfirmCommand = new RelayCommand(() =>
        {
            if (_pendingAction is { } action)
            {
                PersistOptions();
                _invoke(action);
            }
        });
        BackCommand = new RelayCommand(() => PendingAction = null);
        CloseCommand = new RelayCommand(() => _close());
    }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Applies the staged action from the options step.
    /// </summary>
    public ICommand ConfirmCommand { get; }

    /// <summary>
    /// Returns from the options step to the action buttons.
    /// </summary>
    public ICommand BackCommand { get; }

    public ICommand CloseCommand { get; }

    public string Heading => "AmneziaGeo";

    public string SubText
    {
        get => _subText;
        private set => Set(ref _subText, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => Set(ref _versionText, value);
    }

    public int Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>
    /// The step of the apply, under the status line.
    /// </summary>
    public string StepText
    {
        get => _stepText;
        private set
        {
            if (Set(ref _stepText, value))
            {
                Raise(nameof(HasStepText));
            }
        }
    }

    public bool HasStepText => !string.IsNullOrEmpty(StepText);

    public Phase Phase
    {
        get => _phase;
        private set
        {
            if (Set(ref _phase, value))
            {
                RaiseVisibility();
            }
        }
    }

    public InstallState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(InstallButtonText));
                RaiseVisibility();
            }
        }
    }

    public string InstallButtonText => State == InstallState.NewerInstalled ? Loc.Instance.Get("InstallerVm_InstallDowngrade") : Loc.Instance.Get("InstallerVm_Install");

    /// <summary>
    /// Action being configured on the options step, or null on the action buttons step.
    /// </summary>
    public InstallerAction? PendingAction
    {
        get => _pendingAction;
        private set
        {
            if (Set(ref _pendingAction, value))
            {
                if (value is not null)
                {
                    DeleteConfig = false;
                }
                RaiseVisibility();
                Raise(nameof(DeleteConfigLabel));
                Raise(nameof(OptionsHeading));
            }
        }
    }

    /// <summary>
    /// Show the maintenance action buttons step.
    /// </summary>
    public bool ShowActionButtons => Phase == Phase.Ready && _pendingAction is null;

    /// <summary>
    /// Show the options step for the staged action.
    /// </summary>
    public bool ShowOptionsStep => Phase == Phase.Ready && _pendingAction is not null;

    private bool IsApplyAction => _pendingAction is InstallerAction.Install or InstallerAction.Update or InstallerAction.Repair;

    private InstallerAction? SoleAction => State == InstallState.NotInstalled ? InstallerAction.Install : null;

    /// <summary>
    /// Show Back only when there was a choice to return to.
    /// </summary>
    public bool ShowBack => ShowOptionsStep && SoleAction is null;

    public bool ShowInstall => ShowActionButtons && (State == InstallState.NotInstalled || State == InstallState.NewerInstalled);

    public bool ShowUpdate => ShowActionButtons && State == InstallState.Installed;

    public bool ShowRepair => ShowActionButtons && State == InstallState.Installed;

    public bool ShowRemove => ShowActionButtons && (State == InstallState.Installed || State == InstallState.NewerInstalled);

    public bool ShowProgress => Phase == Phase.Applying;

    public bool ShowDone => Phase == Phase.Done;

    public bool DoneSucceeded => _success;

    /// <summary>
    /// Whether to download the geo lists after install.
    /// </summary>
    public bool DownloadLists
    {
        get => _downloadLists;
        set => Set(ref _downloadLists, value);
    }

    public bool ShowDownloadOption => ShowOptionsStep && IsApplyAction;

    /// <summary>
    /// Whether to wipe the runtime configuration.
    /// </summary>
    public bool DeleteConfig
    {
        get => _deleteConfig;
        set
        {
            if (Set(ref _deleteConfig, value))
            {
                Raise(nameof(ShowAutoConnectOption));
            }
        }
    }

    /// <summary>
    /// Show the wipe toggle on the options step.
    /// </summary>
    public bool ShowDeleteConfigOption => ShowOptionsStep;

    /// <summary>
    /// Contextual label for the wipe toggle.
    /// </summary>
    public string DeleteConfigLabel => _pendingAction == InstallerAction.Remove
        ? Loc.Instance.Get("InstallerVm_DeleteConfigAndCache")
        : Loc.Instance.Get("InstallerVm_ResetSettings");

    /// <summary>
    /// Heading on the options step.
    /// </summary>
    public string OptionsHeading => _pendingAction switch
    {
        InstallerAction.Update => Loc.Instance.Get("InstallerVm_OptionsUpdate"),
        InstallerAction.Repair => Loc.Instance.Get("InstallerVm_OptionsRepair"),
        InstallerAction.Remove => Loc.Instance.Get("InstallerVm_OptionsRemove"),
        _ => Loc.Instance.Get("InstallerVm_OptionsInstall"),
    };

    /// <summary>
    /// Whether to launch the UI after the installer closes.
    /// </summary>
    public bool LaunchOnClose
    {
        get => _launchOnClose;
        set => Set(ref _launchOnClose, value);
    }

    /// <summary>
    /// Show the launch-after checkbox on the options step (before applying), for install/update only (#165).
    /// </summary>
    public bool ShowLaunchOnInstall => ShowOptionsStep && _pendingAction is InstallerAction.Install or InstallerAction.Update;

    /// <summary>
    /// Whether to create a desktop shortcut (#183).
    /// </summary>
    public bool DesktopShortcut
    {
        get => _desktopShortcut;
        set => Set(ref _desktopShortcut, value);
    }

    /// <summary>
    /// Whether to create a Start-menu shortcut (#183).
    /// </summary>
    public bool StartMenuShortcut
    {
        get => _startMenuShortcut;
        set => Set(ref _startMenuShortcut, value);
    }

    /// <summary>
    /// Show the shortcut checkboxes on the options step, for install/update/repair only - not removal (#183).
    /// </summary>
    public bool ShowShortcutOptions => ShowOptionsStep && IsApplyAction;

    /// <summary>
    /// Whether to dial the existing connection right after the post-install launch, bypassing the launcher (#188).
    /// </summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set => Set(ref _autoConnect, value);
    }

    /// <summary>
    /// Show the nested auto-connect checkbox: only when the agent reports an existing connectable profile, on
    /// install/update, and not while a settings reset (which wipes that profile) is chosen (#188).
    /// </summary>
    public bool ShowAutoConnectOption =>
        ShowOptionsStep && _canAutoConnect && !DeleteConfig
        && _pendingAction is InstallerAction.Install or InstallerAction.Update;

    /// <summary>
    /// The validated auto-connect decision applied at close: honoured only with a launch-after, an existing
    /// connectable profile detected this run, and the box left on. Guards a saved true when no profile is
    /// present or a reset wipes it (#188).
    /// </summary>
    public bool EffectiveAutoConnect => _canAutoConnect && !DeleteConfig && LaunchOnClose && AutoConnect;

    /// <summary>
    /// Records whether the detected install has a connectable profile, gating the auto-connect option (#188).
    /// </summary>
    public void SetCanAutoConnect(bool value)
    {
        if (_canAutoConnect != value)
        {
            _canAutoConnect = value;
            Raise(nameof(ShowAutoConnectOption));
        }
    }

    /// <summary>
    /// True while the list download runs with no percentage.
    /// </summary>
    public bool IsIndeterminate
    {
        get => _indeterminate;
        private set
        {
            if (Set(ref _indeterminate, value))
            {
                Raise(nameof(ShowPercent));
            }
        }
    }

    public bool ShowPercent => !IsIndeterminate;

    /// <summary>
    /// Geo-list download outcome shown on the final screen.
    /// </summary>
    public string GeoResult
    {
        get => _geoResult;
        private set
        {
            if (Set(ref _geoResult, value))
            {
                Raise(nameof(HasGeoResult));
            }
        }
    }

    public bool HasGeoResult => !string.IsNullOrEmpty(GeoResult);

    /// <summary>
    /// Apply detection result to the view state.
    /// </summary>
    public void SetDetected(InstallState state, string? installedVersion, string? newVersion)
    {
        State = state;
        VersionText = BuildVersionText(state, installedVersion, newVersion);
        SubText = state switch
        {
            InstallState.NotInstalled => Loc.Instance.Get("InstallerVm_ReadyToInstall"),
            InstallState.Installed => Loc.Instance.Get("InstallerVm_AlreadyInstalled"),
            InstallState.NewerInstalled => Loc.Instance.Get("InstallerVm_NewerInstalled"),
            _ => Loc.Instance.Get("InstallerVm_Ready"),
        };
        Phase = Phase.Ready;

        if (SoleAction is { } sole)
        {
            PendingAction = sole;
        }
    }

    // Version line: install shows the incoming version, an upgrade and a rollback show from -> to, otherwise
    // the installed one.
    private static string BuildVersionText(InstallState state, string? installed, string? next)
    {
        if (state == InstallState.NotInstalled)
        {
            return string.IsNullOrEmpty(next) ? string.Empty : Loc.Instance.Get("InstallerVm_InstallVersion", next);
        }

        if (state == InstallState.Installed && IsUpgrade(installed, next))
        {
            return Loc.Instance.Get("InstallerVm_UpdateVersion", installed!, next!);
        }

        if (state == InstallState.NewerInstalled && !string.IsNullOrEmpty(installed) && !string.IsNullOrEmpty(next))
        {
            return Loc.Instance.Get("InstallerVm_DowngradeVersion", installed, next);
        }

        return string.IsNullOrEmpty(installed) ? string.Empty : Loc.Instance.Get("InstallerVm_InstalledVersion", installed);
    }

    private static bool IsUpgrade(string? installed, string? next) =>
        System.Version.TryParse(installed, out var from) && System.Version.TryParse(next, out var to) && to > from;

    /// <summary>
    /// Opens the update options step directly, skipping the maintenance action buttons.
    /// </summary>
    public void StageUpdate()
    {
        if (State == InstallState.Installed)
        {
            PendingAction = InstallerAction.Update;
        }
    }

    /// <summary>
    /// Switch to the live-progress view.
    /// </summary>
    public void BeginApply(InstallerAction action)
    {
        _action = action;
        Rewind();
        IsIndeterminate = false;
        SubText = action switch
        {
            InstallerAction.Repair => Loc.Instance.Get("InstallerVm_Repairing"),
            InstallerAction.Remove => Loc.Instance.Get("InstallerVm_Removing"),
            InstallerAction.Update => Loc.Instance.Get("InstallerVm_Updating"),
            _ => Loc.Instance.Get("InstallerVm_Installing"),
        };
        _step = ApplyStep.Preparing;
        StepText = StepLabel(_step);
        Phase = Phase.Applying;
        _bar.Start();
    }

    /// <summary>
    /// Switch to the removing-the-installed-version view.
    /// </summary>
    public void BeginRemoveNewer()
    {
        _bar.Stop();
        Rewind();
        StepText = string.Empty;
        SubText = Loc.Instance.Get("InstallerVm_RemovingNewer");
        IsIndeterminate = true;
        Phase = Phase.Applying;
    }

    /// <summary>
    /// Moves the bar on to a step of the apply. The step in progress is carried to the end first, and one that
    /// only just started is given its moment before it gives way.
    /// </summary>
    public void BeginStep(ApplyStep step)
    {
        if (Phase != Phase.Applying)
        {
            return;
        }

        if (step == _step)
        {
            _nextStep = null;
            return;
        }

        _nextStep = step;
        CloseStep();
    }

    /// <summary>
    /// Reports a step that knows its own size, as a percentage of itself.
    /// </summary>
    public void ReportStep(ApplyStep step, int percent)
    {
        BeginStep(step);
        if (step == _step && percent is >= 0 and <= 100)
        {
            _target = percent;
        }
    }

    /// <summary>
    /// Feeds the engine's own percentage for the running package into the current step, so a step that moves
    /// files fills faster than one that merely takes time.
    /// </summary>
    public void ReportEngineProgress(int packagePercent)
    {
        var advanced = packagePercent - _enginePercent;
        _enginePercent = packagePercent;
        if (advanced > 0)
        {
            _work += advanced * EngineWork;
        }
    }

    /// <summary>
    /// Starts the engine percentage over for a new package.
    /// </summary>
    public void ResetEngineProgress() => _enginePercent = 0;

    /// <summary>
    /// Switch to the checking-for-updates view.
    /// </summary>
    public void BeginGeoCheck()
    {
        SubText = Loc.Instance.Get("InstallerVm_CheckingGeoUpdates");
        StepText = string.Empty;
        IsIndeterminate = true;
    }

    /// <summary>
    /// Switch to the downloading-lists view.
    /// </summary>
    public void BeginGeoDownload()
    {
        SubText = Loc.Instance.Get("InstallerVm_DownloadingGeo");
        Rewind();
        IsIndeterminate = true;
        _bar.Start();
    }

    /// <summary>
    /// Report geo-list download progress.
    /// </summary>
    public void ReportGeoProgress(int percent)
    {
        if (percent is < 0 or > 100)
        {
            return;
        }

        IsIndeterminate = false;
        _target = percent;
        _bar.Start();
    }

    private static string StepLabel(ApplyStep step) => step switch
    {
        ApplyStep.Extracting => Loc.Instance.Get("InstallerVm_StepExtracting"),
        ApplyStep.RemovingPrevious => Loc.Instance.Get("InstallerVm_StepRemovingPrevious"),
        ApplyStep.Removing => Loc.Instance.Get("InstallerVm_StepRemoving"),
        ApplyStep.StoppingService => Loc.Instance.Get("InstallerVm_StepStoppingService"),
        ApplyStep.CopyingFiles => Loc.Instance.Get("InstallerVm_StepCopyingFiles"),
        ApplyStep.StartingService => Loc.Instance.Get("InstallerVm_StepStartingService"),
        ApplyStep.Registering => Loc.Instance.Get("InstallerVm_StepRegistering"),
        ApplyStep.Finishing => Loc.Instance.Get("InstallerVm_StepFinishing"),
        _ => Loc.Instance.Get("InstallerVm_StepPreparing"),
    };

    // Lets the waiting step in once the current one has been shown long enough.
    private void CloseStep()
    {
        if (_nextStep is not null && !_closing && _stepTicks >= MinStepTicks)
        {
            _closing = true;
        }
    }

    // Hands the bar over to the waiting step, from zero.
    private void StartStep()
    {
        var next = _nextStep;
        Rewind();
        if (next is { } step)
        {
            _step = step;
            StepText = StepLabel(step);
        }
    }

    private void Rewind()
    {
        _shown = 0;
        _work = 0;
        _target = -1;
        _stepTicks = 0;
        _closing = false;
        _nextStep = null;
        Progress = 0;
    }

    // One frame of the bar: run out a step that is done, close a fifth of the gap to a reported percentage, or
    // follow the curve where the step reports nothing.
    private void DrawBar()
    {
        _stepTicks++;
        CloseStep();

        if (_closing)
        {
            _shown = Math.Min(100, _shown + Math.Max(4, (100 - _shown) * 0.5));
            Progress = (int)_shown;
            if (_shown >= 99.5)
            {
                StartStep();
            }

            return;
        }

        if (_target >= 0)
        {
            _shown = Math.Min(_target, _shown + Math.Max(0.4, (_target - _shown) * 0.18));
        }
        else
        {
            _work += TimeWork;
            _shown = 100 * (1 - Math.Exp(-_work));
        }

        Progress = (int)_shown;
    }

    /// <summary>
    /// Show the apply result.
    /// </summary>
    public void Complete(bool success, string message)
    {
        _bar.Stop();
        _success = success;
        SubText = message;
        StepText = string.Empty;
        IsIndeterminate = false;
        Phase = Phase.Done;
        Raise(nameof(DoneSucceeded));
    }

    /// <summary>
    /// Finish with the MSI result and the geo-download outcome.
    /// </summary>
    public void CompleteWithGeo(string message, string geoResult) => CompleteWithGeo(true, message, geoResult);

    /// <summary>
    /// Finish with an explicit success flag (a failed post-install config import fails the run) plus the
    /// import/geo detail line.
    /// </summary>
    public void CompleteWithGeo(bool success, string message, string geoResult)
    {
        GeoResult = geoResult;
        Complete(success, message);
    }

    // Persist the non-destructive choices so the next install/update/repair starts from them (#183). The
    // reset/delete-config choice is intentionally not saved.
    private void PersistOptions()
    {
        new InstallerOptions
        {
            DesktopShortcut = DesktopShortcut,
            StartMenuShortcut = StartMenuShortcut,
            LaunchAfter = LaunchOnClose,
            DownloadLists = DownloadLists,
            AutoConnect = AutoConnect,
        }.Save();
    }

    private void RaiseVisibility()
    {
        Raise(nameof(ShowActionButtons));
        Raise(nameof(ShowOptionsStep));
        Raise(nameof(ShowBack));
        Raise(nameof(ShowInstall));
        Raise(nameof(ShowUpdate));
        Raise(nameof(ShowRepair));
        Raise(nameof(ShowRemove));
        Raise(nameof(ShowProgress));
        Raise(nameof(ShowDone));
        Raise(nameof(ShowDownloadOption));
        Raise(nameof(ShowDeleteConfigOption));
        Raise(nameof(ShowLaunchOnInstall));
        Raise(nameof(ShowShortcutOptions));
        Raise(nameof(ShowAutoConnectOption));
    }
}
