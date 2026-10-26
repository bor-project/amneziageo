using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The channel the Windows agent takes from the settings of the machine: the one picked by hand, else the kind of the build.
/// </summary>
public sealed class UpdateChannelStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-channel-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

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
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NothingPicked_LeavesTheChannelOfTheBuild()
    {
        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.Equal(new AppSettings().UpdateChannel, settings.UpdateChannel);
    }

    [Fact]
    public async Task TheSwitchOfThePrereleases_StandsForTheBetas()
    {
        await _store.SetSettingAsync("allow-prerelease", "true");

        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.Equal(UpdateChannels.Beta, settings.UpdateChannel);
    }

    [Fact]
    public async Task AChannelPickedByHand_WinsOverTheSwitch()
    {
        await _store.SetSettingAsync("allow-prerelease", "true");
        Assert.True(await new SettingsStore(_store).SetAsync(AmneziaGeo.Ipc.SettingKeys.UpdateChannel, UpdateChannels.Test));

        var settings = await new SettingsStore(_store).LoadAsync();

        Assert.Equal(UpdateChannels.Test, settings.UpdateChannel);
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
