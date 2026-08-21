using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The order a drag leaves a catalogue in: it is stored per catalogue and survives a re-read, and a new entry
/// lands after the ones already listed.
/// </summary>
public sealed class CatalogOrderTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-order-{Guid.NewGuid():N}.db");
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
            TryDelete(path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfigsKeepTheStoredOrder()
    {
        await _store.SaveConfigAsync("alpha", "[Interface]");
        await _store.SaveConfigAsync("bravo", "[Interface]");
        await _store.SaveConfigAsync("charlie", "[Interface]");

        await _store.SetConfigOrderAsync(["charlie", "alpha", "bravo"]);

        Assert.Equal(["charlie", "alpha", "bravo"], await _store.ListConfigNamesAsync());
    }

    [Fact]
    public async Task RoutingListsKeepTheStoredOrder()
    {
        await SaveListAsync("alpha");
        await SaveListAsync("bravo");
        await SaveListAsync("charlie");

        await _store.SetRoutingListOrderAsync(["charlie", "alpha", "bravo"]);

        Assert.Equal(["charlie", "alpha", "bravo"], await ListNamesAsync());
    }

    [Fact]
    public async Task NewRoutingListLandsLast()
    {
        await SaveListAsync("alpha");
        await SaveListAsync("bravo");
        await _store.SetRoutingListOrderAsync(["bravo", "alpha"]);

        await SaveListAsync("charlie");

        Assert.Equal(["bravo", "alpha", "charlie"], await ListNamesAsync());
    }

    private async Task SaveListAsync(string name)
    {
        await _store.SaveRoutingListAsync(new RoutingList(0, name, [], [], [], [], [], [], [], []));
    }

    private async Task<IReadOnlyList<string>> ListNamesAsync()
    {
        return (await _store.ListRoutingListSummariesAsync()).Select(summary => summary.Name).ToList();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
