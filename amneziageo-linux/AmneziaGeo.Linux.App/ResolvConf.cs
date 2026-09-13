using System.Net;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Points the system resolver at the agent's DNS router and puts the previous one back.
/// </summary>
internal static class ResolvConf
{
    private const string DefaultPath = "/etc/resolv.conf";
    private const string StubPath = "/run/systemd/resolve/resolv.conf";
    private const string LinkMarker = "#amneziageo-link:";

    // A run that was killed cannot put the file back itself, so the copy waits in the library, which a reboot
    // leaves in place - the run directory of earlier agents does not.
    private const string RuntimeBackup = "/run/amneziageo-resolv.bak";

    // The resolver file AMNEZIAGEO_RESOLV_CONF names instead of the machine's own.
    private static readonly string? CustomPath = Environment.GetEnvironmentVariable("AMNEZIAGEO_RESOLV_CONF") is { Length: > 0 } custom
        ? custom.Trim()
        : null;

    private static readonly string Path = CustomPath ?? DefaultPath;

    // The copy of a container's own file stays inside that container.
    private static readonly string Backup = ContainerHost.Detected && CustomPath is null
        ? RuntimeBackup
        : System.IO.Path.Combine(AgentPaths.Root, "resolv.bak");

    /// <summary>
    /// The resolvers the machine uses on its own network.
    /// </summary>
    public static IReadOnlyList<IPAddress> CaptureUpstream()
    {
        var servers = ReadNameservers(Saved() ?? Path);
        return servers.Count > 0 ? servers : ReadNameservers(StubPath);
    }

    /// <summary>
    /// Sends every lookup to the given address, keeping the previous file for the restore; systemd-resolved is
    /// pointed there through the tunnel link.
    /// </summary>
    public static async Task<bool> ApplyAsync(IPAddress listen, string iface, AgentLog log)
    {
        if (PointFile(listen, log) is not { } saved)
        {
            return false;
        }

        if (IsResolvedLink(saved))
        {
            await ResolvedLink.PointAsync(iface, listen, log).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Puts the previous resolver file back; a leftover from a crashed run is restored the same way.
    /// </summary>
    public static async Task RestoreAsync(string iface, AgentLog log)
    {
        if (Saved() is not { } backup)
        {
            return;
        }

        try
        {
            var saved = File.ReadAllText(backup);
            if (saved.StartsWith(LinkMarker, StringComparison.Ordinal))
            {
                File.Delete(Path);
                File.CreateSymbolicLink(Path, saved[LinkMarker.Length..].Trim());
            }
            else
            {
                Replace(saved, log);
            }

            File.Delete(backup);
            if (IsResolvedLink(saved))
            {
                await ResolvedLink.RevertAsync(iface, log).ConfigureAwait(false);
                await ResolvedLink.ReloadAsync(log).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.Error("dns", "restoring the system resolver failed", ex);
        }
    }

    // Writes the file that sends lookups to the address and returns the saved previous one, or null on failure.
    private static string? PointFile(IPAddress listen, AgentLog log)
    {
        try
        {
            if (Saved() is null)
            {
                var link = new FileInfo(Path).LinkTarget;
                File.WriteAllText(Backup, link is not null ? LinkMarker + link : File.ReadAllText(Path));
            }

            Replace($"# added by amneziageo\nnameserver {listen}\noptions edns0\n", log);
            return File.ReadAllText(Saved()!);
        }
        catch (Exception ex)
        {
            log.Error("dns", "pointing the system resolver at the agent failed", ex);
            return null;
        }
    }

    // Whether the saved file is a link into the run directory of systemd-resolved.
    private static bool IsResolvedLink(string saved) =>
        saved.StartsWith(LinkMarker, StringComparison.Ordinal) && saved.Contains("systemd/resolve", StringComparison.Ordinal);

    // The copy left by this run or by one an older agent left in the run directory.
    private static string? Saved()
    {
        if (File.Exists(Backup))
        {
            return Backup;
        }

        return File.Exists(RuntimeBackup) ? RuntimeBackup : null;
    }

    // Writes the file anew, in place when it is mounted in from outside and cannot be unlinked.
    private static void Replace(string text, AgentLog log)
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException ex)
        {
            log.Debug("dns", $"{Path} is rewritten in place: {ex.Message}");
        }

        File.WriteAllText(Path, text);
    }

    // Reads the IPv4 nameservers of a resolver file, dropping loopback stubs outside a container.
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
                    && (!IPAddress.IsLoopback(address) || (ContainerHost.Detected && !address.Equals(DnsRouter.Listen))))
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
