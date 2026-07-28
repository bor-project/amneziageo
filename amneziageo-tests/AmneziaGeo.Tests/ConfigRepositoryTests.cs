using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
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
    public async Task ListReferencingProfiles_ReturnsProfilesBoundToConfig()
    {
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SaveProfileAsync(new Profile("home", "srv"));
        await _store.SaveProfileAsync(new Profile("work", "srv"));
        await _store.SaveProfileAsync(new Profile("other", "elsewhere"));

        var referencing = await _repo.ListReferencingProfilesAsync("srv");

        Assert.Equal(new[] { "home", "work" }, referencing);
    }

    [Fact]
    public async Task Remove_Throws_WhenConfigStillBoundToAProfile()
    {
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SaveProfileAsync(new Profile("home", "srv"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.RemoveAsync("srv"));

        Assert.True(await _store.ConfigExistsAsync("srv"));
    }

    [Fact]
    public async Task Remove_Succeeds_WhenNoProfileReferencesConfig()
    {
        await _store.SaveConfigAsync("srv", "conf");
        await _store.SaveProfileAsync(new Profile("home", "other"));

        await _repo.RemoveAsync("srv");

        Assert.False(await _store.ConfigExistsAsync("srv"));
    }

    [Fact]
    public async Task Rename_CascadesConfigNameToReferencingProfiles()
    {
        await _store.SaveConfigAsync("old", "conf");
        await _store.SaveProfileAsync(new Profile("home", "old"));
        await _store.SaveProfileAsync(new Profile("work", "old"));
        await _store.SaveProfileAsync(new Profile("other", "keep"));

        await _repo.RenameAsync("old", "new");

        Assert.Equal("new", (await _store.GetProfileAsync("home"))!.Config);
        Assert.Equal("new", (await _store.GetProfileAsync("work"))!.Config);
        Assert.Equal("keep", (await _store.GetProfileAsync("other"))!.Config);
        Assert.True(await _store.ConfigExistsAsync("new"));
        Assert.False(await _store.ConfigExistsAsync("old"));
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
