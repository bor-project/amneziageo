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
    private readonly AgentConnection _connection;
    private readonly UiPreferences _prefs;

    private string _updateSetupUrl = string.Empty;
    private string? _bannerUpdateVersion;
    private string? _downloadedSetupPath;
    private string? _downloadedVersion;

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

    [ObservableProperty]
    private string _amneziaVersion = Loc.Instance.Get("MainVm_NotAvailable");

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
    private bool _updateDownloading;

    [ObservableProperty]
    private bool _updateDownloaded;

    [ObservableProperty]
    private int _updateDownloadPercent;

    [ObservableProperty]
    private bool _updateBannerVisible;

    /// <summary>
    /// ctor
    /// </summary>
    public GeneralViewModel(AgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        // Seed backing fields from prefs without echoing OnChanged.
        _selectedThemeIndex = IndexForTheme(prefs.Theme);
        _selectedLanguageIndex = IndexForLanguage(prefs.Language);
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    public string UpdateVersionBadgeText => Loc.Instance.Get("Main_UpdateAvailableVersion", UpdateVersion);

    public string UpdateBannerText => Loc.Instance.Get("Main_UpdateBanner", UpdateVersion);

    /// <summary>
    /// Whether an update URL is configured (baked into the build from installer.config.json). When false the
    /// update section and its check control are hidden - there is nothing to check against.
    /// </summary>
    public bool HasUpdateUrl => !string.IsNullOrWhiteSpace(UpdateUrl);

    /// <summary>
    /// Applies the version and update-related snapshot fields; a freshly available version raises the banner.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        AppVersion = $"AmneziaGeo {(string.IsNullOrEmpty(snapshot.AgentVersion) ? "-" : snapshot.AgentVersion)}";
        AmneziaVersion = string.IsNullOrEmpty(snapshot.EngineVersion) ? Loc.Instance.Get("MainVm_NotAvailable") : snapshot.EngineVersion;

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

        UpdateDownloading = true;
        UpdateDownloadPercent = 0;
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloading");
        try
        {
            _downloadedSetupPath = await DownloadSetupAsync(_updateSetupUrl, new Progress<int>(p => UpdateDownloadPercent = p));
            _downloadedVersion = UpdateVersion;
            UpdateDownloaded = true;
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateReadyToInstall");
        }
        catch (Exception ex)
        {
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateError", ex.Message);
        }
        finally
        {
            UpdateDownloading = false;
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
            // /passive: a single progress UI, no prompts. The display level propagates to the upgrade's
            // related-bundle uninstall, so the old version is removed WITHOUT its own second installer
            // window flashing alongside the new one. UseShellExecute lets the bundle elevate (UAC) once.
            // LAUNCHAFTER=1 tells the bundle's BA to restart the app once the update is applied (#155).
            Process.Start(new ProcessStartInfo(_downloadedSetupPath) { UseShellExecute = true, Arguments = "/passive LAUNCHAFTER=1" });

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

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var token = TokenForLanguageIndex(value);
        _prefs.Language = token;
        _prefs.Save();
        Loc.Instance.SetCulture(token);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (value < 0)
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

    private void OnCultureChanged()
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
    private static async Task<string> DownloadSetupAsync(string url, IProgress<int> progress)
    {
        var path = Path.Combine(Path.GetTempPath(), "AmneziaGeoSetup.exe");
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n));
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
}
