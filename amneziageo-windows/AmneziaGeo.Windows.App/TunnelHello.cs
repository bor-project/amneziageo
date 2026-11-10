using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Confines hello to the selected tunnel adapter.
/// </summary>
internal static class TunnelHello
{
    internal static bool Reaches(RouteManager routes, string config, IPAddress address) =>
        routes.FindTunnelIndex(config, address.AddressFamily) is { } index && RouteManager.ReachesOn(index, address);

    internal static Task<HelloReply> AskAsync(RouteManager routes, string config, ServiceTarget point, CancellationToken ct) =>
        ServerHello.AskAsync(point, (context, token) => TunnelServiceConnection.ConnectAsync(context.DnsEndPoint,
            (address, socket, _) =>
            {
                if (routes.FindTunnelIndex(config, address.AddressFamily) is not { } index || !RouteManager.ReachesOn(index, address))
                {
                    return ValueTask.FromResult(false);
                }

                UnicastInterface.Pin(socket, index);
                return ValueTask.FromResult(true);
            }, token), ct);
}
