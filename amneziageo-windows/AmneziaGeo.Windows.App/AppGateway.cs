using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Per-application path of this machine. An adapter of its own takes what the programs open, every session is
/// terminated on it and opened again by this process, and the rules decide each one knowing which program it
/// belongs to. A routing table cannot tell one program from another, which is why the sessions are taken here
/// instead of being routed by address for the whole machine. The server itself and the local networks are kept
/// out of the adapter, or the tunnel would look for itself through the path it carries.
/// </summary>
internal sealed class AppGateway : IDisposable
{
    private const string AdapterPrefix = "AmneziaGeo Apps";
    private const int FirstOctet = 73;
    private const int LastOctet = 99;
    private const string GatewayExe = "gateway.exe";
    private const int StartWaitMs = 4000;

    private readonly Process _process;
    private readonly LocalProxyServer _server;
    private readonly ILogger _logger;
    private bool _disposed;

    private AppGateway(Process process, LocalProxyServer server, ILogger logger)
    {
        _process = process;
        _server = server;
        _logger = logger;
    }

    /// <summary>
    /// Whether this machine was asked for the per-application path. It stays off until the environment variable
    /// is set to one: the path takes every session of the machine onto an adapter of its own.
    /// </summary>
    public static bool Wanted() =>
        string.Equals(Environment.GetEnvironmentVariable("AMNEZIAGEO_APP_GATEWAY"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Raises the path, or null when the rules name no application, the gateway is missing, or it refuses to
    /// stand. Nothing of the machine changes in that case: the tunnel keeps deciding by address alone.
    /// </summary>
    public static AppGateway? TryStart(
        string tunnelAdapter,
        IReadOnlyList<string> apps,
        Func<IReadOnlyCollection<uint>, HashSet<uint>>? named,
        GeoIpRanges proxy,
        GeoIpRanges direct,
        GeoIpRanges block,
        IReadOnlyList<string> kept,
        int mtu,
        ILogger logger)
    {
        if (apps.Count == 0 || named is null)
        {
            return null;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, GatewayExe);
        if (!File.Exists(exe))
        {
            logger.LogWarning("{Exe} is not next to the agent, so the applications keep being routed by address", GatewayExe);
            return null;
        }

        var port = FreePort();
        if (port == 0)
        {
            logger.LogWarning("no port was free for the per-application path");
            return null;
        }

        var adapter = $"{AdapterPrefix} {tunnelAdapter}";
        if (Address(adapter) is not { } address)
        {
            logger.LogWarning("no address range was free for {Adapter}, so the applications keep being routed by address", adapter);
            return null;
        }

        var outbound = new GatewayProxyOutbound(named, proxy, direct, block,
            () => Index(tunnelAdapter), () => Physical(tunnelAdapter), logger);
        var server = new LocalProxyServer(outbound, line => logger.LogDebug("apps: {Line}", line), outbound);
        var options = new LocalProxyOptions { Enabled = true, SocksPort = port, HttpPort = port, AllowAnonymous = true };
        if (!server.Apply(options) || !server.Running)
        {
            logger.LogWarning("the per-application proxy did not bind: {Error}", server.Error);
            server.Dispose();
            return null;
        }

        var process = Launch(exe, adapter, address, port, kept, mtu, logger);
        if (process is null)
        {
            server.Dispose();
            return null;
        }

        if (process.WaitForExit(StartWaitMs))
        {
            logger.LogWarning("the per-application gateway stopped at once, so the applications keep being routed by address");
            server.Dispose();
            return null;
        }

        logger.LogInformation("{Count} application(s) are carried by their own adapter, and every session on it is "
            + "decided knowing which program opened it", apps.Count);
        return new AppGateway(process, server, logger);
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
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(StartWaitMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the per-application gateway did not stop cleanly");
        }

        _process.Dispose();
        _server.Dispose();
    }

    // Starts the adapter that takes the sessions: everything enters it except what is kept out by name.
    private static Process? Launch(string exe, string adapter, string address, int port, IReadOnlyList<string> kept, int mtu, ILogger logger)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("--name");
            info.ArgumentList.Add(adapter);
            info.ArgumentList.Add("--address");
            info.ArgumentList.Add(address);
            info.ArgumentList.Add("--routes");
            info.ArgumentList.Add(Taken());
            if (kept.Count > 0)
            {
                info.ArgumentList.Add("--except");
                info.ArgumentList.Add(string.Join(",", kept));
            }

            info.ArgumentList.Add("--named");
            info.ArgumentList.Add("--proxy");
            info.ArgumentList.Add("127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--mtu");
            info.ArgumentList.Add(mtu.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            process.OutputDataReceived += (_, line) => Note(logger, line.Data, warn: false);
            process.ErrorDataReceived += (_, line) => Note(logger, line.Data, warn: true);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the per-application gateway did not start");
            return null;
        }
    }

    private static void Note(ILogger logger, string? line, bool warn)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (warn)
        {
            logger.LogWarning("apps gateway: {Line}", line);
            return;
        }

        logger.LogDebug("apps gateway: {Line}", line);
    }

    // What the adapter takes. Everything by default; a narrower set is what a first run on a machine is checked
    // with, before the whole of its traffic is handed over.
    private static string Taken()
    {
        var wanted = Environment.GetEnvironmentVariable("AMNEZIAGEO_APP_GATEWAY_ROUTES");
        return string.IsNullOrWhiteSpace(wanted) ? "0.0.0.0/0" : wanted.Trim();
    }

    // The address the adapter carries, or null when every range is held.
    internal static string? Range(HashSet<int> taken)
    {
        for (var octet = FirstOctet; octet <= LastOctet; octet++)
        {
            if (!taken.Contains(octet))
            {
                return $"172.31.{octet}.1/24";
            }
        }

        return null;
    }

    // The 172.31 range no other adapter of this machine holds.
    private static string? Address(string adapter) => Range(Held(adapter));

    // Third octets of 172.31 the other adapters hold.
    private static HashSet<int> Held(string adapter)
    {
        var taken = new HashSet<int>();
        foreach (var item in NetworkAdapters.All())
        {
            if (string.Equals(item.Name, adapter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var unicast in item.GetIPProperties().UnicastAddresses)
                {
                    var bytes = unicast.Address.GetAddressBytes();
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 172 && bytes[1] == 31)
                    {
                        taken.Add(bytes[2]);
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return taken;
    }

    // A port nothing else holds; the listener takes it right after.
    private static int FreePort()
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    // Index of the adapter a name belongs to, or zero.
    private static uint Index(string adapter)
    {
        foreach (var item in NetworkAdapters.All())
        {
            if (!string.Equals(item.Name, adapter, StringComparison.OrdinalIgnoreCase)
                || item.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                return (uint)item.GetIPProperties().GetIPv4Properties().Index;
            }
            catch (NetworkInformationException)
            {
                return 0;
            }
        }

        return 0;
    }

    // Index of the link this machine reaches the world by, leaving out the tunnel and the adapter of this path.
    private static uint Physical(string tunnelAdapter)
    {
        foreach (var item in NetworkAdapters.All())
        {
            // Every adapter of ours is skipped, not just this path's: the access point raises one of its own with
            // a gateway on it, and a session sent there would be taken again instead of leaving the machine.
            if (item.OperationalStatus != OperationalStatus.Up
                || item.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || item.Name.StartsWith("AmneziaGeo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, tunnelAdapter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var properties = item.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                return (uint)properties.GetIPv4Properties().Index;
            }
            catch (NetworkInformationException)
            {
            }
        }

        return 0;
    }
}
