using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Decl;

/// <summary>
/// One published file of a release.
/// </summary>
/// <param name="Name">The file among the files of the release.</param>
/// <param name="Platform">The system the file installs on.</param>
/// <param name="Arch">The architecture the file installs on.</param>
/// <param name="Variant">The kind of the file for its platform.</param>
/// <param name="Sha256">The digest of the file, empty on a legacy manifest.</param>
/// <param name="Size">The size of the file in bytes, 0 where the manifest names none.</param>
/// <param name="Patch">The patch that makes the file on a machine with the release before, null where the release
/// carries none.</param>
public sealed record UpdateAsset(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("variant")] string? Variant,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("size")] long Size = 0,
    [property: JsonPropertyName("patch")] UpdatePatchFile? Patch = null);

/// <summary>
/// The patch beside a published file: it makes the files the published file installs out of the files of one earlier
/// version.
/// </summary>
/// <param name="Name">The file among the files of the release.</param>
/// <param name="Size">The size of the file, in bytes.</param>
/// <param name="Sha256">The digest of the file.</param>
/// <param name="From">The version the patch starts from.</param>
public sealed record UpdatePatchFile(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("from")] string? From);

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
    private const string AndroidPlatform = "android";
    private const string AndroidEveryArch = "universal";

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
    /// Takes the manifest of the highest released version a channel takes; of the same version a stable release wins
    /// over a beta and a beta over a test build.
    /// </summary>
    public static string? SelectManifestUrl(string releasesJson, string channel)
    {
        var releases = JsonSerializer.Deserialize<List<GhRelease>>(releasesJson, _options);
        return releases?
            .Where(r => r is { Draft: false, Assets: not null } && UpdateChannels.Takes(channel, UpdateChannels.OfTag(r.TagName, r.Prerelease)))
            .OrderByDescending(r => TagVersion(r.TagName))
            .ThenBy(r => UpdateChannels.Rank(UpdateChannels.OfTag(r.TagName, r.Prerelease)))
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
    /// Android package for a processor type: its own one if the release carries it, the package of every type otherwise.
    /// </summary>
    public static UpdateAsset? AndroidAsset(UpdateManifest manifest, string arch)
    {
        return AssetsFor(manifest, AndroidPlatform, arch).FirstOrDefault()
            ?? AssetsFor(manifest, AndroidPlatform, AndroidEveryArch).FirstOrDefault();
    }

    /// <summary>
    /// Architecture the release names the Android package of a processor type by.
    /// </summary>
    public static string AndroidArch(Architecture processor) => processor switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => processor.ToString(),
    };

    /// <summary>
    /// Published SHA-256 of an asset; empty on a legacy manifest without hashes.
    /// </summary>
    public static string Sha256Of(UpdateManifest manifest, string name)
    {
        return AssetNamed(manifest, name)?.Sha256 ?? string.Empty;
    }

    /// <summary>
    /// The asset of a release by its file name.
    /// </summary>
    public static UpdateAsset? AssetNamed(UpdateManifest manifest, string name)
    {
        return manifest.Installers?
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The patch of an asset for a machine with the version given; null where the asset carries none or its patch
    /// starts from another version.
    /// </summary>
    public static UpdatePatchFile? PatchFor(UpdateAsset? asset, string installed)
    {
        return asset?.Patch is { Name.Length: > 0, Size: > 0, Sha256.Length: > 0, From: { } from } patch
            && Version.TryParse(from, out var start)
            && Version.TryParse(installed, out var held)
            && Four(start).Equals(Four(held))
                ? patch
                : null;
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

    // A version with every one of its four numbers set.
    private static Version Four(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

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
