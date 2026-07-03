using System.Windows.Input;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>The phase of the installer session.</summary>
public enum Phase
{
    Detecting,
    Ready,
    Applying,
    Done,
}

/// <summary>What the engine found already on the machine.</summary>
public enum InstallState
{
    Unknown,
    NotInstalled,
    Installed,
    NewerInstalled,
}

/// <summary>The maintenance action the user picked; mapped to a Burn LaunchAction by the BA.</summary>
public enum InstallerAction
{
    Install,
    Update,
    Repair,
    Remove,
}

/// <summary>
/// Drives the installer window. UI-only: it knows nothing about Burn - the BA injects an
/// <c>invoke</c> callback (run a maintenance action) and a <c>close</c> callback, and pushes
/// detection / progress / result back in via the Set* / Report* methods (always on the UI thread).
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
    private string _seedDbPath = string.Empty;
    private InstallerAction? _pendingAction;

    public InstallerViewModel(Action<InstallerAction> invoke, Action close)
    {
        _invoke = invoke;
        _close = close;

        // The mode buttons stage an action, opening the options step; Confirm applies it and Back returns to
        // the action buttons (#114). The action itself is dispatched (to Burn) only on Confirm.
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
        PickSeedDbCommand = new RelayCommand(PickSeedDb);
    }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand RemoveCommand { get; }

    /// <summary>Applies the staged action (starts the Burn plan/apply) from the options step (#114).</summary>
    public ICommand ConfirmCommand { get; }

    /// <summary>Returns from the options step to the action buttons without applying (#114).</summary>
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

    /// <summary>The action currently being configured on the options step; null while the action buttons
    /// (step 1) are shown (#114).</summary>
    public InstallerAction? PendingAction
    {
        get => _pendingAction;
        private set
        {
            if (Set(ref _pendingAction, value))
            {
                // Entering a step: the destructive wipe toggle always starts unchecked, so a choice made on a
                // previous action's step (then «Назад») never carries over to a different action - e.g. ticking
                // «Удалить конфигурацию и кэш» for Remove must not pre-check «Сбросить настройки» on Update (#114).
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

    /// <summary>Ready phase, step 1: the maintenance action buttons (no action staged yet).</summary>
    public bool ShowActionButtons => Phase == Phase.Ready && _pendingAction is null;

    /// <summary>Ready phase, step 2: options + confirm/back for the staged action (#114).</summary>
    public bool ShowOptionsStep => Phase == Phase.Ready && _pendingAction is not null;

    /// <summary>Whether the staged action installs/keeps the product (so it offers config + geo options), as
    /// opposed to Remove (which offers only the delete-config toggle).</summary>
    private bool IsApplyAction => _pendingAction is InstallerAction.Install or InstallerAction.Update or InstallerAction.Repair;

    /// <summary>The only maintenance action available for the current state, or null when there is a choice. A
    /// machine with nothing installed can only be installed, so its single-button first step is skipped and
    /// the options step opens straight away (#114).</summary>
    private InstallerAction? SoleAction => State == InstallState.NotInstalled ? InstallerAction.Install : null;

    /// <summary>Show «Назад» only when there was a choice to return to - i.e. the options step was reached by
    /// picking an action, not auto-opened for a sole action (#114).</summary>
    public bool ShowBack => ShowOptionsStep && SoleAction is null;

    public bool ShowInstall => ShowActionButtons && (State == InstallState.NotInstalled || State == InstallState.NewerInstalled);

    public bool ShowUpdate => ShowActionButtons && State == InstallState.Installed;

    public bool ShowRepair => ShowActionButtons && State == InstallState.Installed;

    public bool ShowRemove => ShowActionButtons && (State == InstallState.Installed || State == InstallState.NewerInstalled);

    public bool ShowProgress => Phase == Phase.Applying;

    public bool ShowDone => Phase == Phase.Done;

    public bool DoneSucceeded => _success;

    /// <summary>Whether to download the geo lists after install (checkbox, install/update only).</summary>
    public bool DownloadLists
    {
        get => _downloadLists;
        set => Set(ref _downloadLists, value);
    }

    public bool ShowDownloadOption => ShowOptionsStep && IsApplyAction;

    /// <summary>Whether to wipe the runtime configuration (ProgramData\AmneziaGeo: profiles, settings, caches,
    /// geo bases). On install/update/repair this is «Сбросить настройки» (start fresh); on removal it is
    /// «Удалить конфигурацию и кэш». Off by default so a plain (re)install keeps the user's data (#105/#114).</summary>
    public bool DeleteConfig
    {
        get => _deleteConfig;
        set => Set(ref _deleteConfig, value);
    }

    /// <summary>The wipe toggle is offered on every action's options step; its label is contextual
    /// (<see cref="DeleteConfigLabel"/>).</summary>
    public bool ShowDeleteConfigOption => ShowOptionsStep;

    /// <summary>Contextual label for the wipe toggle: «Сбросить настройки» on install/update/repair,
    /// «Удалить конфигурацию и кэш» on removal (#114).</summary>
    public string DeleteConfigLabel => _pendingAction == InstallerAction.Remove
        ? Loc.Instance.Get("InstallerVm_DeleteConfigAndCache")
        : Loc.Instance.Get("InstallerVm_ResetSettings");

    /// <summary>Heading on the options step, naming the staged action (#114).</summary>
    public string OptionsHeading => _pendingAction switch
    {
        InstallerAction.Update => Loc.Instance.Get("InstallerVm_OptionsUpdate"),
        InstallerAction.Repair => Loc.Instance.Get("InstallerVm_OptionsRepair"),
        InstallerAction.Remove => Loc.Instance.Get("InstallerVm_OptionsRemove"),
        _ => Loc.Instance.Get("InstallerVm_OptionsInstall"),
    };

    /// <summary>Whether to launch AmneziaGeo.UI after the installer closes (checkbox on the done screen).</summary>
    public bool LaunchOnClose
    {
        get => _launchOnClose;
        set => Set(ref _launchOnClose, value);
    }

    /// <summary>Show the launch checkbox only after a successful install or update.</summary>
    public bool ShowLaunchOption => Phase == Phase.Done && _success && _action is InstallerAction.Install or InstallerAction.Update;

    /// <summary>True while the list download runs (no percentage available) - spins the progress bar.</summary>
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

    /// <summary>The geo-list download outcome, shown as a second line on the final screen.</summary>
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

    /// <summary>Called once detection completes; sets the state and a human description.</summary>
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

        // If installing is the only thing this machine can do (nothing is installed), skip the single-button
        // action step and open its options straight away (#114).
        if (SoleAction is { } sole)
        {
            PendingAction = sole;
        }
    }

    /// <summary>Switch the window to the live-progress view.</summary>
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

    /// <summary>Switches the window to the indeterminate "checking for base updates" view (update/repair
    /// only - runs before deciding whether a download is needed).</summary>
    public void BeginGeoCheck()
    {
        SubText = Loc.Instance.Get("InstallerVm_CheckingGeoUpdates");
        IsIndeterminate = true;
    }

    /// <summary>Switches the window to the "downloading lists" view (after the MSI step). Starts
    /// indeterminate (a spinner) until the first real percentage arrives via <see cref="ReportGeoProgress"/>.</summary>
    public void BeginGeoDownload()
    {
        SubText = Loc.Instance.Get("InstallerVm_DownloadingGeo");
        Progress = 0;
        IsIndeterminate = true;
    }

    /// <summary>Reports geo-list download progress, flipping the spinner to a determinate percentage.</summary>
    public void ReportGeoProgress(int percent)
    {
        if (percent is >= 0 and <= 100)
        {
            IsIndeterminate = false;
            Progress = percent;
        }
    }

    /// <summary>Called once apply finishes; shows the result.</summary>
    public void Complete(bool success, string message)
    {
        _success = success;
        SubText = message;
        IsIndeterminate = false;
        Phase = Phase.Done;
        Raise(nameof(DoneSucceeded));
        Raise(nameof(ShowLaunchOption));
    }

    /// <summary>Finishes with the MSI result plus a second line carrying the geo-download outcome.</summary>
    public void CompleteWithGeo(string message, string geoResult)
    {
        GeoResult = geoResult;
        Complete(true, message);
    }

    /// <summary>Opens a file picker for a default-settings database (#55); the BA writes the chosen path
    /// into the bundle's SEEDDBPATH variable before planning.</summary>
    public ICommand PickSeedDbCommand { get; }

    /// <summary>The user-selected default-settings DB path (empty = none), read by the BA on install.</summary>
    public string SeedDbPath
    {
        get => _seedDbPath;
        private set
        {
            if (Set(ref _seedDbPath, value))
            {
                Raise(nameof(SeedDbLabel));
            }
        }
    }

    /// <summary>Caption next to the picker: the chosen file name, or a "none selected" hint.</summary>
    public string SeedDbLabel => string.IsNullOrEmpty(SeedDbPath)
        ? Loc.Instance.Get("InstallerVm_SeedDbNone")
        : Loc.Instance.Get("InstallerVm_SeedDbSelected", System.IO.Path.GetFileName(SeedDbPath));

    /// <summary>Whether to offer the default-settings-DB picker: on the options step for install / update /
    /// repair (like the geo option), never for removal.</summary>
    public bool ShowSeedDbOption => ShowOptionsStep && IsApplyAction;

    private void PickSeedDb()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Instance.Get("InstallerVm_SeedDbDialogTitle"),
            Filter = Loc.Instance.Get("InstallerVm_SeedDbDialogFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            SeedDbPath = dialog.FileName;
        }
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
        Raise(nameof(ShowSeedDbOption));
        Raise(nameof(ShowLaunchOption));
    }
}
