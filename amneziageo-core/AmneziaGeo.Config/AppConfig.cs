namespace AmneziaGeo.Config;

public sealed record AppConfig
{
    public string DatabasePath { get; init; } = "amneziageo.db";

    public string? ActiveProfile { get; init; }

    public bool GeoSplitTunnel { get; init; }
}
