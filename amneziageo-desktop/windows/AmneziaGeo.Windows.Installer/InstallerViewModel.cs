using System.Windows.Input;
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

    private Phase _phase = Phase.Detecting;
    private InstallState _state = InstallState.Unknown;
    private InstallerAction _action;
    private string _subText = Loc.Instance.Get("InstallerVm_CheckingInstall");
    private string _versionText = string.Empty;
    private int _progress;
    private bool _success;
    private bool _downloadLists = true;
    private bool _deleteConfig;
    private bool _indeterminate;
    private bool _launchOnClose = true;
    private string _geoResult = string.Empty;
    private InstallerAction? _pendingAction;

    /// <summary>
    /// ctor
    /// </summary>
    public InstallerViewModel(Action<InstallerAction> invoke, Action close)
    {
        _invoke = invoke;
        _close = close;

        InstallCommand = new RelayCommand(() => PendingAction = InstallerAction.Install);
        UpdateCommand = new RelayCommand(() => PendingAction = InstallerAction.Update);
        RepairCommand = new RelayCommand(() => PendingAction = InstallerAction.Repair);
        RemoveCommand = new RelayCommand(() => PendingAction = InstallerAction.Remove);
        ConfirmCommand = new RelayCommand(() =>
        {
            if (_pendingAction is { } action)
            {
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
        set => Set(ref _deleteConfig, value);
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
    public void SetDetected(InstallState state, string? installedVersion)
    {
        State = state;
        VersionText = string.IsNullOrEmpty(installedVersion) ? string.Empty : Loc.Instance.Get("InstallerVm_InstalledVersion", installedVersion);
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

    /// <summary>
    /// Switch to the live-progress view.
    /// </summary>
    public void BeginApply(InstallerAction action)
    {
        _action = action;
        Progress = 0;
        IsIndeterminate = false;
        SubText = action switch
        {
            InstallerAction.Repair => Loc.Instance.Get("InstallerVm_Repairing"),
            InstallerAction.Remove => Loc.Instance.Get("InstallerVm_Removing"),
            InstallerAction.Update => Loc.Instance.Get("InstallerVm_Updating"),
            _ => Loc.Instance.Get("InstallerVm_Installing"),
        };
        Phase = Phase.Applying;
    }

    public void ReportProgress(int percent)
    {
        if (percent is >= 0 and <= 100)
        {
            Progress = percent;
        }
    }

    /// <summary>
    /// Switch to the checking-for-updates view.
    /// </summary>
    public void BeginGeoCheck()
    {
        SubText = Loc.Instance.Get("InstallerVm_CheckingGeoUpdates");
        IsIndeterminate = true;
    }

    /// <summary>
    /// Switch to the downloading-lists view.
    /// </summary>
    public void BeginGeoDownload()
    {
        SubText = Loc.Instance.Get("InstallerVm_DownloadingGeo");
        Progress = 0;
        IsIndeterminate = true;
    }

    /// <summary>
    /// Report geo-list download progress.
    /// </summary>
    public void ReportGeoProgress(int percent)
    {
        if (percent is >= 0 and <= 100)
        {
            IsIndeterminate = false;
            Progress = percent;
        }
    }

    /// <summary>
    /// Show the apply result.
    /// </summary>
    public void Complete(bool success, string message)
    {
        _success = success;
        SubText = message;
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
    }
}
