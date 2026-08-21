namespace AmneziaGeo.Windows.App;

/// <summary>
/// One adapter's own DNS settings as they stood before a redirect. An empty list means that family took its
/// servers from DHCP: putting it back hands the adapter to automatic instead of pinning it to the addresses
/// it happened to hold, which would outlive the network they came from.
/// </summary>
internal sealed record SavedDns(string[] V4, string[] V6);

/// <summary>
/// A recorded adapter: its GUID, which stays with the adapter across reboots, or the interface index alone
/// for state written before the GUID was recorded - an index Windows hands to a different adapter later.
/// </summary>
internal sealed record DnsStateEntry(string? Guid, uint? Index, SavedDns Saved, bool Legacy);

/// <summary>
/// A persisted redirect: the adapters' own settings plus the servers the redirect pointed at.
/// </summary>
internal sealed record DnsRedirectState(IReadOnlyList<DnsStateEntry> Entries, string[] RedirectTargets);

/// <summary>
/// Reads and writes the per-tunnel DNS-redirect state file.
/// </summary>
internal static class DnsStateFile
{
    /// <summary>
    /// Writes the adapters' own settings and the servers the redirect points at.
    /// </summary>
    public static void Write(string path, IReadOnlyDictionary<string, SavedDns> saved, IReadOnlyList<string> redirectTargets)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(path, Format(saved, redirectTargets));
    }

    /// <summary>
    /// Reads a state file; a file that is not there is an empty state.
    /// </summary>
    public static DnsRedirectState Read(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllLines(path)) : new DnsRedirectState([], []);
    }

    /// <summary>
    /// Renders the state: a header with the redirect targets, then one line per adapter.
    /// </summary>
    public static IReadOnlyList<string> Format(IReadOnlyDictionary<string, SavedDns> saved, IReadOnlyList<string> redirectTargets)
    {
        var lines = new List<string> { $"{RedirectKey}={string.Join(",", redirectTargets)}" };
        foreach (var (guid, dns) in saved)
        {
            lines.Add($"{guid}={string.Join(",", dns.V4)}{FamilySeparator}{string.Join(",", dns.V6)}");
        }

        return lines;
    }

    /// <summary>
    /// Parses the state. A numeric key is a file from before the GUID was recorded: it carries IPv4 servers
    /// with no note of whether the adapter set them itself or took them from DHCP.
    /// </summary>
    public static DnsRedirectState Parse(IEnumerable<string> lines)
    {
        var entries = new List<DnsStateEntry>();
        var targets = Array.Empty<string>();
        foreach (var line in lines)
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
                targets = Servers(value);
            }
            else if (IsAdapterGuid(key))
            {
                var families = value.Split(FamilySeparator);
                entries.Add(new DnsStateEntry(
                    key,
                    Index: null,
                    new SavedDns(Family(families, 0), Family(families, 1)),
                    Legacy: false));
            }
            else if (uint.TryParse(key, out var index))
            {
                entries.Add(new DnsStateEntry(
                    Guid: null,
                    index,
                    new SavedDns(Servers(value), []),
                    Legacy: true));
            }
        }

        return new DnsRedirectState(entries, targets);
    }

    /// <summary>
    /// Whether the value is an adapter GUID rather than an interface index.
    /// </summary>
    public static bool IsAdapterGuid(string? value)
    {
        return value is { Length: > 0 } && Guid.TryParse(value, out _);
    }

    private static string[] Family(string[] families, int family)
    {
        return family < families.Length ? Servers(families[family]) : [];
    }

    private static string[] Servers(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    // Header line recording the servers Apply redirected to, so a restore keeps the file only for our own
    // un-reverted redirect. Absent in files written before it was recorded.
    private const string RedirectKey = "@redirect";

    // Splits one adapter's IPv4 servers from its IPv6 ones.
    private const char FamilySeparator = '|';
}
