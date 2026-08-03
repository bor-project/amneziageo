using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Release picking for the prerelease channel: the GitHub API orders releases by creation time, so a rewritten
/// history or a late-published tag can leave an older version on top. The offered update must be the newest one.
/// </summary>
public sealed class UpdateCheckerTests
{
    [Fact]
    public void TakesHighestVersionRegardlessOfApiOrder()
    {
        var json = Releases(
            Release("v1.0.16.0", "https://example.test/16/update.json"),
            Release("v1.0.17.2", "https://example.test/172/update.json"),
            Release("v1.0.17.1", "https://example.test/171/update.json"));

        Assert.Equal("https://example.test/172/update.json", UpdateChecker.SelectManifestUrl(json));
    }

    [Fact]
    public void SkipsDrafts()
    {
        var json = Releases(
            Release("v1.0.18.0", "https://example.test/180/update.json", draft: true),
            Release("v1.0.17.2", "https://example.test/172/update.json"));

        Assert.Equal("https://example.test/172/update.json", UpdateChecker.SelectManifestUrl(json));
    }

    [Fact]
    public void SkipsReleaseWithoutManifest()
    {
        var json = Releases(
            Release("v1.0.18.0"),
            Release("v1.0.17.2", "https://example.test/172/update.json"));

        Assert.Equal("https://example.test/172/update.json", UpdateChecker.SelectManifestUrl(json));
    }

    [Fact]
    public void PrefersStableOverPrereleaseOfSameVersion()
    {
        var json = Releases(
            Release("v1.0.17.2-rc", "https://example.test/rc/update.json", prerelease: true),
            Release("v1.0.17.2", "https://example.test/172/update.json"));

        Assert.Equal("https://example.test/172/update.json", UpdateChecker.SelectManifestUrl(json));
    }

    [Fact]
    public void ReadsVersionFromSuffixedTag()
    {
        var json = Releases(
            Release("v1.0.17.2", "https://example.test/172/update.json"),
            Release("v1.0.18.0-beta", "https://example.test/beta/update.json", prerelease: true));

        Assert.Equal("https://example.test/beta/update.json", UpdateChecker.SelectManifestUrl(json));
    }

    [Fact]
    public void ReturnsNullWhenNothingIsPublishable()
    {
        Assert.Null(UpdateChecker.SelectManifestUrl("[]"));
    }

    private static string Releases(params string[] releases)
    {
        return $"[{string.Join(",", releases)}]";
    }

    private static string Release(string tag, string? manifest = null, bool prerelease = false, bool draft = false)
    {
        var assets = manifest is null
            ? "[]"
            : $"[{{\"name\":\"update.json\",\"browser_download_url\":\"{manifest}\"}}]";
        return $"{{\"tag_name\":\"{tag}\",\"prerelease\":{Json(prerelease)},\"draft\":{Json(draft)},\"assets\":{assets}}}";
    }

    private static string Json(bool value)
    {
        return value ? "true" : "false";
    }
}
