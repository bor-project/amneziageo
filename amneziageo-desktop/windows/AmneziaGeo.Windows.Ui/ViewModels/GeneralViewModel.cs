using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Styling;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// General screen: theme and language preferences, version/about, and the app self-update flow.
/// </summary>
internal sealed partial class GeneralViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly AgentConnection _connection;
    private readonly UiPreferences _prefs;

    private string _updateSetupUrl = string.Empty;
    private string? _bannerUpdateVersion;
    private string? _downloadedSetupPath;
    private string? _downloadedVersion;
    private CancellationTokenSource? _downloadCts;

    // Set while OnCultureChanged re-localizes the combos; suppresses their change handlers.
    private bool _syncingCombos;

    // Set while Apply seeds the connection settings from the snapshot; suppresses their autosave push.
    private bool _suppressSettingPush;

    /// <summary>
    /// UI language options.
    /// </summary>
    public ObservableCollection<string> Languages { get; } = [Loc.Instance.Get("Lang_System"), "Русский", "English"];

    [ObservableProperty]
    private int _selectedLanguageIndex;

    /// <summary>
    /// UI theme options.
    /// </summary>
    public ObservableCollection<string> Themes { get; } = [Loc.Instance.Get("Theme_System"), Loc.Instance.Get("Theme_Light"), Loc.Instance.Get("Theme_Dark")];

    [ObservableProperty]
    private int _selectedThemeIndex;

    [ObservableProperty]
    private string _appVersion = "AmneziaGeo -";

    // Raw engine version from the snapshot; empty renders the localized placeholder live on a language change.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmneziaVersion))]
    private string _engineVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateUrl))]
    private string _updateUrl = string.Empty;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateVersionBadgeText))]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    private string _updateVersion = string.Empty;

    [ObservableProperty]
    private string _updateDescription = string.Empty;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _updateDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _updateDownloaded;

    [ObservableProperty]
    private int _updateDownloadPercent;

    [ObservableProperty]
    private bool _updateBannerVisible;

    // Selective bundle export/import shown inline instead of a modal dialog; back returns to the general page.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralMain))]
    [NotifyPropertyChangedFor(nameof(IsBundleExport))]
    [NotifyPropertyChangedFor(nameof(IsBundleImport))]
    private BundleMode _bundleMode;

    [ObservableProperty]
    private BundleExportViewModel? _bundleExport;

    [ObservableProperty]
    private BundleImportViewModel? _bundleImport;

    /// <summary>
    /// Auto-connect the selected profile on service start (survive a reboot).
    /// </summary>
    [ObservableProperty]
    private bool _surviveReboot;

    /// <summary>
    /// Retry a desired connection at a fixed interval while it stays inactive.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodicReconnectIntervalEnabled))]
    private bool _periodicReconnect;

    /// <summary>
    /// Interval between periodic auto-reconnect attempts, in seconds.
    /// </summary>
    [ObservableProperty]
    private int _reconnectIntervalSeconds = 30;

    /// <summary>
    /// Show tray notifications for connection state changes.
    /// </summary>
    [ObservableProperty]
    private bool _showNotifications = true;

    /// <summary>
    /// Auto-reconnect interval presets, in seconds.
    /// </summary>
    public ObservableCollection<int> ReconnectIntervals { get; } = [10, 15, 30, 60, 120, 300];

    /// <summary>
    /// The interval input is editable only while periodic reconnect is on.
    /// </summary>
    public bool PeriodicReconnectIntervalEnabled => PeriodicReconnect;

    /// <summary>
    /// ctor
    /// </summary>
    public GeneralViewModel(MainWindowViewModel host, AgentConnection connection, UiPreferences prefs)
    {
        _host = host;
        _connection = connection;
        _prefs = prefs;
        // Seed backing fields from prefs without echoing OnChanged.
        _selectedThemeIndex = IndexForTheme(prefs.Theme);
        _selectedLanguageIndex = IndexForLanguage(prefs.Language);
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// Engine version label, or the localized placeholder when the agent reports none.
    /// </summary>
    public string AmneziaVersion => string.IsNullOrEmpty(EngineVersion) ? Loc.Instance.Get("MainVm_NotAvailable") : EngineVersion;

    public string UpdateVersionBadgeText => Loc.Instance.Get("Main_UpdateAvailableVersion", UpdateVersion);

    public string UpdateBannerText => Loc.Instance.Get("Main_UpdateBanner", UpdateVersion);

    /// <summary>
    /// Whether an update URL is configured (baked into the build from installer.config.json). When false the
    /// update section and its check control are hidden - there is nothing to check against.
    /// </summary>
    public bool HasUpdateUrl => !string.IsNullOrWhiteSpace(UpdateUrl);

    /// <summary>
    /// Show the download button only when idle: not while a download runs (then it is Cancel) and not once the
    /// setup is downloaded (then it is Install).
    /// </summary>
    public bool ShowDownloadButton => !UpdateDownloading && !UpdateDownloaded;

    /// <summary>
    /// Whether the normal general page is shown (not a bundle export/import sub-view).
    /// </summary>
    public bool IsGeneralMain => BundleMode == BundleMode.None;

    public bool IsBundleExport => BundleMode == BundleMode.Export;

    public bool IsBundleImport => BundleMode == BundleMode.Import;

    /// <summary>
    /// Applies the version and update-related snapshot fields; a freshly available version raises the banner.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        AppVersion = $"AmneziaGeo {(string.IsNullOrEmpty(snapshot.AgentVersion) ? "-" : snapshot.AgentVersion)}";
        EngineVersion = snapshot.EngineVersion;

        // Seed the connection settings without echoing an autosave push back to the agent.
        _suppressSettingPush = true;
        ShowNotifications = snapshot.ShowNotifications;
        SurviveReboot = snapshot.SurviveReboot;
        PeriodicReconnect = snapshot.PeriodicReconnect;
        EnsureReconnectInterval(snapshot.PeriodicReconnectIntervalSeconds);
        ReconnectIntervalSeconds = snapshot.PeriodicReconnectIntervalSeconds;
        _suppressSettingPush = false;

        UpdateUrl = snapshot.UpdateUrl;

        UpdateAvailable = snapshot.UpdateAvailable;
        UpdateVersion = snapshot.UpdateVersion;
        UpdateDescription = snapshot.UpdateDescription;
        _updateSetupUrl = snapshot.UpdateSetupUrl;

        // A newly offered version invalidates the setup downloaded for the previous one.
        if (UpdateDownloaded && !string.Equals(snapshot.UpdateVersion, _downloadedVersion, StringComparison.Ordinal))
        {
            UpdateDownloaded = false;
            _downloadedSetupPath = null;
            _downloadedVersion = null;
        }

        if (snapshot.UpdateAvailable && !string.IsNullOrEmpty(snapshot.UpdateVersion))
        {
            if (!string.Equals(snapshot.UpdateVersion, _bannerUpdateVersion, StringComparison.Ordinal))
            {
                _bannerUpdateVersion = snapshot.UpdateVersion;
                UpdateBannerVisible = true;
            }
        }
        else
        {
            UpdateBannerVisible = false;
            _bannerUpdateVersion = null;
        }
    }

    [RelayCommand]
    private async Task CheckUpdate()
    {
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateChecking");
        // The URL is baked into the build (installer config), not user-entered; just ask for a check.
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCheckUpdate, []));
        UpdateStatus = ack.Message;
    }

    /// <summary>
    /// Downloads the setup and stops. Installing is a separate, explicit step (#154).
    /// </summary>
    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (string.IsNullOrEmpty(_updateSetupUrl) || UpdateDownloading || UpdateDownloaded)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        UpdateDownloading = true;
        UpdateDownloadPercent = 0;
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloading");
        try
        {
            _downloadedSetupPath = await DownloadSetupAsync(_updateSetupUrl, new Progress<int>(p => UpdateDownloadPercent = p), cts.Token);
            _downloadedVersion = UpdateVersion;
            UpdateDownloaded = true;
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateReadyToInstall");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = string.Empty;
            UpdateDownloadPercent = 0;
        }
        catch (Exception ex)
        {
            // Show a friendly line; the raw error goes to the agent log for diagnostics.
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloadFailed");
            await LogToAgentAsync($"update download failed: {ex}");
        }
        finally
        {
            UpdateDownloading = false;
            _downloadCts = null;
        }
    }

    /// <summary>
    /// Aborts the in-progress setup download.
    /// </summary>
    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    // Forwards a diagnostic line to the agent log; the UI process keeps no log of its own.
    private async Task LogToAgentAsync(string message)
    {
        try
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpLogClient, [message]));
        }
        catch
        {
            // Best-effort: the failure is already surfaced to the user.
        }
    }

    /// <summary>
    /// Launches the already-downloaded setup and quits the app.
    /// </summary>
    [RelayCommand]
    private void ApplyUpdate()
    {
        if (!UpdateDownloaded || string.IsNullOrEmpty(_downloadedSetupPath))
        {
            return;
        }

        UpdateStatus = Loc.Instance.Get("MainVm_UpdateLaunching");
        try
        {
            // Full display (no /passive): the installer opens on its options step so the user reviews and
            // confirms the update before anything is applied, instead of a silent auto-apply. UPDATEFLOW=1
            // tells the BA this is the in-app update flow, so it lands straight on the update options.
            // UseShellExecute lets the bundle elevate (UAC) once. LAUNCHAFTER=1 restarts the app once the
            // update is applied (#155), honoured if the run ever falls back to non-interactive.
            Process.Start(new ProcessStartInfo(_downloadedSetupPath) { UseShellExecute = true, Arguments = "UPDATEFLOW=1 LAUNCHAFTER=1" });

            // Quit so the installer can replace the app's in-use files.
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateError", ex.Message);
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        UpdateBannerVisible = false;
    }

    // Open the selective bundle export inline: snapshot the current catalogue into a fresh export view model.
    [RelayCommand]
    private async Task OpenBundleExport()
    {
        var export = new BundleExportViewModel(_connection, _host.Profile.Profiles, _host.Config.Configs, _host.Routing.RoutingLists);
        await export.LoadRoutingRulesAsync();
        BundleExport = export;
        BundleMode = BundleMode.Export;
    }

    // Open the bundle import inline.
    [RelayCommand]
    private void OpenBundleImport()
    {
        BundleImport = new BundleImportViewModel(_connection);
        BundleMode = BundleMode.Import;
    }

    // Back from a bundle sub-view to the general page.
    [RelayCommand]
    private void CloseBundle()
    {
        BundleMode = BundleMode.None;
        BundleExport = null;
        BundleImport = null;
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_syncingCombos)
        {
            return;
        }

        var token = TokenForLanguageIndex(value);
        _prefs.Language = token;
        _prefs.Save();
        Loc.Instance.SetCulture(token);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_syncingCombos || value < 0)
        {
            return;
        }

        _prefs.Theme = TokenForThemeIndex(value);
        _prefs.Save();
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = ThemeVariantForIndex(value);
        }
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("show-notifications", value ? "on" : "off");
        }
    }

    partial void OnSurviveRebootChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("survive-reboot", value ? "on" : "off");
        }
    }

    partial void OnPeriodicReconnectChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("periodic-reconnect-enabled", value ? "on" : "off");
        }
    }

    partial void OnReconnectIntervalSecondsChanged(int value)
    {
        if (!_suppressSettingPush && value > 0)
        {
            _ = SetSettingAsync("periodic-reconnect-interval-seconds", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // Keeps the interval combo able to display an out-of-band value (e.g. set via CLI): a non-preset is
    // inserted in order so the ComboBox SelectedItem never goes null and writes 0 back into the property.
    private void EnsureReconnectInterval(int seconds)
    {
        if (seconds <= 0 || ReconnectIntervals.Contains(seconds))
        {
            return;
        }

        var index = 0;
        while (index < ReconnectIntervals.Count && ReconnectIntervals[index] < seconds)
        {
            index++;
        }

        ReconnectIntervals.Insert(index, seconds);
    }

    private async Task SetSettingAsync(string key, string value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value]));
    }

    private void OnCultureChanged()
    {
        // Replacing the selected item string resets the index-bound ComboBox to -1; capture and restore it.
        var language = SelectedLanguageIndex;
        var theme = SelectedThemeIndex;

        _syncingCombos = true;
        try
        {
            // Refresh the localized "System" entry in the language combo.
            if (Languages.Count > 0)
            {
                Languages[0] = Loc.Instance.Get("Lang_System");
            }

            // Re-localize the theme options.
            if (Themes.Count >= 3)
            {
                Themes[0] = Loc.Instance.Get("Theme_System");
                Themes[1] = Loc.Instance.Get("Theme_Light");
                Themes[2] = Loc.Instance.Get("Theme_Dark");
            }

            SelectedLanguageIndex = language;
            SelectedThemeIndex = theme;
        }
        finally
        {
            _syncingCombos = false;
        }

        // Re-raise all computed labels on a language change.
        OnPropertyChanged(string.Empty);
    }

    private static int IndexForLanguage(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "ru" => 1,
        "en" => 2,
        _ => 0,
    };

    private static string TokenForLanguageIndex(int index) => index switch
    {
        1 => "ru",
        2 => "en",
        _ => Loc.SystemToken,
    };

    private static int IndexForTheme(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0,
    };

    private static string TokenForThemeIndex(int index) => index switch
    {
        1 => "light",
        2 => "dark",
        _ => string.Empty,
    };

    private static ThemeVariant ThemeVariantForIndex(int index) => index switch
    {
        1 => ThemeVariant.Light,
        2 => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    // Streams the installer to a temp file, reporting integer download percent (mirrors the agent's
    // GeoFileUpdater loop but writes straight to disk - the setup is ~100 MB).
    private static async Task<string> DownloadSetupAsync(string url, IProgress<int> progress, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "AmneziaGeoSetup.exe");
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(path);
            var buffer = new byte[81920];
            long read = 0;
            var lastPercent = -1;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                {
                    var percent = (int)(read * 100 / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            return path;
        }
        catch
        {
            TryDeletePartial(path);
            throw;
        }
    }

    // Drops the half-written setup after a cancelled or failed download.
    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Which inline view the general screen shows: the page, or a bundle export / import sub-view.
/// </summary>
internal enum BundleMode
{
    None,
    Export,
    Import,
}
