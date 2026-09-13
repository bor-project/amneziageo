using System.Net;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Память кэша возвращает решение приложения и имени только при тех же правилах.
/// </summary>
public sealed class StoredRouteMemoryTests : IAsyncLifetime
{
    private const string Tunnel = "home";
    private const string TelegramAddress = "149.154.167.50";
    private const string TelegramName = "149.154.167.99";
    private const string TelegramApp = "dir=%APPDATA%\\Telegram Desktop";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-memory-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    // Считает, что кэш уводит в туннель.
    private sealed class TunnelApplier : IRouteApplier
    {
        public List<string> Tunneled { get; } = [];

        public int Generation => 0;

        public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
        {
            outId = 0;
            inId = 0;
            generation = 0;
            return true;
        }

        public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
        {
            outId = 0;
            inId = 0;
            generation = 0;
            return true;
        }

        public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
        {
            interfaceIndex = 7;
            return true;
        }

        public void RemoveRoute(IPAddress address, uint interfaceIndex)
        {
        }

        public bool TryTunnel(IPAddress address)
        {
            Tunneled.Add(address.ToString());
            return true;
        }

        public IReadOnlyList<IPAddress> AddTunnel(IReadOnlyList<IPAddress> addresses)
        {
            Tunneled.AddRange(addresses.Select(address => address.ToString()));
            return addresses;
        }

        public void RemoveTunnel(IReadOnlyCollection<IPAddress> addresses)
        {
        }

        public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
        {
        }
    }

    // Живых соединений нет.
    private sealed class IdleLive : ILiveDestinations
    {
        public LiveDestinations Snapshot() => new([], []);
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
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UnderTheSameRules_TheAppAndTheNameStay()
    {
        await Memory([TelegramApp], [Domain("telegram.org")]).SaveAsync(Remembered(), CancellationToken.None);

        var routes = await Memory([TelegramApp], [Domain("telegram.org")]).LoadAsync(CancellationToken.None);

        Assert.Collection(routes,
            route => Assert.True(route.ByApp),
            route => Assert.True(route.ByName));
    }

    [Fact]
    public async Task ATunnelThatLostItsRules_DecidesEveryAddressByTheRanges()
    {
        // Туннель вёз всё и стал нейтральным: правила приложения и имени уехали на другой сервер.
        await Memory([TelegramApp], [Domain("telegram.org")]).SaveAsync(Remembered(), CancellationToken.None);

        var routes = await Memory([], []).LoadAsync(CancellationToken.None);

        Assert.All(routes, route => Assert.False(route.ByApp || route.ByName));
        Assert.Equal([TelegramAddress, TelegramName], routes.Select(route => route.Address));
    }

    [Fact]
    public async Task ADomainRuleMovedToAnotherBucket_DropsTheVerdictItsNameSettled()
    {
        await Memory([TelegramApp], [Domain("telegram.org")]).SaveAsync(Remembered(), CancellationToken.None);

        var routes = await Memory([TelegramApp], [], direct: [Domain("telegram.org")]).LoadAsync(CancellationToken.None);

        Assert.DoesNotContain(routes, route => route.ByName);
    }

    [Fact]
    public async Task TheOrderAndCaseOfTheRules_DoNotCount()
    {
        await Memory([TelegramApp, "svc=InstallService"], [Domain("a.example"), Domain("b.example")]).SaveAsync(Remembered(), CancellationToken.None);

        var routes = await Memory(["svc=installservice", TelegramApp], [Domain("b.example"), Domain("A.example")]).LoadAsync(CancellationToken.None);

        Assert.Contains(routes, route => route.ByApp);
    }

    [Fact]
    public async Task ARestoredAppAddress_TakesTheTunnelUnderTheRuleThatClaimedIt()
    {
        await Memory([TelegramApp], []).SaveAsync(Remembered(), CancellationToken.None);
        var applier = new TunnelApplier();

        await Warm(applier, await Memory([TelegramApp], []).LoadAsync(CancellationToken.None));

        Assert.Contains(TelegramAddress, applier.Tunneled);
    }

    [Fact]
    public async Task ARestoredAppAddress_TakesNoTunnelOnceTheAppRuleIsGone()
    {
        await Memory([TelegramApp], []).SaveAsync(Remembered(), CancellationToken.None);
        var applier = new TunnelApplier();

        await Warm(applier, await Memory([], []).LoadAsync(CancellationToken.None));

        Assert.Empty(applier.Tunneled);
    }

    private StoredRouteMemory Memory(IReadOnlyList<string> apps, IReadOnlyList<GeoDomain> proxy, IReadOnlyList<GeoDomain>? direct = null)
    {
        return new StoredRouteMemory(_store, Tunnel, apps, proxy, direct ?? [], []);
    }

    // Поднимает раздельный туннель без диапазонов и забирает в него память.
    private static async Task Warm(TunnelApplier applier, IReadOnlyList<RememberedRoute> routes)
    {
        var cache = new RoutingCache(applier, new IdleLive(), true, [], [], [], 300, NullLogger<RoutingCache>.Instance, hot: 8);
        cache.Restore(routes);
        await cache.WarmAsync(CancellationToken.None);
    }

    private static IReadOnlyList<RememberedRoute> Remembered()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new RememberedRoute(TelegramAddress, nameof(RouteVerdict.None), false, true, now),
            new RememberedRoute(TelegramName, nameof(RouteVerdict.Proxy), true, false, now.AddSeconds(-1)),
        ];
    }

    private static GeoDomain Domain(string value)
    {
        return new GeoDomain(GeoDomainKind.Domain, value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
