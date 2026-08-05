using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// What a connection applies beyond its routing rules: the resolvers the destinations that stay on the machine's
/// own network are looked up through, and the idle window a destination keeps the route it earned.
/// </summary>
internal sealed record TunnelOptions(IReadOnlyList<IPAddress> LocalResolvers, int RouteTtlSeconds)
{
    /// <summary>
    /// Idle window a route survives while the library names none.
    /// </summary>
    public const int DefaultRouteTtlSeconds = 300;

    /// <summary>
    /// What a connection runs under while the library holds no preferences.
    /// </summary>
    public static TunnelOptions Default { get; } = new([], DefaultRouteTtlSeconds);

    /// <summary>
    /// Reads the resolver list stored for a configuration.
    /// </summary>
    public static TunnelOptions Read(string? resolvers, int routeTtlSeconds)
    {
        var servers = new List<IPAddress>();
        foreach (var entry in (resolvers ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                servers.Add(address);
            }
        }

        return new TunnelOptions(servers, routeTtlSeconds);
    }
}
