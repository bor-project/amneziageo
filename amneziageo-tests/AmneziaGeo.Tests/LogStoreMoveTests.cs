using AmneziaGeo.Dal;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rows of the name subsystem an earlier version left in the agent log are handed to the resolver log once,
/// in the order they were written.
/// </summary>
public sealed class LogStoreMoveTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-log-move-{Guid.NewGuid():N}");
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
    public async Task TheRowsOfTheNameSubsystem_LeaveTheAgentLogInTheirOrder()
    {
        _store.AppendAgent(1000, 2, "DnsProxy", "first lookup");
        _store.AppendAgent(2000, 3, "TunnelRunner", "the tunnel is up");
        _store.AppendAgent(3000, 4, "DomainTracker", "second lookup");
        _store.AppendAgent(4000, 3, null, "a row without a source");

        var moved = await _store.MoveToDnsAsync(ResolverLog.Sources);

        Assert.Equal(2, moved);
        var resolver = (await _store.QueryAsync(SqliteLogStore.DnsTable, null, 10, null, null)).Rows;
        Assert.Equal(["second lookup", "first lookup"], resolver.Select(row => row.Message));
        Assert.Equal([3000L, 1000L], resolver.Select(row => row.UnixMs));
        Assert.Equal(["WRN", "DBG"], resolver.Select(row => row.Level));
        Assert.Equal(["DomainTracker", "DnsProxy"], resolver.Select(row => row.Source));
        var agent = (await _store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null)).Rows;
        Assert.Equal(["a row without a source", "the tunnel is up"], agent.Select(row => row.Message));
    }

    [Fact]
    public async Task ASecondPass_MovesNothing()
    {
        _store.AppendAgent(1000, 2, "DnsProxy", "a lookup");
        await _store.MoveToDnsAsync(ResolverLog.Sources);

        var moved = await _store.MoveToDnsAsync(ResolverLog.Sources);

        Assert.Equal(0, moved);
        Assert.Single((await _store.QueryAsync(SqliteLogStore.DnsTable, null, 10, null, null)).Rows);
    }

    [Fact]
    public async Task WithNoSourcesNamed_NothingMoves()
    {
        _store.AppendAgent(1000, 2, "DnsProxy", "a lookup");

        var moved = await _store.MoveToDnsAsync([]);

        Assert.Equal(0, moved);
        await _store.FlushAsync();
        Assert.Single((await _store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null)).Rows);
    }

    [Theory]
    [InlineData(SqliteLogStore.AgentTable, true)]
    [InlineData(SqliteLogStore.DnsTable, true)]
    [InlineData(SqliteLogStore.RoutesTable, false)]
    [InlineData(SqliteLogStore.ChecksTable, false)]
    [InlineData(SqliteLogStore.ProbeTable, false)]
    public void OnlyTheAgentAndTheResolverLog_CarryALevel(string table, bool leveled)
    {
        Assert.Equal(leveled, SqliteLogStore.IsLeveled(table));
    }
}
