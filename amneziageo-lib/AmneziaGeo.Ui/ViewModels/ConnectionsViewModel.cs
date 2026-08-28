using System.Collections.ObjectModel;
using System.IO;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Connections screen: what the agent does with the tunnel itself, the proxy a device on this network is
/// pointed at, and the access point a device joins without being set up at all.
/// </summary>
internal partial class ConnectionsViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    // Set while Apply seeds the settings from the snapshot; suppresses their autosave push.
    private bool _suppressSettingPush;

    // How long after an edit of the accounts a snapshot is left to catch up before it may reseed the rows.
    private const int AccountEditWindowMs = 3000;

    // When the accounts were last edited here.
    private long _accountsTouchedAt;

    // When the name or the password of the access point was last edited here.
    private long _hotspotTouchedAt;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

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
    /// Keep several tunnels up at once.
    /// </summary>
    [ObservableProperty]
    private bool _multiServer;

    /// <summary>
    /// The interval input is editable only while periodic reconnect is on.
    /// </summary>
    public bool PeriodicReconnectIntervalEnabled => PeriodicReconnect;

    /// <summary>
    /// Whether the tunnel settings are offered (Windows only: the Android agent does not apply them).
    /// </summary>
    public bool CanConfigureConnection => OperatingSystem.IsWindows();

    /// <summary>
    /// Whether the several-servers switch is offered on this machine.
    /// </summary>
    public bool MultiServerOffered => AppFeatures.MultiServer;

    /// <summary>
    /// Whether the local proxy listens on its ports.
    /// </summary>
    [ObservableProperty]
    private bool _proxyEnabled;

    /// <summary>
    /// SOCKS5 port of the local proxy.
    /// </summary>
    [ObservableProperty]
    private string _proxySocksPort = "10808";

    /// <summary>
    /// HTTP port of the local proxy.
    /// </summary>
    [ObservableProperty]
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
    /// Whether the proxy only carries traffic while the tunnel is up, as it does on Android.
    /// </summary>
    public bool ProxyNeedsTunnel => OperatingSystem.IsAndroid();

    /// <summary>
    /// Whether the proxy admits nobody: a password is asked for and no account answers it.
    /// </summary>
    public bool ProxyAdmitsNobody => !ProxyAnonymous && !ProxyAccounts.Any(account => account.User.Trim().Length > 0);

    /// <summary>
    /// Bands the access point may ask for.
    /// </summary>
    public ObservableCollection<string> BandOptions { get; } =
    [
        Loc.Instance.Get("General_HotspotBandAuto"),
        Loc.Instance.Get("General_HotspotBand24"),
        Loc.Instance.Get("General_HotspotBand5"),
    ];

    /// <summary>
    /// Tab the section shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTunnelTab))]
    [NotifyPropertyChangedFor(nameof(IsProxyTab))]
    [NotifyPropertyChangedFor(nameof(IsWifiTab))]
    private string _shareTab = DefaultTab();

    /// <summary>
    /// Whether the access point is asked for.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private bool _hotspotEnabled;

    /// <summary>
    /// Band the access point asks for.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private int _selectedBandIndex;

    /// <summary>
    /// Network name of the access point.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private string _hotspotSsid = string.Empty;

    /// <summary>
    /// Password of the access point.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private string _hotspotPassword = string.Empty;

    /// <summary>
    /// Whether the network password is shown.
    /// </summary>
    [ObservableProperty]
    private bool _isHotspotPasswordRevealed;

    /// <summary>
    /// Whether this machine can raise an access point.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotBlocked))]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private bool _hotspotSupported;

    /// <summary>
    /// What keeps it from coming up; empty while nothing does.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotBlockedText))]
    private string _hotspotReason = HotspotReasons.Ready;

    /// <summary>
    /// Whether the access point is up.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private bool _hotspotRunning;

    /// <summary>
    /// Why the access point is down; empty while it holds.
    /// </summary>
    [ObservableProperty]
    private string _hotspotErrorText = string.Empty;

    /// <summary>
    /// Band the access point took.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotHintText))]
    private string _hotspotBandActual = string.Empty;

    /// <summary>
    /// Devices on the access point right now.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotClientsText))]
    private int _hotspotClientCount;

    /// <summary>
    /// How many devices the access point admits.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotClientsText))]
    private int _hotspotMaxClients;

    /// <summary>
    /// Whether this system raises an access point at all; elsewhere the section carries the proxy alone.
    /// </summary>
    public bool CanShareHotspot => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// Whether the section has more than one tab to switch between.
    /// </summary>
    public bool HasConnectionTabs => CanConfigureConnection || CanShareHotspot;

    /// <summary>
    /// Caption of the tunnel tab; empty where the tunnel settings are not offered, which drops the tab.
    /// </summary>
    public string TunnelTabText =>
        CanConfigureConnection ? Loc.Instance.Get("General_TunnelSection") : string.Empty;

    /// <summary>
    /// Whether the tunnel tab is shown.
    /// </summary>
    public bool IsTunnelTab => CanConfigureConnection && ShareTab == TunnelTab;

    /// <summary>
    /// Whether the proxy tab is shown; with a single tab it is the whole section.
    /// </summary>
    public bool IsProxyTab => !HasConnectionTabs || ShareTab == ProxyTab;

    /// <summary>
    /// Whether the access point tab is shown.
    /// </summary>
    public bool IsWifiTab => CanShareHotspot && ShareTab == WifiTab;

    /// <summary>
    /// Whether the access point fields are locked out.
    /// </summary>
    public bool HotspotBlocked => !HotspotSupported;

    /// <summary>
    /// What stands in the way of an access point on this machine. Each cause has its own remedy, so each
    /// carries its own line.
    /// </summary>
    public string HotspotBlockedText => HotspotReason switch
    {
        HotspotReasons.NoAdapter => Loc.Instance.Get("General_HotspotNoAdapter"),
        HotspotReasons.RadioOff => Loc.Instance.Get("General_HotspotRadioOff"),
        HotspotReasons.NoApMode => Loc.Instance.Get("General_HotspotNoApMode"),
        HotspotReasons.NoTools => Loc.Instance.Get("General_HotspotNoTools"),
        HotspotReasons.ServiceOff => Loc.Instance.Get("General_HotspotServiceOff"),
        HotspotReasons.NoPlatform => Loc.Instance.Get("General_HotspotNoPlatform"),
        _ => string.Empty,
    };

    /// <summary>
    /// The line under the access point fields: what it still needs, or the band it took.
    /// </summary>
    public string HotspotHintText
    {
        get
        {
            if (!HotspotSupported || !HotspotEnabled)
            {
                return string.Empty;
            }

            if (HotspotSsid.Length == 0 || HotspotPassword.Length == 0)
            {
                return Loc.Instance.Get("General_HotspotNeedsName");
            }

            if (!SettingKeys.IsValidHotspotSsid(HotspotSsid))
            {
                return Loc.Instance.Get("General_HotspotBadSsid");
            }

            if (!SettingKeys.IsValidHotspotPassword(HotspotPassword))
            {
                return Loc.Instance.Get("General_HotspotBadPassword");
            }

            if (!HotspotRunning)
            {
                return string.Empty;
            }

            if (HotspotBandActual.Length == 0 || string.Equals(HotspotBandActual, BandToken, StringComparison.Ordinal))
            {
                return Loc.Instance.Get("General_HotspotRunning");
            }

            return HotspotBandActual == HotspotBands.Auto
                ? Loc.Instance.Get("General_HotspotBandAdapter")
                : Loc.Instance.Get("General_HotspotBandActual", BandLabel(HotspotBandActual));
        }
    }

    /// <summary>
    /// How many devices are on the access point, against how many it admits.
    /// </summary>
    public string HotspotClientsText => Loc.Instance.Get("General_HotspotClientsOf", HotspotClientCount, HotspotMaxClients);

    // Tabs the section stands on.
    private const string TunnelTab = "tunnel";
    private const string ProxyTab = "proxy";
    private const string WifiTab = "wifi";

    // Opens the section on the tunnel where it is offered, on the proxy elsewhere.
    private static string DefaultTab() => OperatingSystem.IsWindows() ? TunnelTab : ProxyTab;

    // Band the picked row stands for.
    private string BandToken => SelectedBandIndex switch
    {
        1 => HotspotBands.TwoPointFour,
        2 => HotspotBands.Five,
        _ => HotspotBands.Auto,
    };

    private static int BandIndex(string band) => HotspotBands.Of(band) switch
    {
        HotspotBands.TwoPointFour => 1,
        HotspotBands.Five => 2,
        _ => 0,
    };

    private static string BandLabel(string band) => HotspotBands.Of(band) switch
    {
        HotspotBands.TwoPointFour => Loc.Instance.Get("General_HotspotBand24"),
        HotspotBands.Five => Loc.Instance.Get("General_HotspotBand5"),
        _ => Loc.Instance.Get("General_HotspotBandAuto"),
    };


    /// <summary>
    /// ctor
    /// </summary>
    public ConnectionsViewModel(IAgentConnection connection)
    {
        _connection = connection;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// What the mode adds under the tunnel settings; null on a machine that keeps one tunnel.
    /// </summary>
    public virtual object? TunnelExtras => null;

    /// <summary>
    /// Takes what the agent reports about the tunnel settings, the proxy and the access point.
    /// </summary>
    public virtual void Apply(StatusSnapshot snapshot)
    {
        // Seed the settings without echoing an autosave push back to the agent.
        _suppressSettingPush = true;
        ShowNotifications = snapshot.ShowNotifications;
        MultiServer = snapshot.MultiServer;
        SurviveReboot = snapshot.SurviveReboot;
        PeriodicReconnect = snapshot.PeriodicReconnect;
        ReconnectIntervalSeconds = snapshot.PeriodicReconnectIntervalSeconds;
        ProxyEnabled = snapshot.ProxyEnabled;
        ProxyAnonymous = snapshot.ProxyAnonymous;
        ProxySocksPort = snapshot.ProxySocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProxyHttpPort = snapshot.ProxyHttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyProxyAccounts(snapshot.ProxyCredentials);
        HotspotEnabled = ShareModes.CarriesWifi(snapshot.ShareMode);
        SelectedBandIndex = BandIndex(snapshot.HotspotBand);
        ApplyHotspotSecrets(snapshot);
        _suppressSettingPush = false;
        ApplyProxyEndpoints(snapshot);
        ApplyProxyClients(snapshot);
        ProxyErrorText = snapshot.ProxyEnabled ? snapshot.ProxyError : string.Empty;
        HotspotSupported = snapshot.HotspotSupported;
        HotspotReason = snapshot.HotspotReason;
        HotspotRunning = snapshot.HotspotRunning;
        HotspotErrorText = snapshot.HotspotError;
        HotspotBandActual = snapshot.HotspotBandActual;
        HotspotClientCount = snapshot.HotspotClients;
        HotspotMaxClients = snapshot.HotspotMaxClients;
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

    partial void OnHotspotEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync(SettingKeys.ShareMode, value ? ShareModes.Both : ShareModes.Lan);
        }
    }

    partial void OnSelectedBandIndexChanged(int value)
    {
        if (!_suppressSettingPush && value >= 0)
        {
            _ = SetSettingAsync(SettingKeys.HotspotBand, BandToken);
        }
    }

    // A half-typed name is not a name; an empty one takes the access point down.
    partial void OnHotspotSsidChanged(string value)
    {
        if (_suppressSettingPush)
        {
            return;
        }

        _hotspotTouchedAt = Environment.TickCount64;
        if (value.Length == 0 || SettingKeys.IsValidHotspotSsid(value))
        {
            _ = SetSettingAsync(SettingKeys.HotspotSsid, value);
        }
    }

    partial void OnHotspotPasswordChanged(string value)
    {
        if (_suppressSettingPush)
        {
            return;
        }

        _hotspotTouchedAt = Environment.TickCount64;
        if (value.Length == 0 || SettingKeys.IsValidHotspotPassword(value))
        {
            _ = SetSettingAsync(SettingKeys.HotspotPassword, value);
        }
    }

    /// <summary>
    /// Turns the section to one of its tabs.
    /// </summary>
    [RelayCommand]
    private void SelectShareTab(string tab)
    {
        ShareTab = tab;
    }

    /// <summary>
    /// Shows or hides the network password.
    /// </summary>
    [RelayCommand]
    private void ToggleHotspotReveal()
    {
        IsHotspotPasswordRevealed = !IsHotspotPasswordRevealed;
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

    // The name and the password come back from the agent only when nothing was typed here in the last seconds.
    private void ApplyHotspotSecrets(StatusSnapshot snapshot)
    {
        if (Environment.TickCount64 - _hotspotTouchedAt < AccountEditWindowMs)
        {
            return;
        }

        HotspotSsid = snapshot.HotspotSsid;
        HotspotPassword = snapshot.HotspotPassword;
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

    partial void OnShowNotificationsChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("show-notifications", value ? "on" : "off");
        }
    }

    partial void OnMultiServerChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync(SettingKeys.MultiServer, value ? "on" : "off");
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

    protected async Task SetSettingAsync(string key, string value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value]));
    }

    private void OnCultureChanged()
    {
        // Replacing the selected item string resets the index-bound ComboBox to -1; capture and restore it.
        var band = SelectedBandIndex;

        _suppressSettingPush = true;
        try
        {
            if (BandOptions.Count >= 3)
            {
                BandOptions[0] = Loc.Instance.Get("General_HotspotBandAuto");
                BandOptions[1] = Loc.Instance.Get("General_HotspotBand24");
                BandOptions[2] = Loc.Instance.Get("General_HotspotBand5");
            }

            SelectedBandIndex = band;
        }
        finally
        {
            _suppressSettingPush = false;
        }

        // Re-raise all computed labels on a language change.
        OnPropertyChanged(string.Empty);
    }
}
