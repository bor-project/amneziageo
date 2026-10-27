using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Имя решает самое узкое правило: явный хост раньше категории, внутри - точное имя, длинный суффикс, ключевое слово,
/// выражение; на одной ширине блок, потом «Напрямую», потом туннель.
/// </summary>
public sealed class NameRulesTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-names-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

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

    [Fact]
    public void AHostTheListNames_OutranksItsCategory()
    {
        var rules = new NameRules(
            [Named("google.com")],
            [Category("google.com"), new GeoDomain(GeoDomainKind.Full, "www.google.com")],
            []);

        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("www.google.com"));
        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("mail.google.com"));
        Assert.True(rules.Match("www.google.com")!.Value.Named);
    }

    [Fact]
    public void InsideACategory_TheExactNameThenTheLongestSuffixDecide()
    {
        var rules = new NameRules(
            [Category("example.com")],
            [new GeoDomain(GeoDomainKind.Full, "www.example.com")],
            [Category("ads.example.com")]);

        Assert.Equal(RouteVerdict.Direct, rules.Verdict("www.example.com"));
        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("shop.example.com"));
        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("example.com"));
        Assert.Equal(RouteVerdict.Block, rules.Verdict("x.ads.example.com"));
        Assert.Equal(RouteVerdict.None, rules.Verdict("example.org"));
    }

    [Fact]
    public void AKeyword_YieldsToADomain_AndTheLongerKeywordDecides()
    {
        var rules = new NameRules(
            [new GeoDomain(GeoDomainKind.Plain, "google")],
            [Category("google.com"), new GeoDomain(GeoDomainKind.Plain, "googleusercontent")],
            []);

        Assert.Equal(RouteVerdict.Direct, rules.Verdict("www.google.com"));
        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("google.de"));
        Assert.Equal(RouteVerdict.Direct, rules.Verdict("lh3.googleusercontent.net"));
    }

    [Fact]
    public void AnExpression_YieldsToEverythingNarrower()
    {
        var rules = new NameRules(
            [new GeoDomain(GeoDomainKind.Regex, "^.*\\.example\\.net$")],
            [new GeoDomain(GeoDomainKind.Plain, "cdn")],
            []);

        Assert.Equal(RouteVerdict.Direct, rules.Verdict("cdn.example.net"));
        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("www.example.net"));
    }

    [Fact]
    public void OnTheSameWidth_BlockThenDirectThenTheTunnel()
    {
        var all = new NameRules([Category("example.com")], [Category("example.com")], [Category("example.com")]);
        var two = new NameRules([Named("example.org")], [Named("example.org")], []);

        Assert.Equal(RouteVerdict.Block, all.Verdict("a.example.com"));
        Assert.Equal(RouteVerdict.Direct, two.Verdict("a.example.org"));
    }

    [Fact]
    public void ANameIsMatched_WithoutItsCaseAndTrailingDot()
    {
        var rules = new NameRules([Named("Example.COM.")], [], []);

        Assert.Equal(RouteVerdict.Proxy, rules.Verdict("WWW.example.com."));
        Assert.False(rules.IsEmpty);
        Assert.True(new NameRules([], [], []).IsEmpty);
    }

    [Fact]
    public async Task ADomainTheListNames_IsMarkedAsNamed()
    {
        var list = await new GeoConfigurator(_store, new NoFiles()).MaterializeDraftAsync(["proxy|domain:example.com"]);

        Assert.True(Assert.Single(list.Domains).Named);
    }

    [Fact]
    public void Settle_LeavesNoAddressInTwoSets()
    {
        var (proxy, direct, block) = RangeClaims.Settle(["0.0.0.0/0"], ["10.0.0.0/8", "10.1.2.3"], ["10.1.0.0/16", "192.168.1.0/24"]);

        Assert.Equal(["10.1.2.3"], direct.Where(entry => entry.StartsWith("10.1.", StringComparison.Ordinal)));
        Assert.Contains("192.168.1.0/24", block);
        foreach (var probe in new[] { "10.1.2.3", "10.1.2.4", "10.2.0.1", "192.168.1.9", "8.8.8.8" })
        {
            Assert.True(GeoIpRanges.TryToNumeric(System.Net.IPAddress.Parse(probe), out var value));
            Assert.Equal(1, new[] { proxy, direct, block }.Count(set => GeoIpRanges.Build(set).Contains(value)));
        }
    }

    [Fact]
    public void Settle_CarriesAnEntryOfAnotherFamilyThrough()
    {
        var (proxy, _, _) = RangeClaims.Settle(["fd00::/8", "10.0.0.0/8"], ["10.0.0.0/9"], []);

        Assert.Equal(["fd00::/8", "10.128.0.0/9"], proxy);
    }

    private static GeoDomain Named(string value) => new(GeoDomainKind.Domain, value, Named: true);

    private static GeoDomain Category(string value) => new(GeoDomainKind.Domain, value);
}
