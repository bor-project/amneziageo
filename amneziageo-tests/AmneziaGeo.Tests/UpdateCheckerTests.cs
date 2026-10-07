using AmneziaGeo.Decl;
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

    [Fact]
    public void OffersAHigherVersion()
    {
        Assert.True(UpdateFeed.IsUpdate("1.2.8.0", "1.2.7.3"));
    }

    [Fact]
    public void SaysNothingAboutTheSameVersion()
    {
        Assert.False(UpdateFeed.IsUpdate("1.2.7.3", "1.2.7.3"));
    }

    [Fact]
    public void SaysNothingAboutAnOlderVersion()
    {
        Assert.False(UpdateFeed.IsUpdate("1.2.7.0", "1.2.7.3"));
    }

    [Fact]
    public void TakesAVersionItCannotReadAsAnOffer()
    {
        Assert.True(UpdateFeed.IsUpdate("nightly", "1.2.7.3"));
    }

    [Fact]
    public void OffersNothingWhenTheReleaseCarriesNoInstallerOfTheBuild()
    {
        var info = UpdateChecker.BuildInfo(
            Manifest("1.10.2.0", "AmneziaGeo-1.10.2.0-win-x64.exe", "AmneziaGeo-1.10.2.0-win-arm64-fdd.exe"),
            new Uri("https://example.test/v1.10.2.0-beta.1/update.json"),
            "1.10.1.0",
            "win-x64-fdd");

        Assert.NotNull(info);
        Assert.False(info.Available);
        Assert.Equal("1.10.2.0", info.Version);
        Assert.Null(info.Asset);
    }

    [Fact]
    public void OffersTheUpdateWhoseReleaseCarriesTheInstallerOfTheBuild()
    {
        var info = UpdateChecker.BuildInfo(
            Manifest("1.10.2.0", "AmneziaGeo-1.10.2.0-win-x64.exe", "AmneziaGeo-1.10.2.0-win-x64-fdd.exe"),
            new Uri("https://example.test/v1.10.2.0-beta.1/update.json"),
            "1.10.1.0",
            "win-x64-fdd");

        Assert.NotNull(info);
        Assert.True(info.Available);
        Assert.Equal("https://example.test/v1.10.2.0-beta.1/AmneziaGeo-1.10.2.0-win-x64-fdd.exe", info.SetupUrl);
        Assert.Equal("AmneziaGeo-1.10.2.0-win-x64-fdd.exe", info.Asset?.Name);
    }

    [Fact]
    public void OffersTheUpdateOfAManifestThatListsNoInstallers()
    {
        var info = UpdateChecker.BuildInfo(
            "{\"version\":\"1.10.2.0\",\"setup\":\"AmneziaGeoSetup.exe\"}",
            new Uri("https://example.test/v1.10.2.0/update.json"),
            "1.10.1.0",
            "win-x64-fdd");

        Assert.NotNull(info);
        Assert.True(info.Available);
        Assert.Equal("https://example.test/v1.10.2.0/AmneziaGeo-1.10.2.0-win-x64-fdd.exe", info.SetupUrl);
    }

    [Fact]
    public void SaysNothingAboutTheSameVersionWhateverTheReleaseCarries()
    {
        var info = UpdateChecker.BuildInfo(
            Manifest("1.10.2.0", "AmneziaGeo-1.10.2.0-win-x64-fdd.exe"),
            new Uri("https://example.test/v1.10.2.0/update.json"),
            "1.10.2.0",
            "win-x64-fdd");

        Assert.NotNull(info);
        Assert.False(info.Available);
    }

    // A manifest of a release that lists the installers named.
    private static string Manifest(string version, params string[] installers)
    {
        var listed = installers.Select(name => $"{{\"name\":\"{name}\",\"platform\":\"windows\",\"sha256\":\"ab\"}}");

        return $"{{\"version\":\"{version}\",\"setup\":\"{installers[0]}\",\"installers\":[{string.Join(",", listed)}]}}";
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
