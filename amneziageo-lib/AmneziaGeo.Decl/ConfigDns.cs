namespace AmneziaGeo.Decl;

/// <summary>
/// Preferred DNS servers for local (non-tunneled) name resolution. Empty auto-detects system resolvers.
/// </summary>
public sealed record ConfigDns(
    string Name,
    string Servers);
