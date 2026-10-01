using System.Buffers.Binary;
using System.Globalization;

namespace AmneziaGeo.Routing;

/// <summary>
/// Reads the far ends of the sockets a Linux machine holds off the lines of its socket tables.
/// </summary>
public static class SocketTables
{
    private const int LocalColumn = 1;
    private const int RemoteColumn = 2;
    private const int StateColumn = 3;
    private const int V4HexLength = 8;
    private const int V6HexLength = 32;
    private const string Listening = "0A";
    private const string Mapped = "0000000000000000FFFF0000";

    /// <summary>
    /// The local ports of a table of stream sockets this machine takes connections in on.
    /// </summary>
    public static HashSet<int> Listeners(IEnumerable<string> lines)
    {
        var ports = new HashSet<int>();
        foreach (var line in lines)
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > StateColumn
                && string.Equals(columns[StateColumn], Listening, StringComparison.OrdinalIgnoreCase)
                && Port(columns[LocalColumn]) is { } port)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>
    /// Adds the far end of every socket of a table, host order; a socket on one of the accepting ports is a
    /// connection this machine took in and is left out.
    /// </summary>
    public static void Peers(IEnumerable<string> lines, IReadOnlySet<int>? accepting, HashSet<uint> peers)
    {
        foreach (var line in lines)
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length <= RemoteColumn)
            {
                continue;
            }

            var colon = columns[RemoteColumn].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || Address(columns[RemoteColumn][..colon]) is not { } address)
            {
                continue;
            }

            if (accepting is { Count: > 0 } && Port(columns[LocalColumn]) is { } port && accepting.Contains(port))
            {
                continue;
            }

            peers.Add(address);
        }
    }

    // Each address is printed as host-order words, and an IPv4 socket on a dual-stack listener shows up mapped.
    private static uint? Address(string hex)
    {
        if (hex.Length == V6HexLength)
        {
            return hex.StartsWith(Mapped, StringComparison.OrdinalIgnoreCase) ? Address(hex[Mapped.Length..]) : null;
        }

        if (hex.Length != V4HexLength || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word) || word == 0)
        {
            return null;
        }

        return BinaryPrimitives.ReverseEndianness(word);
    }

    // The port an endpoint of a table names after its address.
    private static int? Port(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');

        return colon > 0 && int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;
    }
}
