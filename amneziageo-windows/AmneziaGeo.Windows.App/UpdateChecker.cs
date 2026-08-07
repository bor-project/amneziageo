using System.Net.Http;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The result of an application update check.
/// </summary>
internal sealed record UpdateInfo(bool Available, string Version, string SetupUrl, string Description, string Sha256);

/// <summary>
/// Checks an HTTP update metadata file for a different version.
/// </summary>
internal sealed class UpdateChecker(HttpClient http)
{
    public async Task<UpdateInfo?> CheckAsync(
        string metadataUrl, string currentVersion, string buildTarget, bool allowPrerelease, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            return null;
        }

        // Prerelease channel: pick the newest release including prereleases via the GitHub API. Falls back to
        // the stable metadata URL when it is not a GitHub release URL or the API is unreachable.
        if (allowPrerelease && UpdateFeed.TryGitHubRepo(metadataUrl, out var owner, out var repo))
        {
            var pre = await CheckViaGitHubAsync(owner, repo, currentVersion, buildTarget, ct);
            if (pre is not null)
            {
                return pre;
            }
        }

        var json = await http.GetStringAsync(metadataUrl, ct);
        return BuildInfo(json, new Uri(metadataUrl), currentVersion, buildTarget);
    }

    private async Task<UpdateInfo?> CheckViaGitHubAsync(
        string owner, string repo, string currentVersion, string buildTarget, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateFeed.ReleasesUrl(owner, repo));
        req.Headers.TryAddWithoutValidation("User-Agent", "AmneziaGeo-UpdateChecker");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var manifestUrl = SelectManifestUrl(body);
        if (manifestUrl is null)
        {
            return null;
        }

        var json = await http.GetStringAsync(manifestUrl, ct);
        return BuildInfo(json, new Uri(manifestUrl), currentVersion, buildTarget);
    }

    // Release picking lives in the shared feed; both agents order the same way.
    internal static string? SelectManifestUrl(string releasesJson) => UpdateFeed.SelectManifestUrl(releasesJson);

    private static UpdateInfo? BuildInfo(string json, Uri baseUrl, string currentVersion, string buildTarget)
    {
        var meta = UpdateFeed.ParseManifest(json);
        if (meta is null)
        {
            return null;
        }

        var version = meta.Version ?? string.Empty;
        var setup = ResolveSetupName(meta, version, buildTarget);
        var setupUrl = new Uri(baseUrl, setup).ToString();
        // The setup's published SHA-256, matched by installer name; empty on a legacy manifest without hashes.
        var sha256 = UpdateFeed.Sha256Of(meta, setup);
        return new UpdateInfo(UpdateFeed.IsUpdate(version, currentVersion), version, setupUrl, meta.Description ?? string.Empty, sha256);
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
