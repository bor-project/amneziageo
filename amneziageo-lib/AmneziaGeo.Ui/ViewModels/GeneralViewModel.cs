using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// General screen: theme and language preferences, version/about, and the app self-update flow.
/// </summary>
internal sealed partial class GeneralViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

    private string _updateSetupUrl = string.Empty;
    private string _expectedSha256 = string.Empty;
    private string? _bannerUpdateVersion;
    private string? _downloadedSetupPath;
    private string? _downloadedVersion;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _upToDateCts;
    private DispatcherTimer? _autoUpdateTimer;
    private bool _autoCheckArmed;

    // The Linux and Android agents own the update: they download the package and hand it to the installer, so the
    // window only relays the commands and mirrors the state the snapshot carries.
    private readonly bool _agentUpdates = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
    private string _agentPhase = string.Empty;
    private string? _installingVersion;

    // Set by the host to run the byte-pump under the process-alive pin, so closing a window mid-download neither
    // quits the app nor aborts the download (#21). Falls back to a direct download when unset.
    private Action? _pinnedDownloadRunner;

    // Set while OnCultureChanged re-localizes the combos; suppresses their change handlers.
    private bool _syncingCombos;

    // Set while a combo restore is queued.
    private bool _restoringCombos;

    // Set while Apply seeds the connection settings from the snapshot; suppresses their autosave push.
    private bool _suppressSettingPush;

    // How long after an edit of the accounts a snapshot is left to catch up before it may reseed the rows.
    private const int AccountEditWindowMs = 3000;

    // When the accounts were last edited here.
    private long _accountsTouchedAt;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionDivider))]
    [NotifyPropertyChangedFor(nameof(ShowRepairDivider))]
    private bool _isCompact;

    partial void OnIsCompactChanged(bool value)
    {
        if (BundleExport is { } export)
        {
            export.IsCompact = value;
        }
    }

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

    // Baked build target (win-<arch>[-fdd]); empty on a build with none (dev / non-Windows), hiding the row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildType))]
    [NotifyPropertyChangedFor(nameof(HasBuildType))]
    private string _buildTarget = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    [NotifyPropertyChangedFor(nameof(DownloadActive))]
    private bool _updateDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    [NotifyPropertyChangedFor(nameof(DownloadActive))]
    private bool _updateDownloaded;

    // The system installer holds the package: the download step is over and must not be offered again.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    private bool _updateInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadActive))]
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
    /// Auto-connect the selected config on service start (survive a reboot).
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
    /// Offer prerelease (beta) versions in the update check.
    /// </summary>
    [ObservableProperty]
    private bool _allowPrerelease;

    /// <summary>
    /// Whether the local proxy listens on its ports.
    /// </summary>
    [ObservableProperty]
    private bool _proxyEnabled;

    /// <summary>
    /// SOCKS5 port of the local proxy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyEnabledHint))]
    private string _proxySocksPort = "10808";

    /// <summary>
    /// HTTP port of the local proxy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyEnabledHint))]
    private string _proxyHttpPort = "10809";

    /// <summary>
    /// Whether the proxy admits a client without an account.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyAdmitsNobody))]
    private bool _proxyAnonymous;

    /// <summary>
    /// Why the listener is down; empty while it holds.
    /// </summary>
    [ObservableProperty]
    private string _proxyErrorText = string.Empty;

    /// <summary>
    /// Whether the clients of the proxy are listed under their count.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyClientsGlyph))]
    private bool _isProxyClientsExpanded;

    /// <summary>
    /// How many connections the clients hold right now.
    /// </summary>
    [ObservableProperty]
    private int _proxyClientCount;

    /// <summary>
    /// Whether anyone is using the proxy right now.
    /// </summary>
    [ObservableProperty]
    private bool _hasProxyClients;

    /// <summary>
    /// Addresses a client points at, one row per front and address; every one of them is copyable.
    /// </summary>
    public ObservableCollection<ProxyEndpointRow> ProxyEndpoints { get; } = [];

    /// <summary>
    /// Clients holding a connection right now.
    /// </summary>
    public ObservableCollection<ProxyClientRow> ProxyClients { get; } = [];

    /// <summary>
    /// Accounts the proxy admits clients under.
    /// </summary>
    public ObservableCollection<ProxyAccountViewModel> ProxyAccounts { get; } = [];

    /// <summary>
    /// Collapse arrow, turned down while the clients are shown.
    /// </summary>
    public string ProxyClientsGlyph => IsProxyClientsExpanded ? "▾" : "◂";

    /// <summary>
    /// Who may use the proxy and on which ports.
    /// </summary>
    public string ProxyEnabledHint => Loc.Instance.Get("General_ProxyEnabledHint", ProxySocksPort, ProxyHttpPort);

    /// <summary>
    /// Whether the proxy only carries traffic while the tunnel is up, as it does on Android.
    /// </summary>
    public bool ProxyNeedsTunnel => OperatingSystem.IsAndroid();

    /// <summary>
    /// Whether the proxy admits nobody: a password is asked for and no account answers it.
    /// </summary>
    public bool ProxyAdmitsNobody => !ProxyAnonymous && !ProxyAccounts.Any(account => account.User.Trim().Length > 0);

    /// <summary>
    /// The interval input is editable only while periodic reconnect is on.
    /// </summary>
    public bool PeriodicReconnectIntervalEnabled => PeriodicReconnect;

    /// <summary>
    /// Whether the network-repair action is offered (Windows only).
    /// </summary>
    public bool CanRepairNetwork => OperatingSystem.IsWindows();

    /// <summary>
    /// Показывает карточку настроек соединения только на Windows (Android-агент их не применяет).
    /// </summary>
    public bool CanConfigureConnection => OperatingSystem.IsWindows();

    /// <summary>
    /// Ставит разделитель перед карточкой соединения: на узком экране рамок у карточек нет, а сама она
    /// есть не на каждой платформе.
    /// </summary>
    public bool ShowConnectionDivider => IsCompact && CanConfigureConnection;

    /// <summary>
    /// Ставит разделитель перед карточкой починки сети.
    /// </summary>
    public bool ShowRepairDivider => IsCompact && CanRepairNetwork;

    /// <summary>
    /// Transient result line for the network-repair action.
    /// </summary>
    [ObservableProperty]
    private string _networkRepairStatus = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public GeneralViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
    {
        _host = host;
        _connection = connection;
        _prefs = prefs;
        // Seed backing fields from prefs without echoing OnChanged.
        _selectedThemeIndex = IndexForTheme(prefs.Theme);
        _selectedLanguageIndex = IndexForLanguage(prefs.Language);
        _probeUploadUrl = prefs.ProbeUploadUrl;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// Speed service the send leg of a probe uploads to; empty measures against the built-in one, and an
    /// arbitrary destination is never uploaded to because it owes nobody an upload.
    /// </summary>
    [ObservableProperty]
    private string _probeUploadUrl = string.Empty;

    partial void OnProbeUploadUrlChanged(string value)
    {
        _prefs.ProbeUploadUrl = value.Trim();
        _prefs.Save();
    }

    /// <summary>
    /// The service a probe uploads to while the field is left empty.
    /// </summary>
    public string ProbeUploadDefault => AmneziaGeo.Ipc.ChannelProbe.DefaultUploadUrl;

    /// <summary>
    /// Engine version label, or the localized placeholder when the agent reports none.
    /// </summary>
    public string AmneziaVersion => string.IsNullOrEmpty(EngineVersion) ? Loc.Instance.Get("MainVm_NotAvailable") : EngineVersion;

    /// <summary>
    /// Whether the build carries a baked build target, so the About build-type row is shown.
    /// </summary>
    public bool HasBuildType => !string.IsNullOrEmpty(BuildTarget);

    /// <summary>
    /// Project home page, shown in About and opened by its link.
    /// </summary>
    public string ProjectUrl => "https://github.com/bor-project/amneziageo";

    /// <summary>
    /// Human-readable build type from the baked target: architecture plus payload (e.g. "x64 · SCD").
    /// </summary>
    public string BuildType => FormatBuildType(BuildTarget);

    // Turns "win-<arch>" / "win-<arch>-fdd" into "<arch> · SCD" / "<arch> · FDD"; the unmarked payload is
    // self-contained, so a target with no "-fdd" suffix reads as SCD.
    private static string FormatBuildType(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return string.Empty;
        }

        var body = target.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? target["win-".Length..] : target;
        var dash = body.IndexOf('-');
        var arch = dash >= 0 ? body[..dash] : body;
        var payload = dash >= 0 && body[(dash + 1)..].Equals("fdd", StringComparison.OrdinalIgnoreCase) ? "FDD" : "SCD";
        return $"{arch} · {payload}";
    }

    public string UpdateVersionBadgeText => Loc.Instance.Get("Main_UpdateAvailableVersion", UpdateVersion);

    public string UpdateBannerText => Loc.Instance.Get("Main_UpdateBanner", UpdateVersion);

    /// <summary>
    /// Whether an update URL is configured (baked into the build from installer.config.json). When false the
    /// update section and its check control are hidden - there is nothing to check against.
    /// </summary>
    public bool HasUpdateUrl => !string.IsNullOrWhiteSpace(UpdateUrl);

    /// <summary>
    /// Set once the installer has been launched and the app is shutting down to be replaced, so the host does
    /// not resurrect a window during teardown.
    /// </summary>
    public bool InstallerLaunched { get; private set; }

    /// <summary>
    /// Sets the host runner that performs a download under the process-alive pin (#21).
    /// </summary>
    public void SetPinnedDownloadRunner(Action runner) => _pinnedDownloadRunner = runner;

    /// <summary>
    /// Whether the last download was cancelled by a host relay (an exit), so a windowless worker exits rather
    /// than surfacing the window (#21).
    /// </summary>
    public bool WasDownloadCancelledByHost { get; private set; }

    /// <summary>
    /// Show the download button only when idle: not while a download runs (then it is Cancel), not once the
    /// setup is downloaded and not while the system installer holds it (then it is Install).
    /// </summary>
    public bool ShowDownloadButton => !UpdateDownloading && !UpdateDownloaded && !UpdateInstalling;

    /// <summary>
    /// Hide the check-update button while a setup is downloading, downloaded or being installed (#6).
    /// </summary>
    public bool ShowCheckUpdateButton => !UpdateDownloading && !UpdateDownloaded && !UpdateInstalling;

    /// <summary>
    /// Whether a download is actively streaming and not yet ready. Drives the percent label and the cancel
    /// control: both drop once the setup is downloaded (Install takes over) or the percent reaches 100.
    /// </summary>
    public bool DownloadActive => UpdateDownloading && !UpdateDownloaded && UpdateDownloadPercent < 100;

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
        BuildTarget = snapshot.BuildTarget;

        // Seed the connection settings without echoing an autosave push back to the agent.
        _suppressSettingPush = true;
        ShowNotifications = snapshot.ShowNotifications;
        AllowPrerelease = snapshot.AllowPrerelease;
        SurviveReboot = snapshot.SurviveReboot;
        PeriodicReconnect = snapshot.PeriodicReconnect;
        ReconnectIntervalSeconds = snapshot.PeriodicReconnectIntervalSeconds;
        ProxyEnabled = snapshot.ProxyEnabled;
        ProxyAnonymous = snapshot.ProxyAnonymous;
        ProxySocksPort = snapshot.ProxySocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProxyHttpPort = snapshot.ProxyHttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyProxyAccounts(snapshot.ProxyCredentials);
        _suppressSettingPush = false;
        ApplyProxyEndpoints(snapshot);
        ApplyProxyClients(snapshot);
        ProxyErrorText = snapshot.ProxyEnabled ? snapshot.ProxyError : string.Empty;

        UpdateUrl = snapshot.UpdateUrl;

        // Fire the open check once the agent has reported an update URL, then leave the hourly timer to it (#22).
        if (_autoCheckArmed && HasUpdateUrl)
        {
            _autoCheckArmed = false;
            _ = SilentCheckUpdateAsync();
        }

        UpdateAvailable = snapshot.UpdateAvailable;
        UpdateVersion = snapshot.UpdateVersion;
        UpdateDescription = snapshot.UpdateDescription;
        _updateSetupUrl = snapshot.UpdateSetupUrl;
        _expectedSha256 = snapshot.UpdateSetupSha256;

        if (_agentUpdates)
        {
            ApplyAgentDownloadState(snapshot);
        }
        else
        {
            ApplyOwnDownloadState(snapshot);
        }

        if (snapshot.UpdateAvailable && !string.IsNullOrEmpty(snapshot.UpdateVersion))
        {
            if (!string.Equals(snapshot.UpdateVersion, _bannerUpdateVersion, StringComparison.Ordinal))
            {
                _bannerUpdateVersion = snapshot.UpdateVersion;
                UpdateBannerVisible = TakeBannerTurn(snapshot.UpdateVersion);
            }
        }
        else
        {
            UpdateBannerVisible = false;
            _bannerUpdateVersion = null;
        }
    }

    // Offers the banner once per version: the mark is kept in the preferences, so a restart brings it back no more.
    private bool TakeBannerTurn(string version)
    {
        if (string.Equals(version, _prefs.ShownUpdateVersion, StringComparison.Ordinal))
        {
            return false;
        }

        _prefs.ShownUpdateVersion = version;
        _prefs.Save();
        return true;
    }

    // Windows: this process owns the setup byte-pump, so the snapshot only carries what another window did.
    private void ApplyOwnDownloadState(StatusSnapshot snapshot)
    {
        // A cancel was requested (e.g. the tray is exiting during a download): abort the in-flight byte-pump so
        // it deletes its partial and returns to the available state (#21). Null-guarded, so a stale request after
        // the download already ended is a no-op. The flag lets a windowless worker exit instead of surfacing the
        // launcher when the cancel came from an exit.
        if (snapshot.UpdateCancelRequested && _downloadCts is not null)
        {
            WasDownloadCancelledByHost = true;
            _downloadCts.Cancel();
        }

        // The agent reports a download running but this single-instance process is not the one doing it: a prior
        // owner died without reporting (e.g. a force-killed exit), so clear the stale phase to recover the tray's
        // exit-confirm and state (#21).
        if (snapshot.UpdateDownloading && _downloadCts is null)
        {
            _ = ReportDownloadAsync("idle", 0, string.Empty, string.Empty);
        }

        // Adopt a setup another process downloaded (the windowless --update worker) so this process can install
        // it; the process that ran the download already holds these locally, and one that is downloading now
        // keeps its own in-flight state.
        if (snapshot.UpdateDownloaded && _downloadedSetupPath is null && _downloadCts is null)
        {
            _downloadedSetupPath = snapshot.UpdateSetupPath;
            _downloadedVersion = snapshot.UpdateVersion;
            UpdateDownloaded = true;
        }

        // A newly offered version invalidates the setup downloaded for the previous one.
        if (UpdateDownloaded && !string.Equals(snapshot.UpdateVersion, _downloadedVersion, StringComparison.Ordinal))
        {
            UpdateDownloaded = false;
            _downloadedSetupPath = null;
            _downloadedVersion = null;
        }
    }

    // Linux: the agent runs the download and the install, so the window follows the phase it publishes.
    private void ApplyAgentDownloadState(StatusSnapshot snapshot)
    {
        UpdateDownloading = snapshot.UpdateDownloading;
        UpdateDownloadPercent = snapshot.UpdateDownloadPercent;

        // The install keeps the downloaded state: dropping it while the system dialogs are up put Download and
        // Check back on screen under a status line that reads "installing".
        UpdateDownloaded = snapshot.UpdateDownloaded;
        UpdateInstalling = snapshot.UpdateInstalling;
        _downloadedSetupPath = snapshot.UpdateSetupPath;
        _downloadedVersion = snapshot.UpdateVersion;

        // The agent comes back on the new version; this window keeps the old one until it is restarted too.
        if (_installingVersion is { Length: > 0 } installing
            && string.Equals(snapshot.AgentVersion, installing, StringComparison.Ordinal))
        {
            _installingVersion = null;
            _agentPhase = string.Empty;
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateRestartApp");
            return;
        }

        // Only a phase change writes the status line, so a manual check's own result is not overwritten.
        var phase = UpdatePhaseKey(snapshot);
        if (!string.Equals(phase, _agentPhase, StringComparison.Ordinal))
        {
            _agentPhase = phase;
            UpdateStatus = phase.Length == 0 ? string.Empty : Loc.Instance.Get(phase);
        }
    }

    // Resource key for the phase the agent publishes; empty while it is idle.
    private static string UpdatePhaseKey(StatusSnapshot snapshot)
    {
        if (snapshot.UpdateInstalling)
        {
            return "MainVm_UpdateInstalling";
        }

        if (snapshot.UpdateDownloadFailed)
        {
            return "MainVm_UpdateDownloadFailed";
        }

        if (snapshot.UpdateDownloading)
        {
            return "MainVm_UpdateDownloading";
        }

        return snapshot.UpdateDownloaded ? "MainVm_UpdateReadyToInstall" : string.Empty;
    }

    /// <summary>
    /// Starts automatic update checks: one when the window opens (fired once the agent reports an update URL)
    /// and one every hour after. Results surface through the snapshot as the floating update banner; the manual
    /// check stays in settings (#22).
    /// </summary>
    public void BeginAutoUpdateChecks()
    {
        if (_autoUpdateTimer is not null)
        {
            return;
        }

        _autoCheckArmed = true;
        _autoUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _autoUpdateTimer.Tick += (_, _) => _ = SilentCheckUpdateAsync();
        _autoUpdateTimer.Start();
    }

    // Asks the agent to check now without touching the status line: the resulting snapshot raises the update
    // banner when an update is offered. Backs the automatic open + hourly checks (#22); the manual settings
    // check keeps its own checking / up-to-date feedback.
    private async Task SilentCheckUpdateAsync()
    {
        if (!HasUpdateUrl)
        {
            return;
        }

        try
        {
            await _connection.SendCommandRawAsync(new IpcCommand(IpcContract.OpCheckUpdate, ["silent"]));
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task CheckUpdate()
    {
        CancelUpToDateAutoHide();
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateChecking");
        // The URL is baked into the build (installer config), not user-entered; just ask for a check. The raw reply
        // keeps its localization key so the check can tell up-to-date from available from an error (#3).
        try
        {
            var ack = await _connection.SendCommandRawAsync(new IpcCommand(IpcContract.OpCheckUpdate, []));
            ApplyCheckResult(ack);
        }
        catch
        {
            UpdateStatus = Loc.Instance.Get("Agent_UpdateCheckFailed");
        }
    }

    // Maps the check reply to the status line: up-to-date shows a transient notice, an offer clears the line for
    // the badge and download button, an error leaves a message the Check button retries (#3).
    private void ApplyCheckResult(IpcAck ack)
    {
        IpcMessage.TryParse(ack.Message, out var key, out var args);
        switch (key)
        {
            case "Agent_UpToDate":
                UpdateStatus = Loc.Instance.Get("MainVm_UpToDate");
                StartUpToDateAutoHide(UpdateStatus);
                break;
            case "Agent_UpdateAvailable":
                UpdateStatus = string.Empty;
                break;
            default:
                UpdateStatus = string.IsNullOrEmpty(key) ? Loc.Instance.Get("Agent_UpdateCheckFailed") : Loc.Instance.Get(key, args);
                break;
        }
    }

    // Hides the transient up-to-date notice after five seconds, unless a newer status has already replaced it (#3).
    private void StartUpToDateAutoHide(string message)
    {
        CancelUpToDateAutoHide();
        var cts = new CancellationTokenSource();
        _upToDateCts = cts;
        _ = HideStatusAfterDelayAsync(message, cts.Token);
    }

    private async Task HideStatusAfterDelayAsync(string message, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (string.Equals(UpdateStatus, message, StringComparison.Ordinal))
        {
            UpdateStatus = string.Empty;
        }
    }

    private void CancelUpToDateAutoHide()
    {
        _upToDateCts?.Cancel();
        _upToDateCts?.Dispose();
        _upToDateCts = null;
    }

    /// <summary>
    /// Downloads the setup and stops. Installing is a separate, explicit step (#154). Routed through the host so
    /// the byte-pump runs under the process-alive pin: closing a window then cannot abort it (#21).
    /// </summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        if (_agentUpdates)
        {
            _ = SendUpdateCommandAsync(IpcContract.OpDownloadUpdate);
            return;
        }

        if (_pinnedDownloadRunner is not null)
        {
            _pinnedDownloadRunner();
        }
        else
        {
            _ = DownloadCoreAsync();
        }
    }

    // The setup byte-pump: streams the installer, reporting each phase to the agent. A cancel deletes the partial
    // and returns to the available state; a failure latches the tray warning ("failed") and deletes the partial.
    private async Task DownloadCoreAsync()
    {
        if (string.IsNullOrEmpty(_updateSetupUrl) || UpdateDownloading || UpdateDownloaded)
        {
            return;
        }

        WasDownloadCancelledByHost = false;
        CancelUpToDateAutoHide();
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        UpdateDownloading = true;
        UpdateDownloadPercent = 0;
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloading");
        var version = UpdateVersion;
        await ReportDownloadAsync("downloading", 0, string.Empty, version);
        try
        {
            _downloadedSetupPath = await DownloadSetupAsync(_updateSetupUrl, new Progress<int>(p => ReportDownloadProgress(p, version)), cts.Token);
            _downloadedVersion = version;
            UpdateDownloaded = true;
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateReadyToInstall");
            await ReportDownloadAsync("downloaded", 100, _downloadedSetupPath, version);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = string.Empty;
            UpdateDownloadPercent = 0;
            await ReportDownloadAsync("idle", 0, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            // Show a friendly line; the raw error goes to the agent log for diagnostics. The "failed" phase drives
            // the tray warning balloon (#8).
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloadFailed");
            await LogToAgentAsync($"update download failed: {ex}");
            await ReportDownloadAsync("failed", 0, string.Empty, string.Empty);
        }
        finally
        {
            UpdateDownloading = false;
            _downloadCts = null;
        }
    }

    // Advances the shared download percent and relays it to the agent so the tray menu tracks it too (#17). The
    // clamp keeps a jittery report from moving the bar backwards; the final 100 rides the "downloaded" report.
    private void ReportDownloadProgress(int percent, string version)
    {
        // Hold the shown percent at 99 until the state flips to downloaded: the banner then swaps straight from
        // progress to Install with no dangling "100%" + Cancel and no blank tail while the partial is promoted.
        UpdateDownloadPercent = Math.Min(99, Math.Max(UpdateDownloadPercent, percent));
        _ = ReportDownloadAsync("downloading", UpdateDownloadPercent, string.Empty, version);
    }

    // Reports the setup download phase to the agent so the tray and every window share one state.
    private async Task ReportDownloadAsync(string phase, int percent, string path, string version)
    {
        try
        {
            await _connection.SendCommandAsync(new IpcCommand(
                IpcContract.OpReportUpdateDownload,
                [phase, percent.ToString(System.Globalization.CultureInfo.InvariantCulture), path, version]));
        }
        catch
        {
            // Best-effort: the agent's copy of the download state only drives the tray.
        }
    }

    /// <summary>
    /// Aborts the in-progress setup download.
    /// </summary>
    [RelayCommand]
    private void CancelDownload()
    {
        if (_agentUpdates)
        {
            _ = SendUpdateCommandAsync(IpcContract.OpCancelUpdateDownload);
            return;
        }

        _downloadCts?.Cancel();
    }

    // Relays an update command to the agent that owns the flow.
    private async Task SendUpdateCommandAsync(string op)
    {
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(op, []));
            if (!ack.Ok)
            {
                UpdateStatus = ack.Message;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateError", ex.Message);
        }
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
    /// Verifies the already-downloaded setup against the published hash, then launches it and quits the app.
    /// </summary>
    [RelayCommand]
    private async Task ApplyUpdate()
    {
        if (!UpdateDownloaded || UpdateInstalling || string.IsNullOrEmpty(_downloadedSetupPath))
        {
            return;
        }

        // The Linux packages are installed by the agent: it holds root and outlives the restart the install
        // triggers, so the window only asks for it and waits for the version to change.
        if (_agentUpdates)
        {
            _installingVersion = UpdateVersion;
            _agentPhase = "MainVm_UpdateInstalling";
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateInstalling");
            var started = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpApplyUpdate, []));
            if (!started.Ok)
            {
                _installingVersion = null;
                _agentPhase = string.Empty;
                UpdateStatus = started.Message;
            }

            return;
        }

        // Verify integrity before running the installer; a mismatch drops the file and returns to the download
        // step. A manifest without a hash (legacy) verifies as trusted so the flow still works.
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateVerifying");
        if (!await VerifySetupAsync(_downloadedSetupPath, _expectedSha256))
        {
            TryDeletePartial(_downloadedSetupPath);
            _downloadedSetupPath = null;
            _downloadedVersion = null;
            UpdateDownloaded = false;
            await ReportDownloadAsync("idle", 0, string.Empty, string.Empty);
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateVerifyFailed");
            return;
        }

        UpdateStatus = Loc.Instance.Get("MainVm_UpdateLaunching");
        try
        {
            // Full display (no /passive) so the run shows its progress, but every choice is already made here and
            // passed on the command line: the BA skips its options step and applies straight away. UseShellExecute
            // lets the bundle elevate (UAC) once.
            Process.Start(new ProcessStartInfo(_downloadedSetupPath)
            {
                UseShellExecute = true,
                Arguments = BuildInstallerArguments(),
            });

            InstallerLaunched = true;

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

    // The installer command line for an in-app update. UPDATEFLOW=1 marks the flow and, with the options
    // supplied here, tells the BA to run without its options step: the geo bases are left to the app's own
    // scheduled check, the settings are never reset, each shortcut is asked for only where one already exists,
    // and the connection is redialled only when the tunnel was up. UPDATEORIGIN / UPDATEVIEW carry where to
    // return - the window on its current view, or the tray with a notification; the BA records them only once
    // the update applied, so a cancelled or failed run leaves no stale resume behind.
    private string BuildInstallerArguments()
    {
        var windowed = !string.Equals(_host.CurrentSurface, "none", StringComparison.Ordinal);
        return string.Join(' ',
            "UPDATEFLOW=1",
            "LAUNCHAFTER=1",
            "DOWNLOADLISTS=0",
            "DELETECONFIG=0",
            $"AUTOCONNECT={Flag(_host.Home.IsTunnelActive)}",
            $"DESKTOPSHORTCUT={Flag(HasShortcut(Environment.SpecialFolder.CommonDesktopDirectory, string.Empty))}",
            $"STARTMENUSHORTCUT={Flag(HasShortcut(Environment.SpecialFolder.CommonPrograms, "AmneziaGeo"))}",
            $"SHOWCONSOLE={Flag(windowed)}",
            $"UPDATEORIGIN={(windowed ? "ui" : "none")}",
            $"UPDATEVIEW={_host.CurrentView}");
    }

    private static string Flag(bool value) => value ? "1" : "0";

    // Whether the shortcut the installer lays down is on disk; both live in the all-users locations the MSI
    // writes them to, so a shortcut the user removed stays removed by the update.
    private static bool HasShortcut(Environment.SpecialFolder folder, string subFolder)
    {
        try
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            return File.Exists(Path.Combine(root, subFolder, "AmneziaGeo.lnk"));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Background download entry point (tray balloon / menu "Download update"): waits for the update info to
    /// arrive, downloads the setup, and stops. Installing is a separate user step (#7). No-op when no update is
    /// on offer or it is already downloaded.
    /// </summary>
    public async Task RunBackgroundDownloadAsync()
    {
        // Reset before the availability wait so a no-op run (nothing to download) never reads a stale flag from a
        // prior host cancel (#21).
        WasDownloadCancelledByHost = false;

        for (var i = 0; i < 100 && (!UpdateAvailable || string.IsNullOrEmpty(_updateSetupUrl)); i++)
        {
            await Task.Delay(200);
        }

        if (!UpdateAvailable || string.IsNullOrEmpty(_updateSetupUrl))
        {
            return;
        }

        if (!UpdateDownloaded)
        {
            await DownloadCoreAsync();
        }
    }

    /// <summary>
    /// Background install entry point (tray balloon / menu "Install update"): waits for the downloaded setup to
    /// be seeded from the snapshot, then verifies and launches it. No-op when nothing is downloaded.
    /// </summary>
    public async Task RunApplyDownloadedAsync()
    {
        for (var i = 0; i < 100 && !UpdateDownloaded; i++)
        {
            await Task.Delay(200);
        }

        if (UpdateDownloaded)
        {
            await ApplyUpdate();
        }
    }

    // Hashes the downloaded setup and compares it to the manifest hash. An empty expected hash (legacy manifest)
    // passes so the flow keeps working; any read/hash failure fails closed.
    private static async Task<bool> VerifySetupAsync(string path, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return true;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
            return string.Equals(Convert.ToHexStringLower(hash), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        UpdateBannerVisible = false;
    }

    /// <summary>
    /// Elevated one-shot that reverts a leftover DNS redirect so the internet works again after a dead or hung
    /// agent stranded the resolver. A separate elevated process, so it works even when the agent is unresponsive;
    /// touches DNS only - not configs or routes.
    /// </summary>
    [RelayCommand]
    private void RepairNetwork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.App.exe");
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--heal-dns",
                UseShellExecute = true,
                Verb = "runas",
            });
            NetworkRepairStatus = Loc.Instance.Get("General_NetworkRepairDone");
        }
        catch (Exception ex)
        {
            // The user declined the elevation prompt, or the helper could not start.
            _ = LogToAgentAsync($"network repair launch failed: {ex}");
            NetworkRepairStatus = Loc.Instance.Get("General_NetworkRepairFailed");
        }
    }

    // Open the selective bundle export inline: snapshot the current catalogue into a fresh export view model.
    [RelayCommand]
    private async Task OpenBundleExport()
    {
        var export = new BundleExportViewModel(_connection, _host.Config.Configs, _host.Routing.RoutingLists)
        {
            IsCompact = IsCompact,
        };
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

        if (value < 0)
        {
            RestoreCombos();
            return;
        }

        var token = TokenForLanguageIndex(value);
        _prefs.Language = token;
        _prefs.Save();
        Loc.Instance.SetCulture(token);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_syncingCombos)
        {
            return;
        }

        if (value < 0)
        {
            RestoreCombos();
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

    partial void OnAllowPrereleaseChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("allow-prerelease", value ? "on" : "off");
        }
    }

    partial void OnProxyEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync(SettingKeys.ProxyEnabled, value ? "on" : "off");
        }
    }

    partial void OnProxyAnonymousChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync(SettingKeys.ProxyAnonymous, value ? "on" : "off");
        }
    }

    partial void OnProxySocksPortChanged(string value)
    {
        PushPort(SettingKeys.ProxySocksPort, value);
    }

    partial void OnProxyHttpPortChanged(string value)
    {
        PushPort(SettingKeys.ProxyHttpPort, value);
    }

    // A half-typed port is not a port; the agent keeps the last one until a whole number arrives.
    private void PushPort(string key, string value)
    {
        if (!_suppressSettingPush && SettingKeys.TryParseProxyPort(value, out var port))
        {
            _ = SetSettingAsync(key, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Adds an empty account row for the editor to fill in.
    /// </summary>
    [RelayCommand]
    private void AddProxyAccount()
    {
        ProxyAccounts.Add(NewProxyAccount(string.Empty, string.Empty));
        _accountsTouchedAt = Environment.TickCount64;
        OnPropertyChanged(nameof(ProxyAdmitsNobody));
    }

    /// <summary>
    /// Drops one account row.
    /// </summary>
    [RelayCommand]
    private void RemoveProxyAccount(ProxyAccountViewModel account)
    {
        ProxyAccounts.Remove(account);
        PushProxyAccounts();
    }

    /// <summary>
    /// Shows or hides the clients under their count.
    /// </summary>
    [RelayCommand]
    private void ToggleProxyClients()
    {
        IsProxyClientsExpanded = !IsProxyClientsExpanded;
    }

    private ProxyAccountViewModel NewProxyAccount(string user, string password)
    {
        return new ProxyAccountViewModel(user, password, PushProxyAccounts);
    }

    // The whole list travels as one setting, so any edit sends all of it.
    private void PushProxyAccounts()
    {
        _accountsTouchedAt = Environment.TickCount64;
        OnPropertyChanged(nameof(ProxyAdmitsNobody));
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync(SettingKeys.ProxyCredentials, ComposeProxyAccounts());
        }
    }

    private string ComposeProxyAccounts()
    {
        return ProxyCredentials.Compose(ProxyAccounts.Select(a => new ProxyAccount(a.User, a.Password)));
    }

    // The rows are rebuilt only when the agent carries something else, and not while an edit of the last seconds
    // may still be on its way there - a snapshot that crossed it would take the row out from under the caret.
    private void ApplyProxyAccounts(string credentials)
    {
        if (Environment.TickCount64 - _accountsTouchedAt < AccountEditWindowMs
            || string.Equals(ComposeProxyAccounts(), credentials, StringComparison.Ordinal))
        {
            return;
        }

        ProxyAccounts.Clear();
        foreach (var account in ProxyCredentials.Parse(credentials))
        {
            ProxyAccounts.Add(NewProxyAccount(account.User, account.Password));
        }

        OnPropertyChanged(nameof(ProxyAdmitsNobody));
    }

    // Where a client points: every address of this machine the neighbours can reach, and loopback only where
    // there is none.
    private void ApplyProxyEndpoints(StatusSnapshot snapshot)
    {
        var rows = new List<ProxyEndpointRow>();
        if (snapshot.ProxyEnabled && snapshot.ProxyError.Length == 0)
        {
            var hosts = snapshot.ProxyAddresses ?? [];
            if (hosts.Count == 0)
            {
                hosts = ["127.0.0.1"];
            }

            foreach (var host in hosts)
            {
                rows.Add(new ProxyEndpointRow("SOCKS5", $"{host}:{snapshot.ProxySocksPort}"));
                rows.Add(new ProxyEndpointRow("HTTP", $"{host}:{snapshot.ProxyHttpPort}"));
            }
        }

        Sync(ProxyEndpoints, rows);
    }

    private void ApplyProxyClients(StatusSnapshot snapshot)
    {
        var clients = snapshot.ProxyClients ?? [];
        var rows = new List<ProxyClientRow>();
        foreach (var client in clients)
        {
            var count = Loc.Instance.Get("General_ProxyClientConnections", client.Connections);
            var since = client.Since.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var name = client.Name.Length > 0 ? $"{client.Name} · " : string.Empty;
            rows.Add(new ProxyClientRow(client.Address, $"{name}{count} · {since}"));
        }

        ProxyClientCount = clients.Sum(client => client.Connections);
        HasProxyClients = rows.Count > 0;
        Sync(ProxyClients, rows);
    }

    // Replaces the rows only when they differ, so a snapshot every couple of seconds does not blink the list.
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows))
        {
            return;
        }

        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    partial void OnSurviveRebootChanged(bool value)
    {
        SyncBootAutostart(value);
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("survive-reboot", value ? "on" : "off");
        }
    }

    // Mirrors the resident tray's logon autostart to survive-reboot: with it on the tray comes up at logon so
    // the boot-connected tunnel keeps its icon and controls.
    private void SyncBootAutostart(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                var tray = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.Tray.exe");
                run.SetValue("AmneziaGeo", $"\"{tray}\" --autostart");
            }
            else
            {
                run.DeleteValue("AmneziaGeo", false);
            }
        }
        catch (Exception ex)
        {
            _ = LogToAgentAsync($"boot-autostart sync failed: {ex}");
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

    // Puts the saved choice back after a combo clears its selection.
    private void RestoreCombos()
    {
        if (_restoringCombos)
        {
            return;
        }

        _restoringCombos = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _syncingCombos = true;
                try
                {
                    SelectedLanguageIndex = IndexForLanguage(_prefs.Language);
                    SelectedThemeIndex = IndexForTheme(_prefs.Theme);
                }
                finally
                {
                    _syncingCombos = false;
                    _restoringCombos = false;
                }
            },
            DispatcherPriority.Background);
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

    // Full path of the completed setup; the download streams to the ".part" sibling and is promoted here only on
    // a clean finish, so an interrupted run leaves only the partial and this name is never a half-written file.
    private static string SetupPath => Path.Combine(Path.GetTempPath(), "AmneziaGeoSetup.exe");

    private static string PartialPath => SetupPath + ".part";

    /// <summary>
    /// Deletes a leftover partial download from an interrupted run (killed mid-download), so it is never taken
    /// for a ready update and the temp file does not linger (#21).
    /// </summary>
    public static void CleanupOrphanedPartial() => TryDeletePartial(PartialPath);

    // Streams the installer to a ".part" temp file, reporting integer download percent (mirrors the agent's
    // GeoFileUpdater loop but writes straight to disk - the setup is ~100 MB), then promotes it to the final name.
    private static async Task<string> DownloadSetupAsync(string url, IProgress<int> progress, CancellationToken ct)
    {
        var path = SetupPath;
        var partial = PartialPath;
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[81920];
                long read = 0;
                var lastPercent = -1;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
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
            }

            // The stream finished cleanly: promote the partial onto the final setup name.
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDeletePartial(partial);
            throw;
        }
    }

    // Drops a half-written or corrupt setup after a cancelled, failed, or unverified download.
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
