using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Decl;

/// <summary>
/// One published file of a release.
/// </summary>
public sealed record UpdateAsset(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("variant")] string? Variant,
    [property: JsonPropertyName("sha256")] string? Sha256);

/// <summary>
/// Update metadata published next to a release.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("setup")] string? Setup,
    [property: JsonPropertyName("installers")] IReadOnlyList<UpdateAsset>? Installers);

/// <summary>
/// Release feed every agent checks its own build against.
/// </summary>
public static class UpdateFeed
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads an update metadata file; a manifest without a version is no manifest.
    /// </summary>
    public static UpdateManifest? ParseManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, _options);
        return manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ? null : manifest;
    }

    /// <summary>
    /// Takes the manifest of the highest released version; a stable release wins over a prerelease of the
    /// same version.
    /// </summary>
    public static string? SelectManifestUrl(string releasesJson)
    {
        var releases = JsonSerializer.Deserialize<List<GhRelease>>(releasesJson, _options);
        return releases?
            .Where(r => r is { Draft: false, Assets: not null })
            .OrderByDescending(r => TagVersion(r.TagName))
            .ThenBy(r => r.Prerelease)
            .Select(r => r.Assets!
                .FirstOrDefault(a => string.Equals(a.Name, "update.json", StringComparison.OrdinalIgnoreCase))?.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    /// <summary>
    /// Splits a GitHub release URL into owner and repository.
    /// </summary>
    public static bool TryGitHubRepo(string metadataUrl, out string owner, out string repo)
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

    /// <summary>
    /// Releases endpoint of a repository.
    /// </summary>
    public static string ReleasesUrl(string owner, string repo) =>
        $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20";

    /// <summary>
    /// Assets published for a platform and architecture.
    /// </summary>
    public static IReadOnlyList<UpdateAsset> AssetsFor(UpdateManifest manifest, string platform, string arch)
    {
        return manifest.Installers is null
            ? []
            : [.. manifest.Installers.Where(a =>
                string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Arch, arch, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Published SHA-256 of an asset; empty on a legacy manifest without hashes.
    /// </summary>
    public static string Sha256Of(UpdateManifest manifest, string name)
    {
        return manifest.Installers?
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?
            .Sha256 ?? string.Empty;
    }

    /// <summary>
    /// Whether a remote version is newer than the installed one.
    /// </summary>
    public static bool IsUpdate(string remote, string current)
    {
        // An older release is not an offer: Android turns the downgrade down, and the check would keep offering it.
        if (Version.TryParse(remote, out var r) && Version.TryParse(current, out var c))
        {
            return r.CompareTo(c) > 0;
        }

        return !string.Equals(remote.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // Reads the numeric part of a release tag (v1.2.3.4-beta -> 1.2.3.4); an unparsable tag sorts lowest.
    private static Version TagVersion(string? tag)
    {
        var text = (tag ?? string.Empty).TrimStart('v', 'V');
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        return Version.TryParse(text, out var version) ? version : new Version(0, 0);
    }

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<GhAsset>? Assets);

    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? Url);
}
