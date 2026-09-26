using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The default geo sources reach a fresh install whole and an older one only by what joined the set since, each in
/// front of the default that follows it, without bringing back what the owner removed or overwriting a held name.
/// </summary>
public sealed class GeoDefaultsTests : IAsyncLifetime
{
    private const string Zkeenip = "https://github.com/jameszeroX/zkeen-ip/releases/latest/download/zkeenip.dat";
    private const string Geosite = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";
    private const string Geoip = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
    private const string SiteRu = "https://github.com/runetfreedom/russia-blocked-geosite/releases/latest/download/geosite-ru-only.dat";
    private const string IpRu = "https://github.com/runetfreedom/russia-blocked-geoip/releases/latest/download/geoip-ru-only.dat";
    private const string Mine = "https://example.org/mine.dat";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-geo-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AFreshInstallTakesZkeenipFirst()
    {
        var added = await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.True(added);
        Assert.Equal([Zkeenip, Geosite, Geoip, SiteRu, IpRu], held.Select(row => row.Url));
        Assert.Equal("zkeenip", held[0].Name);
        Assert.Equal([1, 2, 3, 4, 5], held.Select(row => row.Position));
        Assert.Equal("3", await _store.GetSettingAsync("geo.seed-version"));
    }

    [Fact]
    public async Task AnInstallOfTheSecondSetTakesZkeenipOnTopOnce()
    {
        await HeldAsync("2", ("geosite-1", Geosite), ("geoip-2", Geoip), ("geosite-3", SiteRu), ("geoip-4", IpRu));

        var added = await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var again = await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.True(added);
        Assert.False(again);
        Assert.Equal(["zkeenip", "geosite-1", "geoip-2", "geosite-3", "geoip-4"], held.Select(row => row.Name));
        Assert.Equal([1, 2, 3, 4, 5], held.Select(row => row.Position));
    }

    [Fact]
    public async Task ADefaultRemovedByTheOwnerIsNotBroughtBack()
    {
        await HeldAsync("2", ("geoip-2", Geoip), ("geosite-3", SiteRu), ("geoip-4", IpRu));

        await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.Equal([Zkeenip, Geoip, SiteRu, IpRu], held.Select(row => row.Url));
    }

    [Fact]
    public async Task AnInstallOfTheFirstSetTakesWhatJoinedSinceInItsPlaces()
    {
        await HeldAsync(null, ("geosite-1", Geosite), ("geoip-2", Geoip));

        await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.Equal([Zkeenip, Geosite, Geoip, SiteRu, IpRu], held.Select(row => row.Url));
        Assert.Equal(["zkeenip", "geosite-1", "geoip-2", "geosite-4", "geoip-5"], held.Select(row => row.Name));
    }

    [Fact]
    public async Task ADefaultHeldUnderItsAddressIsNotAddedTwice()
    {
        await HeldAsync("2", ("geosite-1", Geosite), ("geoip-2", Geoip), ("geosite-3", SiteRu), ("geoip-4", IpRu), ("geoip-5", Zkeenip));

        var added = await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.False(added);
        Assert.Single(held, row => row.Url == Zkeenip);
    }

    [Fact]
    public async Task ANameAlreadyTakenIsNotOverwritten()
    {
        await HeldAsync(null, ("geosite-1", Geosite), ("geoip-2", Geoip), ("geosite-5", Mine));

        await GeoDefaults.SeedAsync(_store, null, null, CancellationToken.None);
        var held = await _store.ListGeoSourcesAsync();

        Assert.Equal(Mine, held.Single(row => row.Name == "geosite-5").Url);
        Assert.Equal("geosite-6", held.Single(row => row.Url == SiteRu).Name);
    }

    // Puts the sources at the places 1, 2, ... in the order given, and the mark of the set when there is one.
    private async Task HeldAsync(string? stamp, params (string Name, string Url)[] rows)
    {
        for (var index = 0; index < rows.Length; index++)
        {
            var kind = rows[index].Name.StartsWith("geoip", StringComparison.Ordinal) ? "geoip" : "geosite";
            await _store.SaveGeoSourceAsync(new GeoSource(rows[index].Name, kind, rows[index].Url, index + 1));
        }

        if (stamp is not null)
        {
            await _store.SetSettingAsync("geo.seed-version", stamp);
        }
    }
}
