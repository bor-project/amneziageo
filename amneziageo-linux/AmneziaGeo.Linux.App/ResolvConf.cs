using System.Net;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Points the system resolver at the agent's DNS router and puts the previous one back.
/// </summary>
internal static class ResolvConf
{
    private const string Path = "/etc/resolv.conf";
    private const string StubPath = "/run/systemd/resolve/resolv.conf";
    private const string LinkMarker = "#amneziageo-link:";

    // A run that was killed cannot put the file back itself, so the copy waits in the library, which a reboot
    // leaves in place - the run directory of earlier agents does not.
    private const string RuntimeBackup = "/run/amneziageo-resolv.bak";
    private static readonly string Backup = System.IO.Path.Combine(AgentPaths.Root, "resolv.bak");

    /// <summary>
    /// The resolvers the machine uses on its own network.
    /// </summary>
    public static IReadOnlyList<IPAddress> CaptureUpstream()
    {
        var servers = ReadNameservers(Saved() ?? Path);
        return servers.Count > 0 ? servers : ReadNameservers(StubPath);
    }

    /// <summary>
    /// Sends every lookup to the given address, keeping the previous file for the restore.
    /// </summary>
    public static bool Apply(IPAddress listen, AgentLog log)
    {
        try
        {
            if (Saved() is null)
            {
                var link = new FileInfo(Path).LinkTarget;
                File.WriteAllText(Backup, link is not null ? LinkMarker + link : File.ReadAllText(Path));
            }

            File.Delete(Path);
            File.WriteAllText(Path, $"# added by amneziageo\nnameserver {listen}\noptions edns0\n");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("dns", "pointing the system resolver at the agent failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Puts the previous resolver file back; a leftover from a crashed run is restored the same way.
    /// </summary>
    public static void Restore(AgentLog log)
    {
        if (Saved() is not { } backup)
        {
            return;
        }

        try
        {
            var saved = File.ReadAllText(backup);
            File.Delete(Path);
            if (saved.StartsWith(LinkMarker, StringComparison.Ordinal))
            {
                File.CreateSymbolicLink(Path, saved[LinkMarker.Length..].Trim());
            }
            else
            {
                File.WriteAllText(Path, saved);
            }

            File.Delete(backup);
        }
        catch (Exception ex)
        {
            log.Error("dns", "restoring the system resolver failed", ex);
        }
    }

    // The copy left by this run or by one an older agent left in the run directory.
    private static string? Saved()
    {
        if (File.Exists(Backup))
        {
            return Backup;
        }

        return File.Exists(RuntimeBackup) ? RuntimeBackup : null;
    }

    // Reads the IPv4 nameservers of a resolver file, dropping loopback stubs.
    private static IReadOnlyList<IPAddress> ReadNameservers(string path)
    {
        var servers = new List<IPAddress>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed[10..].Trim();
                if (IPAddress.TryParse(value, out var address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address))
                {
                    servers.Add(address);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return servers;
    }
}
