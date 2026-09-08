using System.Globalization;

using AmneziaGeo.Dal;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Releasing the pooled connections of a store that is in use: the work in flight has to finish instead of
/// dying on a connection closed under it, and a suspended store has to let its file be replaced.
/// </summary>
public sealed class StoreSuspendTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-suspend-{Guid.NewGuid():N}.db");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ageo-suspend-log-{Guid.NewGuid():N}.db");
    private readonly string _sparePath = Path.Combine(Path.GetTempPath(), $"ageo-suspend-spare-{Guid.NewGuid():N}.db");

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _logPath, _sparePath })
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                TryDelete(file);
            }
        }
    }

    [Fact]
    public async Task StateStore_PoolReleasedWhileInUse_LeavesTheWorkGoing()
    {
        var store = new SqliteStateStore(_dbPath);
        await store.InitializeAsync();

        using var done = new CancellationTokenSource();
        var clearing = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                store.ClearPool();
            }
        });

        var writer = Task.Run(async () =>
        {
            for (var round = 0; round < 500; round++)
            {
                await store.SetSettingAsync("probe", round.ToString(CultureInfo.InvariantCulture));
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 500; round++)
            {
                await store.GetSettingAsync("probe");
                await store.ListConfigNamesAsync();
            }
        }));

        await Task.WhenAll(readers.Append(writer));
        done.Cancel();
        await clearing;
        store.ClearPool();
    }

    [Fact]
    public async Task LogStore_PoolReleasedWhileInUse_LeavesTheWorkGoing()
    {
        using var store = new SqliteLogStore(_logPath);
        await store.InitializeAsync();

        using var done = new CancellationTokenSource();
        var clearing = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                store.ClearPool();
            }
        });

        var writer = Task.Run(async () =>
        {
            for (var round = 0; round < 200; round++)
            {
                store.AppendAgent(DateTimeOffset.Now.ToUnixTimeMilliseconds(), 3, "test", $"row {round}");
                await store.FlushAsync();
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 200; round++)
            {
                await store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null);
                await store.CountAsync(SqliteLogStore.AgentTable, null, null);
            }
        }));

        await Task.WhenAll(readers.Append(writer));
        done.Cancel();
        await clearing;
    }

    [Fact]
    public async Task Suspend_HoldsNewWorkOff_UntilItIsReleased()
    {
        var store = new SqliteStateStore(_dbPath);
        await store.InitializeAsync();
        await store.SetSettingAsync("probe", "value");

        var suspension = await store.SuspendAsync();
        var pending = store.GetSettingAsync("probe");
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(pending.IsCompleted);

        await suspension.DisposeAsync();
        Assert.Equal("value", await pending);
        store.ClearPool();
    }

    [Fact]
    public async Task Suspend_LetsTheDatabaseFileBeReplaced()
    {
        var store = new SqliteStateStore(_dbPath);
        await store.InitializeAsync();
        await store.SetSettingAsync("probe", "before");

        var spare = new SqliteStateStore(_sparePath);
        await spare.InitializeAsync();
        await spare.SetSettingAsync("probe", "after");
        spare.ClearPool();

        var suspension = await store.SuspendAsync();
        await using (suspension)
        {
            File.Move(_sparePath, _dbPath, overwrite: true);
        }

        Assert.Equal("after", await store.GetSettingAsync("probe"));
        store.ClearPool();
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
