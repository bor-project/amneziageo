using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Points network adapters' DNS at the loopback proxy and restores them, via WMI
/// (Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder). State is persisted per tunnel and keyed by the
/// adapter's GUID, so a crashed predecessor's redirect can be reverted from another process. An adapter that
/// took its servers from DHCP is handed back to automatic, never pinned to the addresses it happened to have.
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
    /// Sets every IP-enabled adapter's DNS to the proxy servers. The adapters' own settings are recorded
    /// first, so a crash between the record and the redirect still leaves something to put back.
    /// </summary>
    public void Apply(string name, IReadOnlyList<string> proxyServers)
    {
        _name = name;
        var saved = new Dictionary<string, SavedDns>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                if (AdapterGuid(adapter) is { } guid)
                {
                    saved[guid] = Capture(guid, proxyServers);
                }
            }
        }

        DnsStateFile.Write(TunnelPaths.DnsStateFile(name), saved, proxyServers);

        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                // An adapter whose settings were not recorded is left alone: a redirect there could not be
                // put back.
                if (AdapterGuid(adapter) is not { } guid || !saved.ContainsKey(guid))
                {
                    continue;
                }

                SetDns(adapter, proxyServers);
                // WMI SetDNSServerSearchOrder is IPv4-only and leaves the adapter's IPv6 DNS in place;
                // point IPv6 DNS at the proxy's ::1 too so every query reaches us.
                RedirectV6Dns(Convert.ToUInt32(adapter["InterfaceIndex"]));
            }
        }

        logger.LogDebug("every adapter now sends its name lookups to {Servers}; the settings they had before are saved and put back on disconnect", string.Join(",", proxyServers));
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
            logger.LogDebug(ex, "the system's cached name lookups could not be cleared; a site may keep using an address resolved before the tunnel came up");
        }
    }

    // dnsapi export ipconfig /flushdns calls; clears the system resolver cache in-process.
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern uint DnsFlushResolverCache();

    /// <summary>
    /// Restores the DNS settings this instance redirected. Keeps the state for a retry if a reset did not take.
    /// </summary>
    public void Restore()
    {
        var file = TunnelPaths.DnsStateFile(_name);
        try
        {
            if (RestoreFile(file) != RestoreOutcome.Done)
            {
                return;
            }

            TryDelete(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the adapters' own DNS settings could not be put back; the record is kept and retried, so lookups are not left pointing at this machine");
        }
    }

    /// <summary>
    /// Restores any DNS redirect persisted by a previous run (any tunnel), even from another process. A file
    /// whose adapters are still on our redirect is kept so a later call retries once the adapter is ready
    /// - this is the recovery for a redirect that outlived its proxy (dirty shutdown or reboot with no tunnel).
    /// <paramref name="abortIf"/> stands the cleanup down the moment a tunnel bring-up is requested, so a boot
    /// pass cannot revert a connect's live redirect out from under it.
    /// </summary>
    public void RestoreSaved(Func<bool>? abortIf = null, string? only = null)
    {
        var restored = false;
        foreach (var file in StateFiles(only))
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

            try
            {
                var outcome = RestoreFile(file);
                if (outcome == RestoreOutcome.Done)
                {
                    TryDelete(file);
                    restored = true;
                    continue;
                }

                if (outcome == RestoreOutcome.Pending)
                {
                    logger.LogWarning("one adapter's own DNS settings could not be put back yet; the record ({File}) is kept and tried again, so name lookups are not left pointing at this machine", Path.GetFileName(file));
                    continue;
                }

                // Nothing on our redirect, and an adapter the file names is gone. Kept while it could still come
                // back, dropped once it has had long enough - otherwise a renumbered adapter keeps the file, and
                // its retry, alive on every boot from here on.
                if (!Expired(file))
                {
                    logger.LogDebug("the DNS record {File} names an adapter Windows does not list yet; it is kept in case the adapter comes back", Path.GetFileName(file));
                    continue;
                }

                logger.LogInformation("the DNS record {File} names an adapter that is gone for good; it is discarded, nothing left to put back", Path.GetFileName(file));
                TryDelete(file);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "the DNS record {File} could not be read; it is left in place and retried, so the adapter's own settings are not lost", file);
            }
        }

        if (restored)
        {
            logger.LogDebug("the adapters' own DNS settings are back in place");
        }
    }

    // The records to revert: one tunnel's own, or every tunnel's when none is named.
    private static IEnumerable<string> StateFiles(string? only)
    {
        return only is { Length: > 0 } ? new[] { TunnelPaths.DnsStateFile(only) } : TunnelPaths.DnsStateFiles();
    }

    /// <summary>
    /// Standalone recovery for the uninstall custom action and the UI repair button: reverts any persisted
    /// redirect, then hands back to automatic every adapter still pointing DNS at a loopback proxy and every
    /// adapter pinned to servers only another adapter ever learned. Runs without the agent, so a dead or hung
    /// service can't strand the box without a resolver. Touches DNS only - not routes, profiles, or configs.
    /// </summary>
    public void RestoreAndHeal()
    {
        RestoreSaved();

        foreach (var adapter in Adapters())
        {
            using (adapter)
            {
                if (AdapterGuid(adapter) is not { } guid)
                {
                    continue;
                }

                var index = Convert.ToUInt32(adapter["InterfaceIndex"]);
                var current = adapter["DNSServerSearchOrder"] as string[] ?? [];
                if (current.Any(IsLoopback) || StaticServers(V4InterfacesKey, guid).Any(IsLoopback))
                {
                    // A persisted original was missing or the redirect outlived its state file: hand this adapter
                    // back to automatic (DHCP) so a dead loopback proxy stops swallowing every query.
                    SetDns(adapter, []);
                    logger.LogInformation("adapter {Guid} was still sending name lookups to this machine; it is back on automatic DNS", guid);
                }
                else if (Misplaced(guid))
                {
                    SetDns(adapter, []);
                    logger.LogInformation("adapter {Guid} was pinned to DNS servers only another adapter ever learned; it is back on automatic DNS", guid);
                }

                if (StaticServers(V6InterfacesKey, guid).Any(IsLoopback))
                {
                    ResetV6Dns(index);
                }
            }
        }

        FlushCache();
    }

    // What a restore pass leaves behind.
    private enum RestoreOutcome
    {
        Done,    // every recorded adapter is present and off our redirect
        Pending, // an adapter is still on our redirect
        Absent,  // nothing to revert, but an adapter is not enumerable
    }

    // Reverts one state file and reports what is left. Only an adapter that still carries our redirect is
    // written to: an index or a GUID that now belongs to another adapter must never be handed settings that
    // were taken from this one.
    private RestoreOutcome RestoreFile(string path)
    {
        var state = DnsStateFile.Read(path);
        var absent = false;
        var pending = false;
        foreach (var entry in state.Entries)
        {
            switch (RestoreEntry(entry, state.RedirectTargets))
            {
                case RestoreOutcome.Pending:
                    pending = true;
                    break;
                case RestoreOutcome.Absent:
                    absent = true;
                    break;
            }
        }

        if (pending)
        {
            return RestoreOutcome.Pending;
        }

        return absent ? RestoreOutcome.Absent : RestoreOutcome.Done;
    }

    private RestoreOutcome RestoreEntry(DnsStateEntry entry, string[] targets)
    {
        var adapter = FindAdapter(entry);
        if (adapter is null)
        {
            return RestoreOutcome.Absent;
        }

        using (adapter)
        {
            if ((AdapterGuid(adapter) ?? entry.Guid) is not { } guid)
            {
                return RestoreOutcome.Absent;
            }

            var index = Convert.ToUInt32(adapter["InterfaceIndex"]);
            var saved = Resolve(entry, guid);
            if (IsOurs(adapter, guid, targets))
            {
                SetDns(adapter, saved.V4);
            }

            if (StaticServers(V6InterfacesKey, guid).Any(IsLoopback))
            {
                SetV6Dns(index, saved.V6);
            }

            return Probe(index, guid, targets);
        }
    }

    // The adapter this entry was recorded for: by GUID, which survives a reboot and a renumbering, and by
    // interface index only for state written before the GUID was recorded.
    private static ManagementObject? FindAdapter(DnsStateEntry entry)
    {
        var where = entry.Guid is { } guid
            ? $"SettingID = '{guid}'"
            : $"InterfaceIndex = {entry.Index}";
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE {where}");
        foreach (ManagementObject adapter in searcher.Get())
        {
            return adapter;
        }

        return null;
    }

    // Time an adapter has to reappear before its state is treated as leftover. Measured from the write, so a
    // redirect applied by a live connect restarts the clock.
    private static readonly TimeSpan StateLifetime = TimeSpan.FromHours(24);

    private static bool Expired(string file)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StateLifetime;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static RestoreOutcome Probe(uint index, string guid, string[] targets)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {index}");
        foreach (ManagementObject adapter in searcher.Get())
        {
            using (adapter)
            {
                return IsOurs(adapter, guid, targets) ? RestoreOutcome.Pending : RestoreOutcome.Done;
            }
        }

        return RestoreOutcome.Absent;
    }

    // Whether the adapter still carries the redirect. The live list answers for an adapter that is up; the
    // static entry answers for one that is down, and that one has to be put back too.
    private static bool IsOurs(ManagementObject adapter, string guid, string[] targets)
    {
        var dns = adapter["DNSServerSearchOrder"] as string[] ?? [];
        return IsStillOurs(dns, targets) || IsStillOurs(StaticServers(V4InterfacesKey, guid), targets);
    }

    // The adapter still lists a server we set. Legacy state with no recorded target falls back to any loopback,
    // so a third-party loopback resolver a user re-asserts is not mistaken for our un-reverted redirect.
    private static bool IsStillOurs(string[] dns, string[] targets)
    {
        return targets.Length > 0
            ? dns.Any(s => targets.Contains(s, StringComparer.OrdinalIgnoreCase))
            : dns.Any(IsLoopback);
    }

    // What to put back on an adapter. State written before the servers' origin was recorded says nothing about
    // where they came from: servers the adapter learned over DHCP are handed back as automatic, because writing
    // them statically outlives the network they belong to.
    private static SavedDns Resolve(DnsStateEntry entry, string guid)
    {
        if (!entry.Legacy)
        {
            return entry.Saved;
        }

        var learned = DhcpServers(V4InterfacesKey, guid);
        var fromDhcp = entry.Saved.V4.Any(s => learned.Contains(s, StringComparer.OrdinalIgnoreCase));
        return fromDhcp ? entry.Saved with { V4 = [] } : entry.Saved;
    }

    // The adapter's own DNS settings, read where Windows keeps them: an empty static list means the adapter
    // takes its servers from DHCP. Our own targets and loopback are dropped, so a dirty predecessor's redirect
    // cannot be recorded as an original and handed back later.
    private static SavedDns Capture(string guid, IReadOnlyList<string> proxyServers)
    {
        return new SavedDns(
            Own(StaticServers(V4InterfacesKey, guid), proxyServers),
            Own(StaticServers(V6InterfacesKey, guid), proxyServers));
    }

    private static string[] Own(string[] servers, IReadOnlyList<string> proxyServers)
    {
        return servers.Where(s => !proxyServers.Contains(s) && !IsLoopback(s)).ToArray();
    }

    // A static list this adapter never learned itself while another adapter did: the signature of an older
    // build putting saved servers back on the wrong interface. An adapter pinned to its own DHCP servers, or to
    // anything no adapter here learned, is a user's own choice and is left alone.
    private static bool Misplaced(string guid)
    {
        var pinned = StaticServers(V4InterfacesKey, guid);
        if (pinned.Length == 0)
        {
            return false;
        }

        var own = DhcpServers(V4InterfacesKey, guid);
        if (pinned.Any(s => own.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var elsewhere = LearnedElsewhere(guid);
        return pinned.All(s => elsewhere.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    // Every DNS server some other adapter on this machine learned over DHCP.
    private static string[] LearnedElsewhere(string guid)
    {
        var result = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(V4InterfacesKey);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                if (!string.Equals(name, guid, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddRange(DhcpServers(V4InterfacesKey, name));
                }
            }
        }
        catch (Exception)
        {
            // No read on the interface list: the heal falls back to the loopback rule alone.
        }

        return [.. result];
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

    // The adapter's GUID: it stays with the adapter across reboots, unlike the interface index, so state keyed
    // by it goes back on the adapter it was taken from.
    private static string? AdapterGuid(ManagementObject adapter)
    {
        var guid = adapter["SettingID"] as string;
        return DnsStateFile.IsAdapterGuid(guid) ? guid : null;
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

    // WMI cannot set IPv6 DNS; netsh can, by interface index. Best-effort, never throws.
    private static void RedirectV6Dns(uint index)
    {
        Netsh($"interface ipv6 set dnsservers name={index} static ::1 primary validate=no");
    }

    private static void ResetV6Dns(uint index)
    {
        Netsh($"interface ipv6 set dnsservers name={index} source=dhcp");
    }

    // Puts one adapter's IPv6 servers back; an empty list means it took them from DHCP.
    private static void SetV6Dns(uint index, IReadOnlyList<string> servers)
    {
        if (servers.Count == 0)
        {
            ResetV6Dns(index);
            return;
        }

        Netsh($"interface ipv6 set dnsservers name={index} static {servers[0]} primary validate=no");
        for (var i = 1; i < servers.Count; i++)
        {
            Netsh($"interface ipv6 add dnsservers name={index} {servers[i]} index={i + 1} validate=no");
        }
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

    private static bool IsLoopback(string server)
    {
        return IPAddress.TryParse(server, out var ip) && IPAddress.IsLoopback(ip);
    }

    // Where Windows keeps each adapter's own DNS settings: NameServer is what was set statically, empty when
    // the adapter takes DNS from DHCP; DhcpNameServer is what the lease offered.
    private const string V4InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string V6InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    private static string[] StaticServers(string root, string guid)
    {
        return ReadServers(root, guid, "NameServer");
    }

    private static string[] DhcpServers(string root, string guid)
    {
        return ReadServers(root, guid, "DhcpNameServer");
    }

    private static string[] ReadServers(string root, string guid, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{guid}");
            return (key?.GetValue(value) as string)?.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        }
        catch (Exception)
        {
            // An adapter without a settings key has nothing of its own to put back.
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
