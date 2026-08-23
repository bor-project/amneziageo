using System.Diagnostics;
using System.Globalization;
using System.Text;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Access point of this machine: hostapd on the wireless adapter, dnsmasq behind it, and the subnet they serve
/// masqueraded into the tunnel. Nothing leaves that subnet except through the tunnel.
/// </summary>
internal sealed class LinuxHotspot(AgentLog log, string tunnelInterface) : IDisposable
{
    // Own nftables table; the rules of other software are never touched.
    private const string Table = "amneziageo-share";

    // Where the resolver of this agent listens.
    private const string Resolver = "127.0.0.71";

    // Subnets tried in turn until one is free.
    private static readonly string[] Candidates = ["192.168.144", "192.168.145", "192.168.146", "10.72.0"];

    // How many stations hostapd admits.
    private const int Capacity = 32;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotspotOptions _applied = new();
    private Process? _hostapd;
    private Process? _dnsmasq;
    private string _iface = string.Empty;
    private string _subnet = string.Empty;
    private string _runDirectory = string.Empty;
    private bool _forwardWasOn;
    private bool _tookOverFromManager;
    private bool _disposed;

    /// <summary>
    /// Whether the access point is up.
    /// </summary>
    public bool Running { get; private set; }

    /// <summary>
    /// Why the access point is down; empty while it holds.
    /// </summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this machine can raise an access point.
    /// </summary>
    public bool Supported { get; private set; }

    /// <summary>
    /// What stands in the way; empty while nothing does.
    /// </summary>
    public string Reason { get; private set; } = HotspotReasons.NoAdapter;

    /// <summary>
    /// Band the access point took.
    /// </summary>
    public string BandActual { get; private set; } = string.Empty;

    /// <summary>
    /// Stations on the access point right now.
    /// </summary>
    public int Clients { get; private set; }

    /// <summary>
    /// How many stations the access point admits.
    /// </summary>
    public static int MaxClients => Capacity;

    /// <summary>
    /// Reads what the adapter can do, so the window can say what is missing.
    /// </summary>
    public async Task ProbeAsync(CancellationToken ct)
    {
        var device = WirelessDevices().FirstOrDefault();
        if (device is null)
        {
            Set(false, HotspotReasons.NoAdapter);
            return;
        }

        if (Which("hostapd") is null || Which("dnsmasq") is null)
        {
            Set(false, HotspotReasons.NoTools);
            return;
        }

        if (await RadioBlockedAsync(ct).ConfigureAwait(false))
        {
            Set(false, HotspotReasons.RadioOff);
            return;
        }

        if (!await CarriesApModeAsync(ct).ConfigureAwait(false))
        {
            Set(false, HotspotReasons.NoApMode);
            return;
        }

        Set(true, HotspotReasons.Ready);
    }

    /// <summary>
    /// Moves the access point to the settings, raising or dropping it as they ask.
    /// </summary>
    public async Task ApplyAsync(HotspotOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (options == _applied && Running == options.Wanted)
            {
                return;
            }

            _applied = options;
            await StopCoreAsync(ct).ConfigureAwait(false);
            if (!options.Wanted || !Supported)
            {
                return;
            }

            await StartCoreAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error("hotspot", "the access point could not take the settings", ex);
            Error = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rereads the stations and the band in force, and notices a helper that died under us.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!Running)
        {
            return;
        }

        if (_hostapd is { HasExited: true } || _dnsmasq is { HasExited: true })
        {
            log.Warn("hotspot", "a helper of the access point went down; it is taken down whole");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Error = "hostapd or dnsmasq stopped";
                await StopCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            return;
        }

        Clients = await StationsAsync(ct).ConfigureAwait(false);
        BandActual = await BandInForceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the access point down and leaves nothing of it behind.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warn("hotspot", $"the access point did not come down cleanly: {ex.Message}");
        }

        _gate.Dispose();
    }

    private void Set(bool supported, string reason)
    {
        Supported = supported;
        Reason = reason;
    }

    private async Task StartCoreAsync(HotspotOptions options, CancellationToken ct)
    {
        _iface = WirelessDevices().FirstOrDefault() ?? string.Empty;
        if (_iface.Length == 0)
        {
            Error = "no wireless adapter";
            return;
        }

        _runDirectory = Path.Combine(AgentPaths.Root, "share");
        Directory.CreateDirectory(_runDirectory);

        // The configurations under it carry the network password, so nobody but root reads the folder at
        // all: a file written inside is unreachable before its own mode is set.
        File.SetUnixFileMode(_runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _subnet = await FreeSubnetAsync(ct).ConfigureAwait(false);

        await ReleaseFromManagerAsync(ct).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "link", "set", "dev", _iface, "up").ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "address", "flush", "dev", _iface).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "address", "add", $"{_subnet}.1/24", "dev", _iface).ConfigureAwait(false);
        await Shell.RunAsync("sysctl", ct, "-w", $"net.ipv6.conf.{_iface}.disable_ipv6=1").ConfigureAwait(false);

        _forwardWasOn = await ForwardingAsync(ct).ConfigureAwait(false);
        await Shell.RunAsync("sysctl", ct, "-w", "net.ipv4.ip_forward=1").ConfigureAwait(false);

        if (!await StartHostapdAsync(options, ct).ConfigureAwait(false))
        {
            await StopCoreAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!await StartDnsmasqAsync(ct).ConfigureAwait(false))
        {
            await StopCoreAsync(ct).ConfigureAwait(false);
            return;
        }

        await WriteRulesAsync(ct).ConfigureAwait(false);

        Running = true;
        Error = string.Empty;
        BandActual = await BandInForceAsync(ct).ConfigureAwait(false);
        log.Info("hotspot", $"'{options.Ssid}' up on {_iface}, subnet {_subnet}.0/24, out through {tunnelInterface}");
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        Stop(ref _dnsmasq);
        Stop(ref _hostapd);

        if (_iface.Length > 0)
        {
            await Shell.RunAsync("nft", ct, "delete", "table", "inet", Table).ConfigureAwait(false);
            await Shell.RunAsync("ip", ct, "address", "flush", "dev", _iface).ConfigureAwait(false);
            if (!_forwardWasOn)
            {
                await Shell.RunAsync("sysctl", ct, "-w", "net.ipv4.ip_forward=0").ConfigureAwait(false);
            }

            await RestoreToManagerAsync(ct).ConfigureAwait(false);
        }

        if (Running)
        {
            log.Info("hotspot", "the access point is down");
        }

        Running = false;
        Clients = 0;
        BandActual = string.Empty;
        _iface = string.Empty;
        _subnet = string.Empty;
    }

    private async Task<bool> StartHostapdAsync(HotspotOptions options, CancellationToken ct)
    {
        var path = Path.Combine(_runDirectory, "hostapd.conf");
        await File.WriteAllTextAsync(path, HostapdConfig(options, await CountryAsync(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        _hostapd = Spawn(Which("hostapd") ?? "hostapd", path);
        if (_hostapd is null)
        {
            Error = "hostapd did not start";
            return false;
        }

        // hostapd rejects a configuration it cannot serve within a second or two, and its exit says so.
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (!_hostapd.HasExited)
        {
            return true;
        }

        Error = FirstFault(await ReadOutputAsync(_hostapd).ConfigureAwait(false), "hostapd stopped");
        log.Error("hotspot", $"hostapd refused the access point: {Error}");
        _hostapd = null;
        return false;
    }

    private async Task<bool> StartDnsmasqAsync(CancellationToken ct)
    {
        var path = Path.Combine(_runDirectory, "dnsmasq.conf");
        await File.WriteAllTextAsync(path, DnsmasqConfig(), ct).ConfigureAwait(false);

        _dnsmasq = Spawn(Which("dnsmasq") ?? "dnsmasq", "--conf-file=" + path, "--keep-in-foreground");
        if (_dnsmasq is null)
        {
            Error = "dnsmasq did not start";
            return false;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (!_dnsmasq.HasExited)
        {
            return true;
        }

        Error = FirstFault(await ReadOutputAsync(_dnsmasq).ConfigureAwait(false), "dnsmasq stopped");
        log.Error("hotspot", $"dnsmasq refused the subnet of the access point: {Error}");
        _dnsmasq = null;
        return false;
    }

    // One table of our own: masquerade into the tunnel, the two directions the clients need, the MSS a tunnel
    // fits, and a drop for everything else out of that subnet so a downed tunnel leaks nothing.
    private async Task WriteRulesAsync(CancellationToken ct)
    {
        var rules = new StringBuilder();
        rules.Append(CultureInfo.InvariantCulture, $"table inet {Table} {{\n");
        rules.Append("  chain postrouting {\n");
        rules.Append("    type nat hook postrouting priority srcnat; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    ip saddr {_subnet}.0/24 oifname \"{tunnelInterface}\" masquerade\n");
        rules.Append("  }\n");
        rules.Append("  chain forward {\n");
        rules.Append("    type filter hook forward priority filter; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{_iface}\" oifname \"{tunnelInterface}\" tcp flags syn / syn,rst tcp option maxseg size set 1380\n");
        rules.Append(CultureInfo.InvariantCulture, $"    oifname \"{_iface}\" iifname \"{tunnelInterface}\" tcp flags syn / syn,rst tcp option maxseg size set 1380\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{_iface}\" oifname \"{tunnelInterface}\" accept\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{tunnelInterface}\" oifname \"{_iface}\" ct state established,related accept\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{_iface}\" drop\n");
        rules.Append("  }\n");
        rules.Append("}\n");

        var path = Path.Combine(_runDirectory, "share.nft");
        await File.WriteAllTextAsync(path, rules.ToString(), ct).ConfigureAwait(false);
        await Shell.RunAsync("nft", ct, "delete", "table", "inet", Table).ConfigureAwait(false);
        var applied = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
        if (applied.ExitCode != 0)
        {
            log.Warn("hotspot", $"the rules of the access point were refused: {applied.Output}");
            Error = applied.Output;
        }
    }

    private string HostapdConfig(HotspotOptions options, string country)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"interface={_iface}\n");
        text.Append("driver=nl80211\n");
        text.Append(CultureInfo.InvariantCulture, $"ssid={options.Ssid}\n");
        text.Append("utf8_ssid=1\n");
        text.Append(CultureInfo.InvariantCulture, $"hw_mode={(options.Band == HotspotBands.Five ? "a" : "g")}\n");

        // Zero hands the channel to the adapter's own survey; a channel chosen here is the surest way to keep the
        // point from coming up.
        text.Append("channel=0\n");
        if (country.Length == 2)
        {
            text.Append(CultureInfo.InvariantCulture, $"country_code={country}\n");
            text.Append("ieee80211d=1\n");
        }

        text.Append("ieee80211n=1\n");
        text.Append("wmm_enabled=1\n");
        text.Append("auth_algs=1\n");
        text.Append("ignore_broadcast_ssid=0\n");
        text.Append(CultureInfo.InvariantCulture, $"max_num_sta={Capacity}\n");
        text.Append("wpa=2\n");
        text.Append("wpa_key_mgmt=WPA-PSK\n");
        text.Append("rsn_pairwise=CCMP\n");
        text.Append(CultureInfo.InvariantCulture, $"wpa_passphrase={options.Password}\n");
        return text.ToString();
    }

    // The clients take their address, their gateway and their resolver from here; the resolver is ours, so their
    // names are seen by the same rules this machine's own names are.
    private string DnsmasqConfig()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"interface={_iface}\n");
        text.Append("bind-interfaces\n");
        text.Append("except-interface=lo\n");
        text.Append("no-resolv\n");
        text.Append(CultureInfo.InvariantCulture, $"server={Resolver}\n");
        text.Append(CultureInfo.InvariantCulture, $"dhcp-range={_subnet}.10,{_subnet}.200,255.255.255.0,12h\n");
        text.Append(CultureInfo.InvariantCulture, $"dhcp-option=option:router,{_subnet}.1\n");
        text.Append(CultureInfo.InvariantCulture, $"dhcp-option=option:dns-server,{_subnet}.1\n");
        text.Append(CultureInfo.InvariantCulture, $"listen-address={_subnet}.1\n");
        text.Append(CultureInfo.InvariantCulture, $"dhcp-leasefile={Path.Combine(_runDirectory, "leases")}\n");
        return text.ToString();
    }

    // A subnet no route of this machine already covers.
    private async Task<string> FreeSubnetAsync(CancellationToken ct)
    {
        var routes = await Shell.RunAsync("ip", ct, "route", "show").ConfigureAwait(false);
        foreach (var candidate in Candidates)
        {
            if (!routes.Output.Contains(candidate + ".", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return Candidates[0];
    }

    private async Task<bool> ForwardingAsync(CancellationToken ct)
    {
        var read = await Shell.RunAsync("sysctl", ct, "-n", "net.ipv4.ip_forward").ConfigureAwait(false);
        return read.Output.Trim() == "1";
    }

    // NetworkManager takes the adapter back the moment hostapd claims it, so it is asked to let go first.
    private async Task ReleaseFromManagerAsync(CancellationToken ct)
    {
        if (Which("nmcli") is null)
        {
            return;
        }

        var released = await Shell.RunAsync("nmcli", ct, "device", "set", _iface, "managed", "no").ConfigureAwait(false);
        _tookOverFromManager = released.ExitCode == 0;
    }

    private async Task RestoreToManagerAsync(CancellationToken ct)
    {
        if (!_tookOverFromManager || Which("nmcli") is null)
        {
            return;
        }

        await Shell.RunAsync("nmcli", ct, "device", "set", _iface, "managed", "yes").ConfigureAwait(false);
        _tookOverFromManager = false;
    }

    private async Task<int> StationsAsync(CancellationToken ct)
    {
        if (_iface.Length == 0)
        {
            return 0;
        }

        var dump = await Shell.RunAsync("iw", ct, "dev", _iface, "station", "dump").ConfigureAwait(false);
        return dump.ExitCode != 0
            ? 0
            : dump.Output.Split('\n').Count(line => line.StartsWith("Station ", StringComparison.Ordinal));
    }

    // What the adapter settled on, which is not always what was asked for: a card holding a client connection
    // must put its point on that connection's channel.
    private async Task<string> BandInForceAsync(CancellationToken ct)
    {
        if (_iface.Length == 0)
        {
            return string.Empty;
        }

        var info = await Shell.RunAsync("iw", ct, "dev", _iface, "info").ConfigureAwait(false);
        var megahertz = Megahertz(info.Output);
        return megahertz switch
        {
            >= 4900 => HotspotBands.Five,
            >= 2400 => HotspotBands.TwoPointFour,
            _ => string.Empty,
        };
    }

    // The frequency out of the "channel 6 (2437 MHz)" line iw prints.
    private static int Megahertz(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var open = line.IndexOf('(');
            var close = line.IndexOf(" MHz", StringComparison.Ordinal);
            if (open >= 0 && close > open
                && int.TryParse(line[(open + 1)..close], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static async Task<bool> RadioBlockedAsync(CancellationToken ct)
    {
        var list = await Shell.RunAsync("rfkill", ct, "list", "wifi").ConfigureAwait(false);
        return list.ExitCode == 0 && list.Output.Contains("blocked: yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> CarriesApModeAsync(CancellationToken ct)
    {
        var list = await Shell.RunAsync("iw", ct, "list").ConfigureAwait(false);
        return list.ExitCode == 0
            && list.Output.Split('\n').Any(line => line.Trim() is "* AP");
    }

    // Regulatory domain the 5 GHz band needs; without it hostapd refuses every channel above 14.
    private static async Task<string> CountryAsync(CancellationToken ct)
    {
        var region = await Shell.RunAsync("iw", ct, "reg", "get").ConfigureAwait(false);
        var token = Shell.Token(region.Output, "country");
        var country = token?.TrimEnd(':');
        return country is { Length: 2 } && country != "00" ? country : string.Empty;
    }

    private static IEnumerable<string> WirelessDevices()
    {
        const string root = "/sys/class/net";
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateDirectories(root).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (Directory.Exists(Path.Combine(path, "wireless")) || Directory.Exists(Path.Combine(path, "phy80211")))
            {
                yield return Path.GetFileName(path);
            }
        }
    }

    private static string? Which(string name)
    {
        foreach (var directory in new[] { "/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/local/bin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Process? Spawn(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(info);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Stop(ref Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    private static async Task<string> ReadOutputAsync(Process process)
    {
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        return (stdout + stderr).Trim();
    }

    // The line worth showing out of a helper's parting words.
    private static string FirstFault(string output, string fallback)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(text => text.Length > 0);
        return line ?? fallback;
    }
}
