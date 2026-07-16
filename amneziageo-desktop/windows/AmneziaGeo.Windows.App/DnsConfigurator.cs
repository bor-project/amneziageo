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
    /// Reads every adapter's resolvers into a deduped pool (gateway adapter's first), so the proxy can race
    /// non-geo queries across all providers - a multi-WAN box where one provider censors a name is answered
    /// by another.
    /// </summary>
    public IReadOnlyList<string> CaptureUpstream()
    {
        var gateway = new List<string>();
        var others = new List<string>();
        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                var dns = (adapter["DNSServerSearchOrder"] as string[] ?? []).Where(s => !IsLoopback(s)).ToArray();
                if (dns.Length == 0)
                {
                    continue;
                }

                var target = adapter["DefaultIPGateway"] is string[] { Length: > 0 } ? gateway : others;
                target.AddRange(dns);
            }
        }

        var pool = new List<string>();
        foreach (var server in gateway.Concat(others))
        {
            if (!pool.Contains(server))
            {
                pool.Add(server);
            }
        }

        return pool;
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

        WriteState(TunnelPaths.DnsStateFile(name), saved, proxyServers);
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
    /// Restores the DNS servers this instance redirected. Keeps the state for a retry if a reset did not take.
    /// </summary>
    public void Restore()
    {
        var file = TunnelPaths.DnsStateFile(_name);
        try
        {
            var state = ReadState(file);
            RestoreState(state.Originals);
            if (!FullyRestored(state))
            {
                return;
            }

            TryDelete(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "dns restore failed; keeping state for retry");
        }
    }

    /// <summary>
    /// Restores any DNS redirect persisted by a previous run (any tunnel), even from another process. A file
    /// whose adapters are still on our redirect is kept so a later call retries once the adapter is ready
    /// - this is the recovery for a redirect that outlived its proxy (dirty shutdown or reboot with no tunnel).
    /// <paramref name="abortIf"/> stands the cleanup down the moment a tunnel bring-up is requested, so a boot
    /// pass cannot revert a connect's live redirect out from under it.
    /// </summary>
    public void RestoreSaved(Func<bool>? abortIf = null)
    {
        var restored = false;
        foreach (var file in TunnelPaths.DnsStateFiles())
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

            try
            {
                var state = ReadState(file);
                RestoreState(state.Originals);
                if (!FullyRestored(state))
                {
                    logger.LogWarning("dns restore incomplete; keeping {File} for retry", Path.GetFileName(file));
                    continue;
                }

                TryDelete(file);
                restored = true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "dns restore from {File} failed", file);
            }
        }

        if (restored)
        {
            logger.LogDebug("dns redirect restored from persisted state");
        }
    }

    // Delete only when every recorded adapter is present AND no longer on our redirect. A not-ready adapter
    // (not yet enumerable at boot, or renumbered) or one still on our redirect keeps the file for a later retry.
    private static bool FullyRestored(DnsState state)
    {
        return state.Originals.Keys.All(index => Probe(index, state.RedirectTargets) == AdapterDns.Clean);
    }

    // Per-adapter DNS after a restore attempt.
    private enum AdapterDns
    {
        NotReady, // adapter row absent / not enumerable yet - cannot confirm the restore took
        Ours,     // still on a server we set - the redirect has not been reverted
        Clean,    // present and no longer on our redirect
    }

    private static AdapterDns Probe(uint index, string[] targets)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {index}");
        foreach (ManagementObject adapter in searcher.Get())
        {
            using (adapter)
            {
                var dns = adapter["DNSServerSearchOrder"] as string[] ?? [];
                return IsStillOurs(dns, targets) ? AdapterDns.Ours : AdapterDns.Clean;
            }
        }

        return AdapterDns.NotReady;
    }

    // The adapter still lists a server we set. Legacy state with no recorded target falls back to any loopback,
    // so a third-party loopback resolver a user re-asserts is not mistaken for our un-reverted redirect.
    private static bool IsStillOurs(string[] dns, string[] targets)
    {
        return targets.Length > 0
            ? dns.Any(s => targets.Contains(s, StringComparer.OrdinalIgnoreCase))
            : dns.Any(IsLoopback);
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

    private static void WriteState(string path, Dictionary<uint, string[]> originals, IReadOnlyList<string> redirectTargets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { $"{RedirectKey}={string.Join(",", redirectTargets)}" };
        foreach (var (index, servers) in originals)
        {
            lines.Add($"{index}={string.Join(",", servers)}");
        }

        File.WriteAllLines(path, lines);
    }

    private static DnsState ReadState(string path)
    {
        var originals = new Dictionary<uint, string[]>();
        var targets = Array.Empty<string>();
        if (!File.Exists(path))
        {
            return new DnsState(originals, targets);
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (key == RedirectKey)
            {
                targets = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            else if (uint.TryParse(key, out var index))
            {
                originals[index] = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return new DnsState(originals, targets);
    }

    // Header line recording the servers Apply redirected to, so a restore keeps the file only for our own
    // un-reverted redirect. Absent in pre-existing (legacy) state files.
    private const string RedirectKey = "@redirect";

    // Persisted redirect: per-adapter prior servers plus the loopback targets Apply set.
    private sealed record DnsState(Dictionary<uint, string[]> Originals, string[] RedirectTargets);

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
