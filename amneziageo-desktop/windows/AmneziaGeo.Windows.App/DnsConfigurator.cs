using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Points network adapters' DNS at the loopback proxy and restores them, via WMI
/// (Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder). State is persisted per tunnel so a
/// crashed predecessor's redirect can be reverted from another process.
/// </summary>
internal sealed class DnsConfigurator(ILogger<DnsConfigurator> logger)
{
    private string _name = string.Empty;

    /// <summary>
    /// Reads the resolvers the system currently uses, so the proxy can forward non-geo queries upstream.
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
    /// Reads the connection-specific DNS suffixes the system advertises, so the proxy treats names under
    /// them as local and resolves them via the LAN resolver.
    /// </summary>
    public IReadOnlyList<string> CaptureLocalDnsSuffixes()
    {
        var suffixes = new List<string>();

        void Add(string? raw)
        {
            var v = raw?.Trim().Trim('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(v) && v.Length > 1 && !suffixes.Contains(v))
            {
                suffixes.Add(v);
            }
        }

        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                Add(adapter["DNSDomain"] as string);
                foreach (var s in adapter["DNSDomainSuffixSearchOrder"] as string[] ?? [])
                {
                    Add(s);
                }
            }
        }

        return suffixes;
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
                // Exclude our proxy targets and loopback so a dirty predecessor cannot be "restored".
                saved[index] = current.Where(s => !proxyServers.Contains(s) && !IsLoopback(s)).ToArray();
                SetDns(adapter, proxyServers);
                // WMI SetDNSServerSearchOrder is IPv4-only and leaves the adapter's IPv6 DNS in place;
                // point IPv6 DNS at the proxy's ::1 too so every query reaches us.
                RedirectV6Dns(index);
            }
        }

        WriteState(TunnelPaths.DnsStateFile(name), saved);
        logger.LogDebug("dns redirect applied via WMI -> {Servers}", string.Join(",", proxyServers));
    }

    /// <summary>
    /// Sets a single adapter's DNS by interface index. Empty list resets the adapter to automatic (DHCP).
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
    /// Clears the OS DNS resolver cache (the same call ipconfig /flushdns makes). Run after the redirect
    /// is applied and on teardown.
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

    // dnsapi export ipconfig /flushdns calls; clears the system resolver cache in-process.
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
            // Hand IPv6 DNS back to automatic; it was redirected to ::1.
            ResetV6Dns(index);
        }
    }

    // WMI cannot set IPv6 DNS; netsh can, by interface index. Best-effort, never throws.
    private static void RedirectV6Dns(uint index)
    {
        Netsh($"interface ipv6 set dnsservers name={index} static ::1 primary validate=no");
    }

    private static void ResetV6Dns(uint index)
    {
        Netsh($"interface ipv6 set dnsservers name={index} source=dhcp");
    }

    private static void Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(4000);
        }
        catch (Exception)
        {
            // netsh missing or an adapter rejecting the change must not abort the apply/restore.
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
