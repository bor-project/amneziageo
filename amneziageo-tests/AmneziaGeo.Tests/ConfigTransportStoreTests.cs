using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
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
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
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

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Transport_KeepsTheRouterOff()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("routes-only", false, string.Empty, 443, 1420, false, MtuMode.Custom, UseRouter: false));

        var stored = await _store.GetConfigTransportAsync("routes-only");

        Assert.NotNull(stored);
        Assert.False(stored!.UseRouter);
        Assert.Equal(MtuMode.Custom, stored.MtuMode);
    }

    [Fact]
    public async Task Transport_LeavesTheRouterOnByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("plain", false, string.Empty, 443));

        var stored = await _store.GetConfigTransportAsync("plain");

        Assert.NotNull(stored);
        Assert.True(stored!.UseRouter);
    }

    [Fact]
    public async Task Transport_RewritesTheRouterWithTheRest()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("rewritten", false, string.Empty, 443, 0, false, MtuMode.Auto, UseRouter: false));
        await _store.SetConfigTransportAsync(
            new ConfigTransport("rewritten", true, "gate.example.net", 8443, 1380, true, MtuMode.Custom));

        var stored = await _store.GetConfigTransportAsync("rewritten");

        Assert.NotNull(stored);
        Assert.True(stored!.UseRouter);
        Assert.True(stored.UseIpv6);
        Assert.Equal(1380, stored.Mtu);
    }

    [Fact]
    public async Task Transport_KeepsAccessFromTheTunnel()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("reachable", false, string.Empty, 443, 0, false, MtuMode.Auto, true, AllowInbound: true, InboundNetwork: true));

        var stored = await _store.GetConfigTransportAsync("reachable");

        Assert.NotNull(stored);
        Assert.True(stored!.AllowInbound);
        Assert.True(stored.InboundNetwork);
    }

    [Fact]
    public async Task Transport_RefusesAccessFromTheTunnelByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("quiet", false, string.Empty, 443));

        var stored = await _store.GetConfigTransportAsync("quiet");

        Assert.NotNull(stored);
        Assert.False(stored!.AllowInbound);
        Assert.False(stored.InboundNetwork);
    }

    [Fact]
    public async Task Transport_HoldsTheRouterPerConfiguration()
    {
        await _store.SetConfigTransportAsync(
            new ConfigTransport("one", false, string.Empty, 443, 0, false, MtuMode.Auto, UseRouter: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("two", false, string.Empty, 443));

        Assert.False((await _store.GetConfigTransportAsync("one"))!.UseRouter);
        Assert.True((await _store.GetConfigTransportAsync("two"))!.UseRouter);
    }
}
