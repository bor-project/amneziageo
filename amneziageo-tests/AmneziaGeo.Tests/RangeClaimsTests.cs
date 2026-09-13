using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Из пересекающихся правил адреса решает более узкое, при равных - «Напрямую».
/// </summary>
public sealed class RangeClaimsTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-claims-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void ANarrowerTunnelRange_CutsItsHoleInTheDirectRange()
    {
        var direct = GeoIpRanges.Build(RangeClaims.CarveDirect(["10.0.0.0/8"], ["10.8.2.0/24"]));

        Assert.False(direct.Contains(Address("10.8.2.5")));
        Assert.True(direct.Contains(Address("10.8.3.1")));
        Assert.True(direct.Contains(Address("10.0.0.1")));
        Assert.True(direct.Contains(Address("10.255.255.255")));
    }

    [Fact]
    public void AWiderTunnelRange_LeavesTheDirectRangeWhole()
    {
        Assert.Equal(["10.8.2.0/24"], RangeClaims.CarveDirect(["10.8.2.0/24"], ["10.0.0.0/8"]));
    }

    [Fact]
    public void TheSameRangeInBothBuckets_StaysDirect()
    {
        Assert.Equal(["192.168.1.0/24"], RangeClaims.CarveDirect(["192.168.1.0/24"], ["192.168.1.0/24"]));
    }

    [Fact]
    public void NestedRules_AreDecidedByTheNarrowest()
    {
        var direct = GeoIpRanges.Build(RangeClaims.CarveDirect(["10.0.0.0/8", "10.8.2.0/24"], ["10.8.0.0/16", "10.8.2.128/25"]));

        Assert.True(direct.Contains(Address("10.9.0.1")));
        Assert.False(direct.Contains(Address("10.8.3.5")));
        Assert.True(direct.Contains(Address("10.8.2.5")));
        Assert.False(direct.Contains(Address("10.8.2.200")));
    }

    [Fact]
    public void AHostOfTheTunnel_LeavesTheRestOfTheNetworkDirect()
    {
        var carved = RangeClaims.CarveDirect(["0.0.0.0/0"], ["1.1.1.1"]);

        Assert.Equal(32, carved.Count);
        Assert.False(GeoIpRanges.Build(carved).Contains(Address("1.1.1.1")));
        Assert.True(GeoIpRanges.Build(carved).Contains(Address("1.1.1.0")));
    }

    [Fact]
    public void AnEntryOfAnotherFamily_IsCarriedThrough()
    {
        Assert.Equal(["fd00::/8", "10.128.0.0/9"], RangeClaims.CarveDirect(["fd00::/8", "10.0.0.0/8"], ["10.0.0.0/9"]));
    }

    [Fact]
    public async Task ASavedList_CarriesTheNarrowerTunnelRangeOutOfItsDirectOne()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        var list = await geo.MaterializeDraftAsync(["direct|cidr:10.0.0.0/8", "proxy|cidr:10.8.2.0/24"]);

        Assert.Equal(["10.8.2.0/24"], list.Routes);
        Assert.False(GeoIpRanges.Build(list.DirectRoutes).Contains(Address("10.8.2.5")));
        Assert.True(GeoIpRanges.Build(list.DirectRoutes).Contains(Address("10.1.2.3")));
    }

    private static uint Address(string text)
    {
        Assert.True(GeoIpRanges.TryToNumeric(System.Net.IPAddress.Parse(text), out var value));
        return value;
    }
}
