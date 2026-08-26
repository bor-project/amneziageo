using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Moves the default route onto a tunnel that already stands beside the one carrying it: it takes the route over
/// without being dialled, so what is open through it stays open and the move costs a pipe round-trip instead of a
/// handshake.
/// </summary>
internal static class RouteHandover
{
    /// <summary>
    /// Hands everything over from one running tunnel to another; reports whether it went.
    /// </summary>
    /// <param name="control">Tunnels of this machine.</param>
    /// <param name="store">Library the tunnel state is read from.</param>
    /// <param name="holder">Tunnel giving the route up.</param>
    /// <param name="next">Tunnel taking it over.</param>
    /// <param name="logger">Journal.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<bool> TryAsync(AgentControl control, IStateStore store, string holder, string next, ILogger logger, CancellationToken ct)
    {
        if (control.Find(next) is not { Running: true, HandshakeAge: >= 0 })
        {
            return false;
        }

        // Only a tunnel raised with the default route clipped off it is asked - that is a tunnel that wanted to
        // carry everything and was refused, and nothing else is owed the route.
        if (await store.GetSettingAsync(TunnelPaths.DefaultRouteKey(next), ct).ConfigureAwait(false) != TunnelPaths.ClipDefaultRoute)
        {
            return false;
        }

        // Make before break: the one taking over carries everything before the one giving up stops, so no packet
        // meets a moment with nowhere to go.
        if (RuntimeSnapshotPipe.Send(next, RuntimeSnapshotPipe.Carry(take: true), logger) != "ok")
        {
            logger.LogInformation("{Config} could not take everything over as it stands, so it is dialled again to carry it", next);
            return false;
        }

        RuntimeSnapshotPipe.Send(holder, RuntimeSnapshotPipe.Carry(take: false), logger);
        control.ClaimDefaultRoute(next, preferred: true);
        control.ClaimResolver(next);
        logger.LogInformation("{Config} was already standing beside the tunnel, so it took everything over without being dialled and what was open through it stayed open", next);
        return true;
    }
}
