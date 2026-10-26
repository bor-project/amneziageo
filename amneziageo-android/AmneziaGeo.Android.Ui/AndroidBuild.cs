using System.Reflection;
using AmneziaGeo.Decl;

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
    /// The kind of the release baked at build time: stable, beta or test; empty for a build of no kind.
    /// </summary>
    public static string ReleaseChannel { get; } = UpdateChannels.Of(Metadata("AmneziaGeo.ReleaseChannel"), string.Empty);

    /// <summary>
    /// The channel of the kind of the release baked at build time.
    /// </summary>
    public static string Channel { get; } = ReleaseChannel.Length > 0 ? ReleaseChannel : UpdateChannels.Stable;

    private static string Metadata(string key)
    {
        return typeof(AndroidBuild).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
    }
}
