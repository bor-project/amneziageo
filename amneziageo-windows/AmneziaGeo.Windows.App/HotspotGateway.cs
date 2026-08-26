using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries the clients of the access point through this machine. Sharing forwards what they send over one
/// connection only - the one the point was raised over - and drops whatever that connection holds no route for.
/// This puts an adapter of its own in front of the clients: what they open is terminated on it and opened again as
/// a socket of this machine, which the routing table then carries exactly as it carries this machine's own
/// traffic. It stands only while the access point does.
/// </summary>
internal sealed class HotspotGateway(DnsProxy proxy, RouteManager routes, IProxyOutbound outbound, int mtu, Action<IPAddress> note, ILogger<HotspotGateway> logger) : IDisposable
{
    private const string AdapterName = "AmneziaGeo Gateway";
    // Address the adapter answers on.
    private const string AdapterAddress = "172.31.72.1/24";
    // Hop the adapter answers for, and the one its routes go through.
    private const string AdapterHop = "172.31.72.2";
    // Metric of the default route on the gateway. Every other way out of this machine is a better one, so nothing
    // of its own goes here; sharing has none of the others to choose from and takes this one.
    private const uint CarriedMetric = 9000;
    // Address sharing hands its clients by default; it is settable, so the machine is asked instead of assumed.
    private const string DefaultScope = "192.168.137.1";
    private const string ScopeKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters";
    private const string GatewayExe = "gateway.exe";
    private const int StopWaitMs = 5000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly HotspotNames _names = new();
    private HotspotProxy? _relay;
    private Process? _process;
    private IPAddress? _served;
    private uint? _carried;
    private bool _reported;

    /// <summary>
    /// Follows the access point up and down until the session ends.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Move();
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Lower();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Lower();
    }

    // Holds the gateway up for the whole session and puts it back if it fell, and serves the clients of the
    // point while one stands.
    private void Move()
    {
        try
        {
            if (_process is null || _process.HasExited)
            {
                Lower();
                Raise();
            }

            if (_process is null)
            {
                return;
            }

            Carry();
            var point = Point();
            if (point is null)
            {
                if (_served is not null)
                {
                    proxy.StopServingClients();
                    _served = null;
                }

                return;
            }

            if (_served is null || !_served.Equals(point))
            {
                proxy.StopServingClients();
                _served = proxy.ServeClients(point, _names) ? point : null;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the gateway of the access point could not be moved to what this machine now carries");
        }
    }

    private void Raise()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, GatewayExe);
        if (!File.Exists(exe))
        {
            if (!_reported)
            {
                _reported = true;
                logger.LogWarning("what the clients of the access point open keeps leaving past the rules of this machine: {Exe} is not installed", exe);
            }

            return;
        }

        var relay = new HotspotProxy(_names, outbound, logger, note);
        if (!relay.Start())
        {
            relay.Dispose();
            return;
        }

        _relay = relay;
        _process = Launch(exe, relay.Port);
        if (_process is null)
        {
            Lower();
            return;
        }

        logger.LogInformation("the gateway is up: the access point is shared over it, so what its clients open is opened again here and carried by the rules of this machine (proxy on port {Port})", relay.Port);
    }

    // Gives sharing a way out for everything, not just the addresses this machine hands its clients: a client that
    // dials an address it was never told - no name looked up, nothing to stand in for - reaches the gateway too and
    // goes out under the same rules. Held only while this machine has a way out of its own to prefer, so what goes
    // out of the gateway is never sent back into it.
    private void Carry()
    {
        if (!Uplinked())
        {
            Uncarry();
            return;
        }

        if (_carried is not null)
        {
            return;
        }

        var index = routes.FindInterfaceIndex(AdapterName);
        if (index is null || !IPAddress.TryParse(AdapterHop, out var hop))
        {
            return;
        }

        if (routes.AddCarriedDefault(index.Value, hop, CarriedMetric))
        {
            _carried = index;
        }
    }

    private void Uncarry()
    {
        if (_carried is not { } carried)
        {
            return;
        }

        _carried = null;
        routes.RemoveCarriedDefault(carried);
    }

    // Whether another adapter of this machine still holds a way out.
    private static bool Uplinked()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.Name == AdapterName)
            {
                continue;
            }

            foreach (var hop in nic.GetIPProperties().GatewayAddresses)
            {
                if (hop.Address.AddressFamily == AddressFamily.InterNetwork && !hop.Address.Equals(IPAddress.Any))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Lower()
    {
        proxy.StopServingClients();
        Uncarry();
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(StopWaitMs);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "the gateway of the access point did not come down cleanly");
            }

            process.Dispose();
        }

        _relay?.Dispose();
        _relay = null;
        _names.Clear();
        if (_served is not null)
        {
            _served = null;
            logger.LogInformation("the clients of the access point are no longer carried by this machine");
        }
    }

    private Process? Launch(string exe, int port)
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
            info.ArgumentList.Add(AdapterName);
            info.ArgumentList.Add("--address");
            info.ArgumentList.Add(AdapterAddress);
            info.ArgumentList.Add("--routes");
            info.ArgumentList.Add(HotspotNames.Prefix);
            info.ArgumentList.Add("--proxy");
            info.ArgumentList.Add(string.Concat("127.0.0.1:", port.ToString(CultureInfo.InvariantCulture)));
            info.ArgumentList.Add("--mtu");
            info.ArgumentList.Add(mtu.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            process.OutputDataReceived += (_, line) => Note(line.Data, warn: false);
            process.ErrorDataReceived += (_, line) => Note(line.Data, warn: true);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the gateway of the access point did not start; what its clients open keeps leaving past the rules of this machine");
            return null;
        }
    }

    private void Note(string? line, bool warn)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (warn)
        {
            logger.LogWarning("gateway: {Line}", line);
            return;
        }

        logger.LogDebug("gateway: {Line}", line);
    }

    // The address sharing answers its clients on, while an adapter of this machine carries it.
    private static IPAddress? Point()
    {
        if (!IPAddress.TryParse(Scope(), out var scope))
        {
            return null;
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.Equals(scope))
                {
                    return scope;
                }
            }
        }

        return null;
    }

    private static string Scope()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ScopeKey);
            return key?.GetValue("ScopeAddress") as string ?? DefaultScope;
        }
        catch (Exception)
        {
            return DefaultScope;
        }
    }
}
