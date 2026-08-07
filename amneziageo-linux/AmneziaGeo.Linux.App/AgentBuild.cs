using System.Reflection;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Build identity of the agent.
/// </summary>
internal static class AgentBuild
{
    /// <summary>
    /// Agent version.
    /// </summary>
    public static string Version { get; } = Resolve();

    /// <summary>
    /// Update metadata URL baked at build time; empty turns the update check off.
    /// </summary>
    public static string UpdateUrl { get; } = Metadata("AmneziaGeo.UpdateUrl");

    /// <summary>
    /// Whether the update check also offers prereleases.
    /// </summary>
    public static bool AllowPrerelease { get; } = Metadata("AmneziaGeo.AllowPrerelease") == "1";

    private static string Metadata(string key)
    {
        return typeof(AgentBuild).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
    }

    private static string Resolve()
    {
        var informational = typeof(AgentBuild).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is { Length: > 0 })
        {
            var metadata = informational.IndexOf('+');
            return metadata > 0 ? informational[..metadata] : informational;
        }

        return typeof(AgentBuild).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
