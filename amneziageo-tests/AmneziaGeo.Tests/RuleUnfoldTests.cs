using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Each rule of a list is unfolded apart, in the order of the list, so its share of the route table is known: a range
/// weighs one, a name eight, an application nothing.
/// </summary>
public sealed class RuleUnfoldTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-unfold-{Guid.NewGuid():N}.db");
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
    public async Task EveryRule_IsUnfoldedApartInItsOrder()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        var unfolded = await geo.UnfoldTokensAsync(
            ["proxy|cidr:10.0.0.0/8", "direct|domain:ya.ru", "proxy|app:pkg=org.example", "block|cidr:192.0.2.0/24"]);

        Assert.Equal([1, 8, 0, 1], unfolded.Select(rule => rule.Weight));
        Assert.Equal(["10.0.0.0/8"], unfolded[0].Routes);
        Assert.Equal([new GeoDomain(GeoDomainKind.Domain, "ya.ru", Named: true)], unfolded[1].Domains);
        Assert.Equal(RouteRole.Direct, unfolded[1].Rule.Role);
        Assert.Equal(RouteRole.Block, unfolded[3].Rule.Role);
    }

    [Fact]
    public async Task ATokenThatIsNoRule_IsLeftOut()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        var unfolded = await geo.UnfoldTokensAsync(["proxy|cidr:10.0.0.0/8", "proxy|"]);

        Assert.Single(unfolded);
    }
}
