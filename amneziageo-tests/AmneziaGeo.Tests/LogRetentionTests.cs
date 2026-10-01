using AmneziaGeo.Dal;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// One pass cuts every log table to its cap: the three logs to the rows asked for, the runs to the last fifty.
/// </summary>
public sealed class LogRetentionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-log-retention-{Guid.NewGuid():N}");
    private SqliteLogStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteLogStore(Path.Combine(_root, "log.db"));
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.Dispose();
        _store.ClearPool();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task APass_CutsEveryLogToItsCap()
    {
        for (var index = 0; index < 7; index++)
        {
            _store.AppendAgent(index, 3, "agent", $"agent {index}");
        }

        for (var index = 0; index < 9; index++)
        {
            _store.AppendDns(index, 2, "dns", $"lookup {index}");
        }

        for (var index = 0; index < 6; index++)
        {
            _store.AppendRoute(index, $"route {index}");
        }

        for (var index = 0; index < LogRetention.RunsKept + 3; index++)
        {
            _store.AppendCheck(index, $"check {index}");
            _store.AppendProbe(index, $"probe target{index} over tunnel at noon");
        }

        await _store.FlushAsync();

        var pruned = await LogRetention.PruneAsync(_store, 5);

        Assert.Equal(new LogPruned(2, 4, 1), pruned);
        Assert.Equal(7, pruned.Total);
        Assert.Equal(5, await _store.CountAsync(SqliteLogStore.AgentTable, null, null));
        Assert.Equal(5, await _store.CountAsync(SqliteLogStore.DnsTable, null, null));
        Assert.Equal(5, await _store.CountAsync(SqliteLogStore.RoutesTable, null, null));
        Assert.Equal(LogRetention.RunsKept, await _store.CountAsync(SqliteLogStore.ChecksTable, null, null));
        Assert.Equal(LogRetention.RunsKept, await _store.CountAsync(SqliteLogStore.ProbeTable, null, null));
        var newest = (await _store.QueryAsync(SqliteLogStore.DnsTable, null, 1, null, null)).Rows;
        Assert.Equal("lookup 8", Assert.Single(newest).Message);
    }

    [Fact]
    public async Task LogsWithinTheirCaps_LoseNothing()
    {
        _store.AppendAgent(1, 3, "agent", "kept");
        _store.AppendDns(1, 2, "dns", "kept");
        await _store.FlushAsync();

        var pruned = await LogRetention.PruneAsync(_store);

        Assert.Equal(0, pruned.Total);
        Assert.Equal(1, await _store.CountAsync(SqliteLogStore.AgentTable, null, null));
        Assert.Equal(1, await _store.CountAsync(SqliteLogStore.DnsTable, null, null));
    }
}
