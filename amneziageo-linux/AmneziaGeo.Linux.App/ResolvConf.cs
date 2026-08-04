using System.Net;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Points the system resolver at the agent's DNS router and puts the previous one back.
/// </summary>
internal static class ResolvConf
{
    private const string Path = "/etc/resolv.conf";
    private const string Backup = "/run/amneziageo-resolv.bak";
    private const string StubPath = "/run/systemd/resolve/resolv.conf";
    private const string LinkMarker = "#amneziageo-link:";

    /// <summary>
    /// The resolvers the machine uses on its own network.
    /// </summary>
    public static IReadOnlyList<IPAddress> CaptureUpstream()
    {
        var servers = ReadNameservers(File.Exists(Backup) ? Backup : Path);
        return servers.Count > 0 ? servers : ReadNameservers(StubPath);
    }

    /// <summary>
    /// Sends every lookup to the given address, keeping the previous file for the restore.
    /// </summary>
    public static bool Apply(IPAddress listen, AgentLog log)
    {
        try
        {
            if (!File.Exists(Backup))
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
        if (!File.Exists(Backup))
        {
            return;
        }

        try
        {
            var saved = File.ReadAllText(Backup);
            File.Delete(Path);
            if (saved.StartsWith(LinkMarker, StringComparison.Ordinal))
            {
                File.CreateSymbolicLink(Path, saved[LinkMarker.Length..].Trim());
            }
            else
            {
                File.WriteAllText(Path, saved);
            }

            File.Delete(Backup);
        }
        catch (Exception ex)
        {
            log.Error("dns", "restoring the system resolver failed", ex);
        }
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
