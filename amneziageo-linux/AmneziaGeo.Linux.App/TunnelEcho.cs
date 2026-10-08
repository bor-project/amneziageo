using System.Net;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Echoes through the device of a tunnel and nowhere else.
/// </summary>
internal static class TunnelEcho
{
    /// <summary>
    /// Returns the echo of the tunnel standing on the device given.
    /// </summary>
    public static Func<IPAddress, int, CancellationToken, Task<int>> Through(string device)
    {
        return (target, timeoutMs, ct) => IcmpEcho.ConfinedAsync(target, timeoutMs, socket => DeviceSocket.Bind(socket, device), ct);
    }
}
