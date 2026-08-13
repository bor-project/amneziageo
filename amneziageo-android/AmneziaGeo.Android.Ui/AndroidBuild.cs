using System.Reflection;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Build identity of the package.
/// </summary>
internal static class AndroidBuild
{
    /// <summary>
    /// Update metadata URL baked at build time; empty turns the update check off.
    /// </summary>
    public static string UpdateUrl { get; } = Metadata("AmneziaGeo.UpdateUrl");

    /// <summary>
    /// Whether the update check starts out offering prereleases.
    /// </summary>
    public static bool AllowPrerelease { get; } = Metadata("AmneziaGeo.AllowPrerelease") == "1";

    private static string Metadata(string key)
    {
        return typeof(AndroidBuild).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
    }
}
