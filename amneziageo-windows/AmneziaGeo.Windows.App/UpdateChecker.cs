using System.Net.Http;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The result of an application update check.
/// </summary>
internal sealed record UpdateInfo(bool Available, string Version, string SetupUrl, string Description, string Sha256, UpdateAsset? Asset = null, UpdateRoute? Route = null, string Channel = "")
{
    /// <summary>
    /// Whether the offer came from a source on a channel.
    /// </summary>
    public bool CameFrom(UpdateRoute route, string channel) =>
        Route is not null && string.Equals(Route.Url, route.Url, StringComparison.OrdinalIgnoreCase) && Channel == channel;
}

/// <summary>
/// Checks the source of the releases for a newer version.
/// </summary>
internal sealed class UpdateChecker(HttpClient http)
{
    public async Task<UpdateInfo?> CheckAsync(
        UpdateRoute route, string channel, string currentVersion, string buildTarget, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(route.Url))
        {
            return null;
        }

        var release = await UpdateSources.FetchAsync(http, route, channel, _ => { }, ct).ConfigureAwait(false);
        return release is null ? null : BuildInfo(release.Manifest, release.BaseUrl, currentVersion, buildTarget, route) with { Channel = channel };
    }

    // Release picking lives in the shared feed; both agents order the same way.
    internal static string? SelectManifestUrl(string releasesJson) => UpdateFeed.SelectManifestUrl(releasesJson);

    internal static UpdateInfo? BuildInfo(string json, Uri baseUrl, string currentVersion, string buildTarget)
    {
        var meta = UpdateFeed.ParseManifest(json);
        return meta is null ? null : BuildInfo(meta, baseUrl, currentVersion, buildTarget, null);
    }

    private static UpdateInfo BuildInfo(UpdateManifest meta, Uri baseUrl, string currentVersion, string buildTarget, UpdateRoute? route)
    {
        var version = meta.Version ?? string.Empty;
        var setup = ResolveSetupName(meta, version, buildTarget);
        var setupUrl = new Uri(baseUrl, setup).ToString();
        // The setup's published SHA-256, matched by installer name; empty on a legacy manifest without hashes.
        var sha256 = UpdateFeed.Sha256Of(meta, setup);
        var published = UpdateFeed.AssetNamed(meta, setup);
        var asset = route is null ? published : UpdateSources.Fitting(published, route);
        // A release that lists its installers without the one of this build has nothing to update it with.
        var carried = asset is not null || meta.Installers is not { Count: > 0 };
        return new UpdateInfo(
            carried && UpdateFeed.IsUpdate(version, currentVersion),
            version,
            setupUrl,
            meta.Description ?? string.Empty,
            sha256,
            asset,
            route);
    }

    // The per-build installer name (AmneziaGeo-<version>-<target>.exe) so each arch/payload gets its own file;
    // falls back to the manifest setup field for a build with no baked target (or a legacy manifest).
    private static string ResolveSetupName(UpdateManifest meta, string version, string buildTarget)
    {
        if (!string.IsNullOrWhiteSpace(buildTarget))
        {
            return $"AmneziaGeo-{version}-{buildTarget}.exe";
        }

        return string.IsNullOrWhiteSpace(meta.Setup) ? "AmneziaGeoSetup.exe" : meta.Setup;
    }
}
