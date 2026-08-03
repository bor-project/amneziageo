using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Geo;
using AmneziaGeo.Linux.Engine;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Brings the amneziawg-go interface up and down over UAPI and iproute2.
/// </summary>
internal sealed class TunnelController : IDisposable
{
    private const string TunDevice = "/dev/net/tun";
    private const int DefaultMtu = 1420;

    private readonly string _enginePath;
    private readonly string _iface;
    private readonly AgentLog _log;
    private AwgDaemon? _daemon;
    private string? _pinnedEndpoint;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelController(string enginePath, string interfaceName, AgentLog log)
    {
        _enginePath = enginePath;
        _iface = interfaceName;
        _log = log;
    }

    /// <summary>
    /// Whether the tunnel interface is up.
    /// </summary>
    public bool Running => _daemon is { Running: true };

    /// <summary>
    /// Brings the tunnel up from a wg-quick config; returns null on success or the reason it was refused.
    /// </summary>
    public async Task<string?> UpAsync(string configText, CancellationToken ct)
    {
        var blocker = Preflight();
        if (blocker is not null)
        {
            _log.Warn("tunnel", $"connect refused: {blocker}");
            return blocker;
        }

        var (config, endpointIp) = await ResolveEndpointAsync(configText, ct).ConfigureAwait(false);
        var uapi = WgQuickToUapi.Convert(config);
        if (uapi is null)
        {
            return "the configuration carries no usable [Interface] PrivateKey";
        }

        await DownAsync(ct).ConfigureAwait(false);

        var daemon = new AwgDaemon(_enginePath, _iface);
        try
        {
            daemon.Start();
            if (!await WaitForSocketAsync(daemon, ct).ConfigureAwait(false))
            {
                daemon.Dispose();
                return $"amneziawg-go did not open {daemon.SocketPath}; its output is on the agent console";
            }

            await daemon.ConfigureAsync(uapi, ct).ConfigureAwait(false);
            _daemon = daemon;
            _log.Info("tunnel", $"{_iface} configured, endpoint {endpointIp ?? "(none)"}");
        }
        catch (Exception ex)
        {
            _log.Error("tunnel", "engine start failed", ex);
            daemon.Dispose();
            return $"engine start failed: {ex.Message}";
        }

        var failure = await ApplyNetworkAsync(config, endpointIp, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            await DownAsync(ct).ConfigureAwait(false);
            return failure;
        }

        return null;
    }

    /// <summary>
    /// Tears the tunnel down; the interface goes with the daemon process.
    /// </summary>
    public async Task DownAsync(CancellationToken ct = default)
    {
        if (_pinnedEndpoint is { } pinned)
        {
            _pinnedEndpoint = null;
            await RunAsync("ip", ct, "route", "del", pinned).ConfigureAwait(false);
        }

        if (_daemon is { } daemon)
        {
            _daemon = null;
            daemon.Dispose();
            _log.Info("tunnel", $"{_iface} down");
        }
    }

    // Refuses the connect with an actionable reason when the host cannot carry a tunnel.
    private string? Preflight()
    {
        if (!File.Exists(_enginePath))
        {
            return $"the amneziawg-go binary is missing at {_enginePath}; build it with amneziageo-linux/tools/build-engine-linux.sh and rebuild the agent";
        }

        if (geteuid() != 0)
        {
            return "creating the tunnel interface needs root; start the agent from \"Debug Linux (agent)\", the \"Run Linux agent (sudo)\" task, or with: sudo dotnet AmneziaGeo.Linux.App.dll";
        }

        if (!File.Exists(TunDevice))
        {
            return $"{TunDevice} is missing; load the module with: sudo modprobe tun";
        }

        return null;
    }

    // The engine does not resolve names, so a hostname endpoint is rewritten to its address.
    private static async Task<(string Config, string? EndpointIp)> ResolveEndpointAsync(string config, CancellationToken ct)
    {
        var endpoint = WgConfigEditor.GetEndpoint(config);
        var colon = endpoint?.LastIndexOf(':') ?? -1;
        if (endpoint is null || colon <= 0)
        {
            return (config, null);
        }

        var host = endpoint[..colon].Trim('[', ']');
        var port = endpoint[(colon + 1)..];
        if (IPAddress.TryParse(host, out var literal))
        {
            return (config, literal.ToString());
        }

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var resolved = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) ?? Array.Find(addresses, _ => true);
        return resolved is null
            ? (config, null)
            : (WgConfigEditor.SetEndpoint(config, $"{resolved}:{port}"), resolved.ToString());
    }

    // Waits for the daemon to publish its control socket.
    private static async Task<bool> WaitForSocketAsync(AwgDaemon daemon, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(daemon.SocketPath))
            {
                return true;
            }

            if (!daemon.Running)
            {
                return false;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    // Addresses, MTU, and the routes the peer AllowedIPs ask for.
    private async Task<string?> ApplyNetworkAsync(string config, string? endpointIp, CancellationToken ct)
    {
        foreach (var address in WgConfigEditor.GetAddresses(config))
        {
            var family = address.Contains(':', StringComparison.Ordinal) ? "-6" : "-4";
            var added = await RunAsync("ip", ct, family, "address", "add", address, "dev", _iface).ConfigureAwait(false);
            if (added.ExitCode != 0)
            {
                return $"ip address add {address} failed: {added.Output}";
            }
        }

        var mtu = WgConfigEditor.GetMtu(config);
        var up = await RunAsync("ip", ct, "link", "set", "dev", _iface, "mtu", (mtu > 0 ? mtu : DefaultMtu).ToString(CultureInfo.InvariantCulture), "up").ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            return $"ip link set up failed: {up.Output}";
        }

        if (endpointIp is not null)
        {
            await PinEndpointAsync(endpointIp, ct).ConfigureAwait(false);
        }

        foreach (var allowed in WgConfigEditor.GetAllowedIps(config))
        {
            foreach (var route in ExpandRoute(allowed))
            {
                var added = await RunAsync("ip", ct, "route", "replace", route, "dev", _iface).ConfigureAwait(false);
                if (added.ExitCode == 0)
                {
                    _log.Route($"{route} dev {_iface}");
                }
                else
                {
                    _log.Warn("tunnel", $"ip route add {route} failed: {added.Output}");
                }
            }
        }

        return null;
    }

    // A default route is laid as two halves so it outranks the physical one without replacing it.
    private static IEnumerable<string> ExpandRoute(string cidr)
    {
        return cidr switch
        {
            "0.0.0.0/0" => ["0.0.0.0/1", "128.0.0.0/1"],
            "::/0" => ["::/1", "8000::/1"],
            _ => [cidr],
        };
    }

    // Keeps the peer reachable off-tunnel while a default route is in place.
    private async Task PinEndpointAsync(string endpointIp, CancellationToken ct)
    {
        var lookup = await RunAsync("ip", ct, "route", "get", endpointIp).ConfigureAwait(false);
        if (lookup.ExitCode != 0)
        {
            return;
        }

        var tokens = lookup.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var via = Read(tokens, "via");
        var dev = Read(tokens, "dev");
        if (via is null || dev is null || dev == _iface)
        {
            return;
        }

        var pinned = await RunAsync("ip", ct, "route", "add", endpointIp, "via", via, "dev", dev).ConfigureAwait(false);
        if (pinned.ExitCode == 0)
        {
            _pinnedEndpoint = endpointIp;
            _log.Route($"{endpointIp} via {via} dev {dev}");
        }
    }

    // Reads the token following a keyword in `ip route get` output.
    private static string? Read(string[] tokens, string keyword)
    {
        var index = Array.IndexOf(tokens, keyword);
        return index >= 0 && index + 1 < tokens.Length ? tokens[index + 1] : null;
    }

    // Runs a command and returns its exit code with the merged output.
    private static async Task<(int ExitCode, string Output)> RunAsync(string file, CancellationToken ct, params string[] args)
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

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, $"could not start {file}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, (stdout + stderr).Trim());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DownAsync().GetAwaiter().GetResult();
    }
}
