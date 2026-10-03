using AmneziaGeo.Dal;
using AmneziaGeo.Windows.App;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The journal at the level error stores errors alone, and the rows of level info and above still reach the
/// memory the support archive is built from.
/// </summary>
public sealed class RecentRowsSinkTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-recent-{Guid.NewGuid():N}");
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
    public void TheFloor_IsTheLevelInForceAndInfoAtTheHighest()
    {
        var level = new LogLevelController();
        Assert.Equal(LogEventLevel.Information, level.Floor.MinimumLevel);

        level.Set("debug");
        Assert.Equal((LogEventLevel.Debug, LogEventLevel.Debug), (level.Floor.MinimumLevel, level.Switch.MinimumLevel));

        level.Set("error");
        Assert.Equal((LogEventLevel.Information, LogEventLevel.Error), (level.Floor.MinimumLevel, level.Switch.MinimumLevel));

        level.Set("none");
        Assert.Equal(LogEventLevel.Information, level.Floor.MinimumLevel);
        Assert.False(level.Captures(5));
    }

    [Fact]
    public async Task ARowTheLevelDoesNotStore_IsStillKeptInMemory()
    {
        var level = new LogLevelController();
        level.Set("error");
        var recent = new RecentLog();
        var sink = new LogDbSink(_store, recent, level);

        sink.Emit(Event("ConfigRunner", LogEventLevel.Debug, "a debug row"));
        sink.Emit(Event("ConfigRunner", LogEventLevel.Information, "connected through srv"));
        sink.Emit(Event("ConfigRunner", LogEventLevel.Error, "the tunnel session stopped"));
        await _store.FlushAsync();

        Assert.Equal(["connected through srv", "the tunnel session stopped"], recent.Snapshot().Select(row => row.Message));
        var stored = await _store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null);
        Assert.Equal(["the tunnel session stopped"], stored.Rows.Select(row => row.Message));
    }

    private static LogEvent Event(string source, LogEventLevel level, string message)
    {
        var template = new MessageTemplateParser().Parse(message);
        var properties = new[] { new LogEventProperty("Source", new ScalarValue(source)) };
        return new LogEvent(DateTimeOffset.Now, level, null, template, properties);
    }
}
