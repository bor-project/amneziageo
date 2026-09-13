namespace AmneziaGeo.Linux.App;

/// <summary>
/// Whether the agent runs inside a container.
/// </summary>
internal static class ContainerHost
{
    /// <summary>
    /// True inside a Docker or Podman container.
    /// </summary>
    public static bool Detected { get; } = File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
}
