using System.Buffers.Binary;
using System.Globalization;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Reads the destinations the machine currently holds sockets to off the kernel's socket tables.
/// </summary>
internal sealed class ProcNet : ILiveDestinations
{
    private static readonly string[] Tables =
    [
        "/proc/net/tcp",
        "/proc/net/tcp6",
        "/proc/net/udp",
        "/proc/net/udp6",
    ];

    private const int RemoteColumn = 2;
    private const int V4HexLength = 8;
    private const int V6HexLength = 32;

    /// <summary>
    /// Remote addresses of every current connection, host order. The socket tables name no image here, so nothing
    /// is attributed to an app rule.
    /// </summary>
    public LiveDestinations Snapshot()
    {
        var peers = new HashSet<uint>();
        foreach (var table in Tables)
        {
            Read(table, peers);
        }

        return new LiveDestinations(peers, []);
    }

    private static void Read(string path, HashSet<uint> peers)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length <= RemoteColumn)
                {
                    continue;
                }

                var colon = columns[RemoteColumn].IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && Parse(columns[RemoteColumn][..colon]) is { } address)
                {
                    peers.Add(address);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Each address is printed as host-order words, and an IPv4 socket on a dual-stack listener shows up mapped.
    private static uint? Parse(string hex)
    {
        if (hex.Length == V6HexLength)
        {
            var mapped = hex.StartsWith("0000000000000000FFFF0000", StringComparison.OrdinalIgnoreCase);
            return mapped ? Parse(hex[24..]) : null;
        }

        if (hex.Length != V4HexLength || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word) || word == 0)
        {
            return null;
        }

        return BinaryPrimitives.ReverseEndianness(word);
    }
}
