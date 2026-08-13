namespace AmneziaGeo.Routing;

/// <summary>
/// One connection the local proxy holds open for a client.
/// </summary>
/// <param name="Address">Where the client dialled from.</param>
/// <param name="Port">Port it dialled from, which names the client on this machine.</param>
/// <param name="Since">When the connection was accepted.</param>
public sealed record ProxyPeer(string Address, int Port, DateTimeOffset Since);
