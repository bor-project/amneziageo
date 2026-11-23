using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Geo;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The router rides with the tunnel a configuration builds, beside the MTU and the IPv6 opt-in: on unless the
/// configuration says otherwise, and it has to survive a round trip and an edit that touches only its neighbours.
/// </summary>
public sealed class ConfigTransportStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-transport-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(443)]
    [InlineData(65535)]
    public async Task ServicePortSurvivesReinitializationAndNeighbouringEdits(int port)
    {
        var transport = new ConfigTransport("vpn", false) { ServicesPort = port };
        await _store.SetConfigTransportAsync(transport).ConfigureAwait(false);
        await _store.InitializeAsync().ConfigureAwait(false);
        var restored = await _store.GetConfigTransportAsync("vpn").ConfigureAwait(false);
        Assert.Equal(port, restored!.ServicesPort);
        await _store.SetConfigTransportAsync(restored with { AllowSignal = false }).ConfigureAwait(false);
        Assert.Equal(port, (await _store.GetConfigTransportAsync("vpn").ConfigureAwait(false))!.ServicesPort);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    public async Task InvalidServicePortCommandsDoNotWrite(string value)
    {
        var transport = new ConfigTransport("vpn", false) { ServicesPort = 9443 };
        await _store.SetConfigTransportAsync(transport).ConfigureAwait(false);
        var ack = await ConfigServicePortCommands.SetAsync(_store, ["vpn", value], true).ConfigureAwait(false);
        Assert.False(ack.Ok);
        Assert.Equal(transport, await _store.GetConfigTransportAsync("vpn").ConfigureAwait(false));
    }

    [Fact]
    public async Task ServicePortCommandsKeepOtherSettingsAndCachedHello()
    {
        var transport = new ConfigTransport("vpn", false, UseIpv6: true, HoldTraffic: false);
        await _store.SetConfigTransportAsync(transport).ConfigureAwait(false);
        await _store.SetSettingAsync(ServerOfferStore.Key("vpn"), "cached").ConfigureAwait(false);
        Assert.True((await ConfigServicePortCommands.SetAsync(_store, ["vpn", "9443"], true).ConfigureAwait(false)).Ok);
        Assert.Equal(transport with { ServicesPort = 9443 }, await _store.GetConfigTransportAsync("vpn").ConfigureAwait(false));
        Assert.Equal("cached", await _store.GetSettingAsync(ServerOfferStore.Key("vpn")).ConfigureAwait(false));
        Assert.True((await ConfigServicePortCommands.SetAsync(_store, ["vpn", "0"], true).ConfigureAwait(false)).Ok);
        Assert.Equal(transport, await _store.GetConfigTransportAsync("vpn").ConfigureAwait(false));
        Assert.False((await ConfigServicePortCommands.SetAsync(_store, ["missing", "443"], false).ConfigureAwait(false)).Ok);
        Assert.Null(await _store.GetConfigTransportAsync("missing").ConfigureAwait(false));
    }

    [Fact]
    public async Task ExistingDatabaseGainsTheServicePortWithoutLosingItsConfiguration()
    {
        const string text = "[Interface]\nAddress = 10.0.0.2/32\n";
        var transport = new ConfigTransport("old", false, UseIpv6: true, UseRouter: false);
        await _store.SaveConfigAsync("old", text).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(transport).ConfigureAwait(false);
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE config_transport DROP COLUMN services_port";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await _store.InitializeAsync().ConfigureAwait(false);
        Assert.Equal(text, await _store.GetConfigTextAsync("old").ConfigureAwait(false));
        Assert.Equal(transport, await _store.GetConfigTransportAsync("old").ConfigureAwait(false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9443)]
    public void PortableTransportKeepsTheLocalServicePort(int port)
    {
        var transport = new PortableBundle.TransportBlock(false, "", 0, 0, ServicesPort: port);
        var bundle = new PortableBundle.Bundle(PortableBundle.FormatTag, PortableBundle.CurrentVersion,
            [new PortableBundle.ConfigBlock("vpn", "[Interface]", transport, null)], []);
        var restored = PortableBundle.Deserialize(PortableBundle.Serialize(bundle));
        Assert.Equal(port, Assert.Single(restored!.Configs).Transport!.ServicesPort);
    }

    [Fact]
    public void OlderPortableTransportUsesTheServerServicePort()
    {
        var wire = "{\"format\":\"amneziageo\",\"version\":2,\"configs\":[{\"name\":\"vpn\",\"configText\":\"[Interface]\",\"transport\":{\"useWebSocket\":false,\"host\":\"\",\"port\":0,\"mtu\":0}}],\"lists\":[]}";
        wire = wire.Replace("\"amneziageo\"", $"\"{PortableBundle.FormatTag}\"");
        Assert.Equal(0, Assert.Single(PortableBundle.Deserialize(wire)!.Configs).Transport!.ServicesPort);
    }

    [Fact]
    public async Task Transport_KeepsTheRouterOff()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("routes-only", false, 1420, false, MtuMode.Custom, UseRouter: false));

        var stored = await _store.GetConfigTransportAsync("routes-only");

        Assert.NotNull(stored);
        Assert.False(stored!.UseRouter);
        Assert.Equal(MtuMode.Custom, stored.MtuMode);
    }

    [Fact]
    public async Task Transport_LeavesTheRouterOnByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("plain", false));

        var stored = await _store.GetConfigTransportAsync("plain");

        Assert.NotNull(stored);
        Assert.True(stored!.UseRouter);
    }

    [Fact]
    public async Task Transport_RewritesTheRouterWithTheRest()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("rewritten", false, 0, false, MtuMode.Auto, UseRouter: false));
        await _store.SetConfigTransportAsync(
            new ConfigTransport("rewritten", true, 1380, true, MtuMode.Custom));

        var stored = await _store.GetConfigTransportAsync("rewritten");

        Assert.NotNull(stored);
        Assert.True(stored!.UseRouter);
        Assert.True(stored.UseIpv6);
        Assert.Equal(1380, stored.Mtu);
    }

    [Fact]
    public async Task Transport_KeepsTheFrontOfTheSettings()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("manual", true, WebSocketHost: "wss://user:pass@own.example:9443/t", WebSocketPort: 9443));

        var stored = await _store.GetConfigTransportAsync("manual");

        Assert.NotNull(stored);
        Assert.Equal("wss://user:pass@own.example:9443/t", stored!.WebSocketHost);
        Assert.Equal(9443, stored.WebSocketPort);
    }

    [Fact]
    public async Task Transport_KeepsAccessFromTheTunnel()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("reachable", false, 0, false, MtuMode.Auto, true, AllowInbound: true, InboundNetwork: true));

        var stored = await _store.GetConfigTransportAsync("reachable");

        Assert.NotNull(stored);
        Assert.True(stored!.AllowInbound);
        Assert.True(stored.InboundNetwork);
    }

    [Fact]
    public async Task Transport_RefusesAccessFromTheTunnelByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("quiet", false));

        var stored = await _store.GetConfigTransportAsync("quiet");

        Assert.NotNull(stored);
        Assert.False(stored!.AllowInbound);
        Assert.False(stored.InboundNetwork);
    }

    [Fact]
    public async Task Transport_HoldsTheRouterPerConfiguration()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("one", false, 0, false, MtuMode.Auto, UseRouter: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("two", false));

        Assert.False((await _store.GetConfigTransportAsync("one"))!.UseRouter);
        Assert.True((await _store.GetConfigTransportAsync("two"))!.UseRouter);
    }

    [Fact]
    public async Task Transport_HoldsTheRoutingPerConfiguration()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("kept", false, UseRouting: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("taken", false));

        Assert.False((await _store.GetConfigTransportAsync("kept"))!.UseRouting);
        Assert.True((await _store.GetConfigTransportAsync("taken"))!.UseRouting);
    }
}
