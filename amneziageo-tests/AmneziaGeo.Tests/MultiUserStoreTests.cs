using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Machine-vs-user routing of the composite store and the machine-store split migration.
/// </summary>
public sealed class MultiUserStoreTests : IAsyncLifetime
{
    private readonly string _machinePath = Path.Combine(Path.GetTempPath(), $"ageo-machine-{Guid.NewGuid():N}.db");
    private readonly string _userPath = Path.Combine(Path.GetTempPath(), $"ageo-user-{Guid.NewGuid():N}.db");
    private SqliteStateStore _machine = null!;
    private SqliteStateStore _user = null!;
    private ScopedStateStore _scoped = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _machine = new SqliteStateStore(_machinePath);
        _user = new SqliteStateStore(_userPath);
        await _machine.InitializeAsync();
        await _user.InitializeAsync();
        _scoped = new ScopedStateStore(_machine, _user);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _machine.ClearPool();
        _user.ClearPool();
        foreach (var path in new[] { _machinePath, _userPath })
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task GeoSources_RouteToMachineStore()
    {
        await _scoped.SaveGeoSourceAsync(new GeoSource("geoip-1", "geoip", "https://example/geoip", 0));

        Assert.Single(await _machine.ListGeoSourcesAsync());
        Assert.Empty(await _user.ListGeoSourcesAsync());
    }

    [Fact]
    public async Task MachineSettingKey_RoutesToMachine_UserKeyToUser()
    {
        await _scoped.SetSettingAsync("log-level", "debug");
        await _scoped.SetSettingAsync("selected-target", "home");

        Assert.Equal("debug", await _machine.GetSettingAsync("log-level"));
        Assert.Null(await _user.GetSettingAsync("log-level"));
        Assert.Equal("home", await _user.GetSettingAsync("selected-target"));
        Assert.Null(await _machine.GetSettingAsync("selected-target"));

        Assert.Equal("debug", await _scoped.GetSettingAsync("log-level"));
        Assert.Equal("home", await _scoped.GetSettingAsync("selected-target"));
    }

    [Fact]
    public async Task TheOriginOfARoutingList_LivesWithTheUser()
    {
        var list = await _scoped.SaveRoutingListAsync(new RoutingList(0, "Unblock", [], [], [], [], [], [], [], [], []));

        await _scoped.SetRoutingListOriginAsync(new RoutingListOrigin(list, "0f8fad5b-d9cb-469f-a165-70867728950e", "vpn-awg1-office", DateTimeOffset.UnixEpoch));

        Assert.Equal("vpn-awg1-office", Assert.Single(await _user.ListRoutingListOriginsAsync()).Source);
        Assert.Equal("vpn-awg1-office", (await _scoped.GetRoutingListOriginAsync(list))!.Source);
        Assert.Equal("vpn-awg1-office", Assert.Single(await _scoped.ListRoutingListSummariesAsync()).Source);
        Assert.Empty(await _machine.ListRoutingListOriginsAsync());
    }

    [Fact]
    public async Task TheAddressesTheSourcesTookFromServers_LiveWithTheMachine()
    {
        await _scoped.SetSettingAsync(StateKeys.OfferedSourceAddresses, "amneziageo\thttps://geo.example/geoip.dat");

        Assert.Equal("amneziageo\thttps://geo.example/geoip.dat", await _machine.GetSettingAsync(StateKeys.OfferedSourceAddresses));
        Assert.Null(await _user.GetSettingAsync(StateKeys.OfferedSourceAddresses));
    }

    [Fact]
    public async Task GetSettings_MergesMachineAndUser()
    {
        await _scoped.SetSettingAsync("log-level", "trace");
        await _scoped.SetSettingAsync("selected-target", "work");

        var all = await _scoped.GetSettingsAsync();

        Assert.Equal("trace", all["log-level"]);
        Assert.Equal("work", all["selected-target"]);
    }

    [Fact]
    public async Task Config_RoutesToUserStore()
    {
        await _scoped.SaveConfigAsync("srv", "conf");

        Assert.True(await _user.ConfigExistsAsync("srv"));
        Assert.False(await _machine.ConfigExistsAsync("srv"));
    }

    [Fact]
    public async Task SplitLegacy_CopiesGeoAndMachineSettings_Once()
    {
        await _user.SaveGeoSourceAsync(new GeoSource("geosite-1", "geosite", "https://example/geosite", 0));
        await _user.SetSettingAsync("log-level", "warning");
        await _user.SetSettingAsync("selected-target", "keep");

        await MachineMigration.SplitLegacyAsync(_machine, _user, NullLogger.Instance);

        Assert.Single(await _machine.ListGeoSourcesAsync());
        Assert.Equal("warning", await _machine.GetSettingAsync("log-level"));
        Assert.Null(await _machine.GetSettingAsync("selected-target"));

        // The marker makes a second run a no-op even after a new legacy source appears.
        await _user.SaveGeoSourceAsync(new GeoSource("geoip-2", "geoip", "https://example/geoip2", 1));
        await MachineMigration.SplitLegacyAsync(_machine, _user, NullLogger.Instance);

        Assert.Single(await _machine.ListGeoSourcesAsync());
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
