using AmneziaGeo.Dal;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The several-servers mode: off until the switch turns it on, and off wherever the platform raises one tunnel
/// at a time, so everything below it stays the way it was before the mode existed.
/// </summary>
public sealed class MultiServerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-multiserver-{Guid.NewGuid():N}.db");
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
    public void Snapshot_LeavesTheModeOff()
    {
        var snapshot = new StatusSnapshot("1.0", null, []);

        Assert.False(snapshot.MultiServer);
        Assert.False(snapshot.MultiTunnel);
    }

    [Fact]
    public async Task Settings_LeaveTheModeOffWhenNothingIsStored()
    {
        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.False(settings.MultiServer);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("maybe", false)]
    public async Task Settings_ReadWhatTheSwitchStored(string stored, bool expected)
    {
        await _store.SetSettingAsync(SettingKeys.MultiServer, stored);

        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.Equal(expected, settings.MultiServer);
    }

    [Fact]
    public async Task Settings_KeepTheModeApartFromAutoSwitching()
    {
        await _store.SetSettingAsync(SettingKeys.MultiServer, "on");

        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.True(settings.MultiServer);
        Assert.False(settings.FailoverEnabled);
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
