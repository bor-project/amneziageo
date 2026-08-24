using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which routing list a configuration routes through, against a real SQLite store.
/// </summary>
public sealed class RoutingBindingTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-bind-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

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
    public async Task Resolve_FollowsTheDefaultListWithoutABinding()
    {
        var id = await ListAsync("default");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetSelectedRoutingListAsync(id);

        Assert.Equal(id, await RoutingBinding.ResolveAsync(_store, "srv"));
    }

    [Fact]
    public async Task Resolve_PrefersTheConfigsOwnList()
    {
        var fallback = await ListAsync("default");
        var own = await ListAsync("own");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetSelectedRoutingListAsync(fallback);
        await _store.SetConfigRoutingAsync(new ConfigRouting("srv", own));

        Assert.Equal(own, await RoutingBinding.ResolveAsync(_store, "srv"));
    }

    [Fact]
    public async Task Resolve_BoundToNoList_LeavesTheDefaultBehind()
    {
        var fallback = await ListAsync("default");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetSelectedRoutingListAsync(fallback);
        await _store.SetConfigRoutingAsync(new ConfigRouting("srv", null));

        Assert.Null(await RoutingBinding.ResolveAsync(_store, "srv"));
    }

    [Fact]
    public async Task Migration_StampsTheConfigsThatPredateIt()
    {
        var id = await ListAsync("list");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetSelectedRoutingListAsync(id);

        await RerunStampAsync();

        var binding = await _store.GetConfigRoutingAsync("srv");
        Assert.NotNull(binding);
        Assert.Equal(id, binding!.RoutingListId);
    }

    [Fact]
    public async Task Migration_LeavesAnUnboundConfigOnTheDefault()
    {
        var first = await ListAsync("first");
        await _store.SetSelectedRoutingListAsync(first);
        await _store.SaveConfigAsync("srv", "conf");

        var second = await ListAsync("second");
        await _store.SetSelectedRoutingListAsync(second);

        Assert.Null(await _store.GetConfigRoutingAsync("srv"));
        Assert.Equal(second, await RoutingBinding.ResolveAsync(_store, "srv"));
    }

    [Fact]
    public async Task RemoveRoutingList_ClearsTheBindingsThatNamedIt()
    {
        var id = await ListAsync("list");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetConfigRoutingAsync(new ConfigRouting("srv", id));

        await _store.RemoveRoutingListAsync(id);

        var binding = await _store.GetConfigRoutingAsync("srv");
        Assert.NotNull(binding);
        Assert.Null(binding!.RoutingListId);
    }

    [Fact]
    public async Task Rename_CarriesTheBinding()
    {
        var id = await ListAsync("list");
        await _store.SaveConfigAsync("old", "conf");
        await _store.SetConfigRoutingAsync(new ConfigRouting("old", id));

        await ConfigRename.CarryAsync(_store, "old", "new");

        Assert.Null(await _store.GetConfigRoutingAsync("old"));
        Assert.Equal(id, (await _store.GetConfigRoutingAsync("new"))?.RoutingListId);
    }

    [Fact]
    public async Task AnyConfigRouting_CountsOnlyABoundConfig()
    {
        var id = await ListAsync("list");
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SetConfigRoutingAsync(new ConfigRouting("srv", null));
        Assert.False(await _store.AnyConfigRoutingAsync());

        await _store.SetConfigRoutingAsync(new ConfigRouting("srv", id));
        Assert.True(await _store.AnyConfigRoutingAsync());
    }

    private Task<long> ListAsync(string name) =>
        _store.SaveRoutingListAsync(new RoutingList(0, name, [], [], [], [], [], [], [], []));

    // Clears the marker so the next schema step stamps again, as it did on a store that predates the binding.
    private async Task RerunStampAsync()
    {
        await _store.SetSettingAsync(StateKeys.ConfigRoutingStamped, string.Empty);
        await _store.InitializeAsync();
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
