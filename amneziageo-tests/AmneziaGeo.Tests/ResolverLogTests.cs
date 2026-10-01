using AmneziaGeo.Dal;
using AmneziaGeo.Windows.App;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the name subsystem writes lands in the resolver log, and the agent log keeps the rest.
/// </summary>
public sealed class ResolverLogTests : IAsyncLifetime
{
    private const string ResolverTable = "dns";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-resolver-log-{Guid.NewGuid():N}");
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

    [Theory]
    [InlineData("DnsProxy")]
    [InlineData("DomainTracker")]
    [InlineData("DnsHealthService")]
    [InlineData("DnsAnswerLearner")]
    [InlineData("DnsConfigurator")]
    [InlineData("AppDnsTracker")]
    [InlineData("LocalDohGuard")]
    [InlineData("FleetLentNames")]
    public async Task ARowOfTheNameSubsystem_GoesToTheResolverLog(string source)
    {
        Emit(source, LogEventLevel.Debug, "example.org A/IPv4: answered from cache");

        var row = Assert.Single(await RowsAsync(ResolverTable));
        Assert.Equal(source, row.Source);
        Assert.Equal("DBG", row.Level);
        Assert.Empty(await RowsAsync(SqliteLogStore.AgentTable));
    }

    [Theory]
    [InlineData("TunnelRunner")]
    [InlineData("ConfigRunner")]
    [InlineData("agent")]
    public async Task ARowOfAnythingElse_StaysInTheAgentLog(string source)
    {
        Emit(source, LogEventLevel.Information, "the tunnel is up");

        Assert.Equal(source, Assert.Single(await RowsAsync(SqliteLogStore.AgentTable)).Source);
        Assert.Empty(await RowsAsync(ResolverTable));
    }

    [Fact]
    public async Task TheResolverLog_IsReadFromALevelUp()
    {
        Emit("DnsProxy", LogEventLevel.Debug, "example.org A/IPv4: answered from cache");
        Emit("DnsProxy", LogEventLevel.Warning, "the resolver in the tunnel stopped answering");
        await _store.FlushAsync();

        var page = await _store.QueryAsync(ResolverTable, null, 10, 4, null);

        Assert.Equal("WRN", Assert.Single(page.Rows).Level);
    }

    [Fact]
    public async Task TheResolverLog_IsSearchedBySourceToo()
    {
        Emit("DnsProxy", LogEventLevel.Information, "DNS is now handled here");
        Emit("DomainTracker", LogEventLevel.Information, "example.org: 2 new addresses now go through the tunnel");
        await _store.FlushAsync();

        var page = await _store.QueryAsync(ResolverTable, null, 10, null, "DomainTracker");

        Assert.Equal("DomainTracker", Assert.Single(page.Rows).Source);
        Assert.Equal(1, await _store.CountAsync(ResolverTable, null, "DomainTracker"));
    }

    [Fact]
    public async Task ClearingTheResolverLog_LeavesTheAgentLog()
    {
        Emit("DnsProxy", LogEventLevel.Information, "DNS is now handled here");
        Emit("TunnelRunner", LogEventLevel.Information, "the tunnel is up");

        await _store.ClearAsync(ResolverTable);

        Assert.Empty(await RowsAsync(ResolverTable));
        Assert.Single(await RowsAsync(SqliteLogStore.AgentTable));
    }

    [Fact]
    public async Task TheResolverLog_IsCutToItsOwnNewestRows()
    {
        for (var index = 0; index < 5; index++)
        {
            Emit("DnsProxy", LogEventLevel.Debug, $"lookup {index}");
        }

        Emit("TunnelRunner", LogEventLevel.Information, "the tunnel is up");
        await _store.FlushAsync();

        var removed = await _store.PruneAsync(ResolverTable, 2);

        Assert.Equal(3, removed);
        Assert.Equal(["lookup 4", "lookup 3"], (await RowsAsync(ResolverTable)).Select(row => row.Message));
        Assert.Single(await RowsAsync(SqliteLogStore.AgentTable));
    }

    [Fact]
    public async Task TheResolverLog_IsRenderedWithItsLevelAndSource()
    {
        Emit("DnsProxy", LogEventLevel.Warning, "the resolver in the tunnel stopped answering");
        await _store.FlushAsync();

        var text = await _store.RenderAsync(ResolverTable, LogFormat.Render);

        Assert.Contains("[WRN] DnsProxy the resolver in the tunnel stopped answering", text, StringComparison.Ordinal);
    }

    // Hands the sink one event the way the host does: the rendered message, its level and the source set on it.
    private void Emit(string source, LogEventLevel level, string message)
    {
        var template = new MessageTemplateParser().Parse(message);
        var properties = new[] { new LogEventProperty("Source", new ScalarValue(source)) };
        new LogDbSink(_store).Emit(new LogEvent(DateTimeOffset.Now, level, null, template, properties));
    }

    private async Task<IReadOnlyList<LogRow>> RowsAsync(string table)
    {
        await _store.FlushAsync();
        return (await _store.QueryAsync(table, null, 100, null, null)).Rows;
    }
}
