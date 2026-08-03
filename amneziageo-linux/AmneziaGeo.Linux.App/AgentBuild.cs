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
