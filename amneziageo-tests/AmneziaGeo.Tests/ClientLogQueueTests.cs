using System.Collections.Concurrent;
using System.Diagnostics;

using AmneziaGeo.Dal;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Rows of the tray and the app window reach the agent through their link, and only the ones it does not take stay in the per-user file.
/// </summary>
public sealed class ClientLogQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-client-log-{Guid.NewGuid():N}");

    private string DatabasePath => Path.Combine(_root, "logs", "log.db");

    [Fact]
    public void ARow_TravelsAsRequestArguments()
    {
        var row = new ClientLogRow(1_789_000_000_123, 5, ClientLog.UiSource, "GUI crashed\nat line 1");

        Assert.True(ClientLogRow.TryRead(row.Args(), out var read));
        Assert.Equal(row, read);
    }

    [Fact]
    public void ArgumentsThatAreNotARow_AreRejected()
    {
        Assert.False(ClientLogRow.TryRead(["reported by hand"], out _));
        Assert.False(ClientLogRow.TryRead(["line", "4", "agent", "1"], out _));
        Assert.False(ClientLogRow.TryRead(["line", "2", ClientLog.TraySource, "1"], out _));
        Assert.False(ClientLogRow.TryRead(["line", "4", ClientLog.TraySource, "soon"], out _));
    }

    [Fact]
    public void QueuedRows_ReachTheLinkInOrder_AndLeaveNoFile()
    {
        var taken = new ConcurrentQueue<string>();
        using (var queue = new ClientLogQueue(DatabasePath))
        {
            queue.Append(Row("first"));
            queue.Append(Row("second"));
            queue.Attach(row =>
            {
                taken.Enqueue(row.Message);
                return Task.FromResult(true);
            });
            queue.Append(Row("third"));
            queue.Flush(2000);
        }

        Assert.Equal(new[] { "first", "second", "third" }, taken.ToArray());
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task RowsTheLinkRefuses_LandInTheFile_WithoutWaitingOut()
    {
        var clock = Stopwatch.StartNew();
        using (var queue = new ClientLogQueue(DatabasePath))
        {
            queue.Attach(_ => Task.FromResult(false));
            queue.Append(Row("refused"));
            queue.Flush(5000);
        }

        Assert.True(clock.ElapsedMilliseconds < 4000, $"flush took {clock.ElapsedMilliseconds} ms");

        using var store = new SqliteLogStore(DatabasePath);
        await store.InitializeAsync();
        var page = await store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null);
        var row = Assert.Single(page.Rows);
        Assert.Equal("refused", row.Message);
        Assert.Equal(ClientLog.TraySource, row.Source);
        store.ClearPool();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ClientLogRow Row(string message)
    {
        return new ClientLogRow(DateTimeOffset.Now.ToUnixTimeMilliseconds(), 4, ClientLog.TraySource, message);
    }
}
