using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// One client of the local proxy, as the status shows it.
/// </summary>
/// <param name="Address">Where it dialled from.</param>
/// <param name="Name">What it is: the applications behind a client of this machine, empty for one on the network.</param>
/// <param name="Connections">How many connections it holds.</param>
/// <param name="Since">When its oldest connection was accepted.</param>
public sealed record ProxyClientEntry(string Address, string Name, int Connections, DateTimeOffset Since)
{
    /// <summary>
    /// One entry per client address out of the open connections, the busiest first.
    /// </summary>
    public static IReadOnlyList<ProxyClientEntry> Gather(IEnumerable<(string Address, DateTimeOffset Since)> connections)
    {
        return [.. connections
            .GroupBy(connection => connection.Address, StringComparer.Ordinal)
            .Select(group => new ProxyClientEntry(group.Key, string.Empty, group.Count(), group.Min(connection => connection.Since)))
            .OrderByDescending(entry => entry.Connections)
            .ThenBy(entry => entry.Address, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Renders the clients as rows of text, one per client.
    /// </summary>
    public static string ToRows(IReadOnlyList<ProxyClientEntry> clients)
    {
        return string.Join('\n', clients.Select(client => string.Join(
            '\t',
            client.Address,
            client.Connections.ToString(CultureInfo.InvariantCulture),
            client.Since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            client.Name.Replace('\t', ' ').Replace('\n', ' '))));
    }

    /// <summary>
    /// Reads the clients back from their rows; a row that is not one is skipped.
    /// </summary>
    public static IReadOnlyList<ProxyClientEntry> FromRows(IEnumerable<string> rows)
    {
        var clients = new List<ProxyClientEntry>();
        foreach (var row in rows)
        {
            var cells = row.Split('\t');
            if (cells.Length >= 3
                && cells[0].Length > 0
                && int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var connections)
                && long.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out var since)
                && since <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                clients.Add(new ProxyClientEntry(
                    cells[0],
                    cells.Length > 3 ? cells[3] : string.Empty,
                    connections,
                    DateTimeOffset.FromUnixTimeMilliseconds(since)));
            }
        }

        return clients;
    }
}
