using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The result of an application update check.
/// </summary>
internal sealed record UpdateInfo(bool Available, string Version, string SetupUrl, string Description);

/// <summary>
/// Checks an HTTP update metadata file for a different version.
/// </summary>
internal sealed class UpdateChecker(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<UpdateInfo?> CheckAsync(
        string metadataUrl, string currentVersion, string buildTarget, bool allowPrerelease, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            return null;
        }

        // Prerelease channel: pick the newest release including prereleases via the GitHub API. Falls back to
        // the stable metadata URL when it is not a GitHub release URL or the API is unreachable.
        if (allowPrerelease && TryGitHubRepo(metadataUrl, out var owner, out var repo))
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
        var api = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.TryAddWithoutValidation("User-Agent", "AmneziaGeo-UpdateChecker");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var releases = JsonSerializer.Deserialize<List<GhRelease>>(body, JsonOptions);
        var release = releases?.FirstOrDefault(r => r is { Draft: false, Assets: not null });
        var manifest = release?.Assets!.FirstOrDefault(a => string.Equals(a.Name, "update.json", StringComparison.OrdinalIgnoreCase));
        if (manifest?.Url is null)
        {
            return null;
        }

        var json = await http.GetStringAsync(manifest.Url, ct);
        return BuildInfo(json, new Uri(manifest.Url), currentVersion, buildTarget);
    }

    private static UpdateInfo? BuildInfo(string json, Uri baseUrl, string currentVersion, string buildTarget)
    {
        var meta = JsonSerializer.Deserialize<UpdateMetadata>(json, JsonOptions);
        if (meta is null || string.IsNullOrWhiteSpace(meta.Version))
        {
            return null;
        }

        var setup = ResolveSetupName(meta, buildTarget);
        var setupUrl = new Uri(baseUrl, setup).ToString();
        return new UpdateInfo(IsUpdate(meta.Version, currentVersion), meta.Version, setupUrl, meta.Description ?? string.Empty);
    }

    // The per-build installer name (AmneziaGeo-<version>-<target>.exe) so each arch/payload gets its own file;
    // falls back to the manifest setup field for a build with no baked target (or a legacy manifest).
    private static string ResolveSetupName(UpdateMetadata meta, string buildTarget)
    {
        if (!string.IsNullOrWhiteSpace(buildTarget))
        {
            return $"AmneziaGeo-{meta.Version}-{buildTarget}.exe";
        }

        return string.IsNullOrWhiteSpace(meta.Setup) ? "AmneziaGeoSetup.exe" : meta.Setup;
    }

    private static bool TryGitHubRepo(string metadataUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (!Uri.TryCreate(metadataUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1];
        return true;
    }

    // A remote version differing in either direction is an update; downgrade is supported (the bundle
    // removes a newer related install and the MSI's AllowDowngrades reinstalls the target).
    private static bool IsUpdate(string remote, string current)
    {
        if (System.Version.TryParse(remote, out var r) && System.Version.TryParse(current, out var c))
        {
            return r.CompareTo(c) != 0;
        }

        return !string.Equals(remote.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UpdateMetadata(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("setup")] string? Setup);

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<GhAsset>? Assets);

    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? Url);
}
