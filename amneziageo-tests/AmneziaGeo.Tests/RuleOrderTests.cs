using System.Net;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Порядок правил списка, одна таблица случаев для всех платформ: узкое сильнее широкого, явный хост сильнее
/// категории, на одной ширине блок, потом «Напрямую», потом туннель.
/// </summary>
public sealed class RuleOrderTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-order-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Случаи по адресам: правила списка, адрес и куда он идёт.
    /// </summary>
    public static TheoryData<string[], string, RoleToken> Addresses => new()
    {
        { ["block|cidr:10.0.0.0/8", "proxy|cidr:10.1.0.0/16"], "10.1.2.3", RoleToken.Proxy },
        { ["block|cidr:10.0.0.0/8", "proxy|cidr:10.1.0.0/16"], "10.2.0.1", RoleToken.Block },
        { ["block|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16"], "10.1.0.9", RoleToken.Direct },
        { ["direct|cidr:10.0.0.0/8", "block|cidr:10.1.0.0/16"], "10.1.0.9", RoleToken.Block },
        { ["direct|cidr:10.0.0.0/8", "block|cidr:10.1.0.0/16"], "10.9.0.9", RoleToken.Direct },
        { ["proxy|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16"], "10.1.0.9", RoleToken.Direct },
        { ["proxy|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16"], "10.2.0.9", RoleToken.Proxy },
        { ["proxy|cidr:10.0.0.0/8", "block|cidr:10.1.0.0/16"], "10.1.0.9", RoleToken.Block },
        { ["direct|cidr:5.0.0.0/8", "proxy|cidr:5.1.2.0/24"], "5.1.2.3", RoleToken.Proxy },
        { ["direct|cidr:5.0.0.0/8", "proxy|cidr:5.1.2.0/24"], "5.1.3.3", RoleToken.Direct },
        { ["proxy|cidr:5.1.2.0/24", "direct|cidr:5.1.2.7/32"], "5.1.2.7", RoleToken.Direct },
        { ["proxy|cidr:192.168.1.0/24", "direct|cidr:192.168.1.0/24"], "192.168.1.5", RoleToken.Direct },
        { ["direct|cidr:192.168.1.0/24", "block|cidr:192.168.1.0/24"], "192.168.1.5", RoleToken.Block },
        { ["proxy|cidr:192.168.1.0/24", "block|cidr:192.168.1.0/24"], "192.168.1.5", RoleToken.Block },
        { ["block|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16", "proxy|cidr:10.1.2.0/24"], "10.1.2.3", RoleToken.Proxy },
        { ["block|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16", "proxy|cidr:10.1.2.0/24"], "10.1.3.3", RoleToken.Direct },
        { ["block|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16", "proxy|cidr:10.1.2.0/24"], "10.200.0.1", RoleToken.Block },
        { ["proxy|cidr:10.1.0.0/16"], "10.2.0.1", RoleToken.None },
    };

    /// <summary>
    /// Случаи по именам: правила списка, имя и куда оно идёт.
    /// </summary>
    public static TheoryData<string[], string, RoleToken> Names => new()
    {
        { ["direct|domain:example.com", "proxy|domain:mail.example.com"], "mail.example.com", RoleToken.Proxy },
        { ["direct|domain:example.com", "proxy|domain:mail.example.com"], "in.mail.example.com", RoleToken.Proxy },
        { ["direct|domain:example.com", "proxy|domain:mail.example.com"], "www.example.com", RoleToken.Direct },
        { ["block|domain:ads.example", "proxy|domain:cdn.ads.example"], "x.cdn.ads.example", RoleToken.Proxy },
        { ["block|domain:ads.example", "proxy|domain:cdn.ads.example"], "track.ads.example", RoleToken.Block },
        { ["proxy|domain:example.org", "block|domain:example.org"], "example.org", RoleToken.Block },
        { ["proxy|domain:example.org", "direct|domain:example.org"], "a.example.org", RoleToken.Direct },
        { ["proxy|domain:example.org"], "example.net", RoleToken.None },
    };

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task AnAddress_GoesWhereItsNarrowestRuleSends(string[] rules, string address, RoleToken expected)
    {
        var list = await new GeoConfigurator(_store, new NoFiles()).MaterializeDraftAsync(rules);

        Assert.Equal(expected, new TargetInspector(list, split: true).ForAddress(IPAddress.Parse(address)).Role);
    }

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task NoAddress_FallsIntoTwoSetsOfAList(string[] rules, string address, RoleToken expected)
    {
        var list = await new GeoConfigurator(_store, new NoFiles()).MaterializeDraftAsync(rules);
        Assert.True(GeoIpRanges.TryToNumeric(IPAddress.Parse(address), out var value));

        var holders = new[] { list.Routes, list.DirectRoutes, list.BlockRoutes }.Count(set => GeoIpRanges.Build(set).Contains(value));

        Assert.Equal(expected == RoleToken.None ? 0 : 1, holders);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AName_GoesWhereItsNarrowestRuleSends(string[] rules, string name, RoleToken expected)
    {
        var list = await new GeoConfigurator(_store, new NoFiles()).MaterializeDraftAsync(rules);

        Assert.Equal(expected, new TargetInspector(list, split: true).ForDomain(name).Role);
    }
}
