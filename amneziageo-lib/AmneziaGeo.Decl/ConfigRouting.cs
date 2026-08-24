namespace AmneziaGeo.Decl;

/// <summary>
/// Routing list a configuration routes through. A null id sends every destination through the tunnel.
/// </summary>
public sealed record ConfigRouting(
    string Name,
    long? RoutingListId);
