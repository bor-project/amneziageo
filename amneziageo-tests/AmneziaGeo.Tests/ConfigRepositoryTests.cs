using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Config delete/rename invariants against a real SQLite store.
/// </summary>
public sealed class ConfigRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-test-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;
    private ConfigRepository _repo = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        _repo = new ConfigRepository(_store, new ServiceManager());
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
    public async Task Remove_DropsTheConfig()
    {
        await _store.SaveConfigAsync("srv", "conf");

        await _repo.RemoveAsync("srv");

        Assert.False(await _store.ConfigExistsAsync("srv"));
    }

    [Fact]
    public async Task Rename_CarriesTheSelectionAndTheTunnelState()
    {
        await _store.SaveConfigAsync("old", "conf");
        await _store.SetSettingAsync(StateKeys.SelectedTarget, "old");
        await _store.SaveTunnelStateAsync(new TunnelState("old", ConnectionStatus.Connected, DateTimeOffset.UtcNow));

        await _repo.RenameAsync("old", "new");
        await ConfigRename.CarryAsync(_store, "old", "new");

        Assert.Equal("new", await _store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.NotNull(await _store.GetTunnelStateAsync("new"));
        Assert.Null(await _store.GetTunnelStateAsync("old"));
    }

    [Fact]
    public async Task Rename_CarriesTransportAndDnsToNewName()
    {
        await _store.SaveConfigAsync("old", "conf");
        await _store.SetConfigTransportAsync(new ConfigTransport("old", true, "ws.example", 8443));
        await _store.SetConfigDnsAsync(new ConfigDns("old", "1.1.1.1"));

        await _repo.RenameAsync("old", "new");

        var transport = await _store.GetConfigTransportAsync("new");
        Assert.NotNull(transport);
        Assert.True(transport!.UseWebSocket);
        Assert.Equal(8443, transport.WebSocketPort);
        Assert.Null(await _store.GetConfigTransportAsync("old"));

        var dns = await _store.GetConfigDnsAsync("new");
        Assert.NotNull(dns);
        Assert.Equal("1.1.1.1", dns!.Servers);
    }

    [Fact]
    public async Task RemoveRoutingList_ClearsTheSelectionThatPointedAtIt()
    {
        var id = await _store.SaveRoutingListAsync(new RoutingList(0, "list", [], [], [], [], [], [], [], []));
        await _store.SetSelectedRoutingListAsync(id);

        await _store.RemoveRoutingListAsync(id);

        Assert.Null(await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task Rename_ToANameNoFileCouldCarry_IsTakenAsItIs()
    {
        await _store.SaveConfigAsync("srv", "conf");

        await _repo.RenameAsync("srv", "дом/офис: 2");

        Assert.True(await _store.ConfigExistsAsync("дом/офис: 2"));
        Assert.True(TunnelDevice.IsAcceptable(TunnelDevice.NameOf("дом/офис: 2")));
    }

    [Fact]
    public async Task Rename_ToABlankName_IsRefused()
    {
        await _store.SaveConfigAsync("srv", "conf");

        await Assert.ThrowsAsync<ArgumentException>(() => _repo.RenameAsync("srv", "  "));
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
