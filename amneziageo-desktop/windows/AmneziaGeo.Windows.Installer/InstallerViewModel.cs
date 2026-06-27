using System.Windows.Input;

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
    private string _subText = "Проверка установки…";
    private string _versionText = string.Empty;
    private int _progress;
    private bool _success;
    private bool _downloadLists = true;
    private bool _indeterminate;
    private string _geoResult = string.Empty;
    private string _seedDbPath = string.Empty;

    public InstallerViewModel(Action<InstallerAction> invoke, Action close)
    {
        _invoke = invoke;
        _close = close;

        InstallCommand = new RelayCommand(() => _invoke(_state == InstallState.NewerInstalled ? InstallerAction.Install : InstallerAction.Install));
        UpdateCommand = new RelayCommand(() => _invoke(InstallerAction.Update));
        RepairCommand = new RelayCommand(() => _invoke(InstallerAction.Repair));
        RemoveCommand = new RelayCommand(() => _invoke(InstallerAction.Remove));
        CloseCommand = new RelayCommand(() => _close());
        PickSeedDbCommand = new RelayCommand(PickSeedDb);
    }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand RemoveCommand { get; }

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

    public string InstallButtonText => State == InstallState.NewerInstalled ? "Установить (откат версии)" : "Установить";

    public bool ShowActions => Phase == Phase.Ready;

    public bool ShowInstall => Phase == Phase.Ready && (State == InstallState.NotInstalled || State == InstallState.NewerInstalled);

    public bool ShowUpdate => Phase == Phase.Ready && State == InstallState.Installed;

    public bool ShowRepair => Phase == Phase.Ready && State == InstallState.Installed;

    public bool ShowRemove => Phase == Phase.Ready && (State == InstallState.Installed || State == InstallState.NewerInstalled);

    public bool ShowProgress => Phase == Phase.Applying;

    public bool ShowDone => Phase == Phase.Done;

    public bool DoneSucceeded => _success;

    /// <summary>Whether to download the geo lists after install (checkbox, install/update only).</summary>
    public bool DownloadLists
    {
        get => _downloadLists;
        set => Set(ref _downloadLists, value);
    }

    public bool ShowDownloadOption => Phase == Phase.Ready && (ShowInstall || ShowUpdate);

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
        VersionText = string.IsNullOrEmpty(installedVersion) ? string.Empty : $"Установлено: {installedVersion}";
        SubText = state switch
        {
            InstallState.NotInstalled => "Готово к установке.",
            InstallState.Installed => "AmneziaGeo уже установлен. Можно обновить, восстановить или удалить.",
            InstallState.NewerInstalled => "Установлена более новая версия. Можно откатиться на эту или удалить.",
            _ => "Готово.",
        };
        Phase = Phase.Ready;
    }

    /// <summary>Switch the window to the live-progress view.</summary>
    public void BeginApply(InstallerAction action)
    {
        Progress = 0;
        IsIndeterminate = false;
        SubText = action switch
        {
            InstallerAction.Repair => "Восстановление…",
            InstallerAction.Remove => "Удаление…",
            InstallerAction.Update => "Обновление…",
            _ => "Установка…",
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
        SubText = "Проверка обновлений баз гео…";
        IsIndeterminate = true;
    }

    /// <summary>Switches the window to the "downloading lists" view (after the MSI step). Starts
    /// indeterminate (a spinner) until the first real percentage arrives via <see cref="ReportGeoProgress"/>.</summary>
    public void BeginGeoDownload()
    {
        SubText = "Загрузка файлов баз гео…";
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
        ? "Файл настроек по умолчанию не выбран."
        : $"Файл настроек: {System.IO.Path.GetFileName(SeedDbPath)}";

    /// <summary>Whether to offer the default-settings-DB picker (install / update only, like the geo option).</summary>
    public bool ShowSeedDbOption => Phase == Phase.Ready && (ShowInstall || ShowUpdate);

    private void PickSeedDb()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файл настроек (state.db)",
            Filter = "База настроек SQLite (*.db)|*.db|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            SeedDbPath = dialog.FileName;
        }
    }

    private void RaiseVisibility()
    {
        Raise(nameof(ShowActions));
        Raise(nameof(ShowInstall));
        Raise(nameof(ShowUpdate));
        Raise(nameof(ShowRepair));
        Raise(nameof(ShowRemove));
        Raise(nameof(ShowProgress));
        Raise(nameof(ShowDone));
        Raise(nameof(ShowDownloadOption));
        Raise(nameof(ShowSeedDbOption));
    }
}
