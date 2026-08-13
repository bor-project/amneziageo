namespace AmneziaGeo.Ipc;

/// <summary>
/// One client of the local proxy, as the status shows it.
/// </summary>
/// <param name="Address">Where it dialled from.</param>
/// <param name="Name">What it is: the applications behind a client of this machine, empty for one on the network.</param>
/// <param name="Connections">How many connections it holds.</param>
/// <param name="Since">When its oldest connection was accepted.</param>
public sealed record ProxyClientEntry(string Address, string Name, int Connections, DateTimeOffset Since);
