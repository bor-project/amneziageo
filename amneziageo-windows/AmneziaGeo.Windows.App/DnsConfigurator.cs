using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Points network adapters' DNS at the loopback proxy and restores them, via WMI
/// (Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder) — the same mechanism
/// Set-DnsClientServerAddress uses, in-process (no process spawn). On this OS the pure-P/Invoke
/// SetInterfaceDnsSettings is a no-op and a bare registry write isn't honored without the WMI/CIM
/// notify, so WMI is the only reliable no-spawn option (needs BuiltInComInteropSupport).
/// The captured original resolvers are exposed so the proxy can forward non-geo queries to them,
/// preserving existing/corporate name resolution when running alongside another VPN. State is
/// persisted per tunnel so a crashed predecessor's redirect can be reverted from another process.
/// </summary>
internal sealed class DnsConfigurator(ILogger<DnsConfigurator> logger)
{
    private string _name = string.Empty;

    /// <summary>
    /// Reads the resolvers the system currently uses (the default-route adapter's, else the first
    /// adapter with DNS), so the proxy can forward non-geo queries upstream. Read-only.
    /// </summary>
    public IReadOnlyList<string> CaptureUpstream()
    {
        string[] gatewayDns = [];
        string[] firstDns = [];
        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                var dns = (adapter["DNSServerSearchOrder"] as string[] ?? []).Where(s => !IsLoopback(s)).ToArray();
                if (dns.Length == 0)
                {
                    continue;
                }

                if (firstDns.Length == 0)
                {
                    firstDns = dns;
                }

                if (adapter["DefaultIPGateway"] is string[] { Length: > 0 } && gatewayDns.Length == 0)
                {
                    gatewayDns = dns;
                }
            }
        }

        return gatewayDns.Length > 0 ? gatewayDns : firstDns;
    }

    /// <summary>
    /// Sets every IP-enabled adapter's DNS to the proxy servers, persisting the prior per-adapter
    /// servers so they can be restored (here or by a reconciler after a crash).
    /// </summary>
    public void Apply(string name, IReadOnlyList<string> proxyServers)
    {
        _name = name;
        var saved = new Dictionary<uint, string[]>();
        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                var index = Convert.ToUInt32(adapter["InterfaceIndex"]);
                var current = adapter["DNSServerSearchOrder"] as string[] ?? [];
                // Capture the baseline excluding our proxy targets and loopback so a dirty predecessor
                // cannot make us "restore" the proxy address itself.
                saved[index] = current.Where(s => !proxyServers.Contains(s) && !IsLoopback(s)).ToArray();
                SetDns(adapter, proxyServers);
            }
        }

        WriteState(TunnelPaths.DnsStateFile(name), saved);
        logger.LogDebug("dns redirect applied via WMI -> {Servers}", string.Join(",", proxyServers));
    }

    /// <summary>
    /// Sets a single adapter's DNS by interface index. Used for the tunnel adapter once it is up:
    /// giving it the proxy as its IPv4 resolver makes it answer instantly and stops Windows' dead
    /// fec0:: IPv6 resolvers (handed to a v4-only adapter) from stalling lookups. Empty list resets
    /// the adapter to automatic (DHCP).
    /// </summary>
    public void SetAdapter(uint interfaceIndex, IReadOnlyList<string> servers)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {interfaceIndex}");
        foreach (ManagementObject adapter in searcher.Get())
        {
            using (adapter)
            {
                SetDns(adapter, servers);
            }
        }
    }

    /// <summary>
    /// Clears the OS DNS resolver cache (the same call <c>ipconfig /flushdns</c> makes). Run right after
    /// the redirect to the loopback proxy is applied: without it, a domain resolved before connecting —
    /// e.g. a popular site like youtube.com already in the cache — is served from the cache and never
    /// reaches the proxy, so it is never matched and never routed into the tunnel until its TTL expires
    /// (split routing silently misses it while its uncached CDN subdomains do get tracked). Also run on
    /// teardown so proxy answers carrying tunnel-routed IPs do not linger. Best-effort; never throws.
    /// </summary>
    public void FlushCache()
    {
        try
        {
            DnsFlushResolverCache();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "dns cache flush failed");
        }
    }

    // ipconfig /flushdns calls this same dnsapi export; it clears the system resolver cache in-process
    // with no process spawn. Undocumented but stable since Windows XP.
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern uint DnsFlushResolverCache();

    /// <summary>
    /// Restores the DNS servers this instance redirected.
    /// </summary>
    public void Restore()
    {
        RestoreState(ReadState(TunnelPaths.DnsStateFile(_name)));
        TryDelete(TunnelPaths.DnsStateFile(_name));
    }

    /// <summary>
    /// Restores any DNS redirect persisted by a previous run (any tunnel), even from another process.
    /// </summary>
    public void RestoreSaved()
    {
        var any = false;
        foreach (var file in TunnelPaths.DnsStateFiles())
        {
            RestoreState(ReadState(file));
            TryDelete(file);
            any = true;
        }

        if (any)
        {
            logger.LogDebug("dns redirect restored from persisted state");
        }
    }

    private void RestoreState(Dictionary<uint, string[]> state)
    {
        foreach (var (index, original) in state)
        {
            // Empty originals => the adapter was on automatic (DHCP); resetting to none reverts it.
            SetAdapter(index, original);
        }
    }

    private static IEnumerable<ManagementObject> Adapters()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = true");
        foreach (ManagementObject adapter in searcher.Get())
        {
            yield return adapter;
        }
    }

    private static void SetDns(ManagementObject adapter, IReadOnlyList<string> servers)
    {
        try
        {
            using var inParams = adapter.GetMethodParameters("SetDNSServerSearchOrder");
            inParams["DNSServerSearchOrder"] = servers.Count > 0 ? servers.ToArray() : null;
            adapter.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
        }
        catch (ManagementException)
        {
            // A single adapter rejecting the change must not abort the whole apply/restore.
        }
    }

    private static bool IsLoopback(string server)
    {
        return IPAddress.TryParse(server, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static void WriteState(string path, Dictionary<uint, string[]> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string>();
        foreach (var (index, servers) in state)
        {
            lines.Add($"{index}={string.Join(",", servers)}");
        }

        File.WriteAllLines(path, lines);
    }

    private static Dictionary<uint, string[]> ReadState(string path)
    {
        var state = new Dictionary<uint, string[]>();
        if (!File.Exists(path))
        {
            return state;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator < 0 || !uint.TryParse(line[..separator], out var index))
            {
                continue;
            }

            state[index] = line[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        return state;
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
