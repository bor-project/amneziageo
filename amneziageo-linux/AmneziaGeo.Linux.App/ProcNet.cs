using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Reads the destinations the machine currently holds sockets to off the kernel's socket tables.
/// </summary>
internal sealed class ProcNet : ILiveDestinations
{
    private static readonly string[] Streams =
    [
        "/proc/net/tcp",
        "/proc/net/tcp6",
    ];

    private static readonly string[] Datagrams =
    [
        "/proc/net/udp",
        "/proc/net/udp6",
    ];

    private readonly bool _dialedOnly;

    /// <summary>
    /// ctor
    /// </summary>
    public ProcNet(bool dialedOnly = false)
    {
        _dialedOnly = dialedOnly;
    }

    /// <summary>
    /// Remote addresses of every current connection, host order; where only the dialed ones are asked for, a
    /// connection this machine took in is left out. The socket tables name no image here, so nothing is attributed
    /// to an app rule.
    /// </summary>
    public LiveDestinations Snapshot()
    {
        var peers = new HashSet<uint>();
        var streams = Streams.Select(Read).ToList();
        var accepting = _dialedOnly ? Accepting(streams) : null;
        foreach (var table in streams)
        {
            SocketTables.Peers(table, accepting, peers);
        }

        foreach (var table in Datagrams)
        {
            SocketTables.Peers(Read(table), null, peers);
        }

        return new LiveDestinations(peers, []);
    }

    // The ports the stream tables show connections taken in on.
    private static HashSet<int> Accepting(IReadOnlyList<string[]> tables)
    {
        var ports = new HashSet<int>();
        foreach (var table in tables)
        {
            ports.UnionWith(SocketTables.Listeners(table));
        }

        return ports;
    }

    // The lines of a table past its heading; none when it cannot be read.
    private static string[] Read(string path)
    {
        try
        {
            return [.. File.ReadLines(path).Skip(1)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
