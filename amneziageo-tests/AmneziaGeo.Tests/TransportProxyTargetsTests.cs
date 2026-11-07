using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyTargetsTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-targets-" + Guid.NewGuid().ToString("N") + ".db");
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        _catalog = new TransportProxyCatalog(_store);
        foreach (var name in new[] { "a", "b" })
        {
            var key = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
            await _store.SaveConfigAsync(name, $"[Interface]\nPrivateKey = {key}\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = vpn-{name}.example.net:443\nAllowedIPs = 0.0.0.0/0").ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LocalPolicyFiltersConfigurationsAndRejectsAnIncompatibleNewChoice()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy(new(true, [new("vpn-a.example.net", 443)]))).ConfigureAwait(false);
        var summary = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.Equal(["a"], summary.CompatibleConfigs);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        Assert.NotNull(await _catalog.ResolveAsync("a").ConfigureAwait(false));
        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.SelectAsync("b", reference)).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", error.Code);
        Assert.Null((await _catalog.SelectionAsync("b").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task TighteningPolicyPreservesTheSavedReferenceAndBlocksConnectAfterRestart()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy(null)).ConfigureAwait(false);
        await _catalog.SelectAsync("b", reference).ConfigureAwait(false);
        await _catalog.SaveLocalAsync(reference.Id, Proxy(new(true, [new("vpn-a.example.net", 443)]))).ConfigureAwait(false);
        var restarted = new TransportProxyCatalog(new SqliteStateStore(_path));
        Assert.Equal(reference, (await restarted.SelectionAsync("b").ConfigureAwait(false)).Reference);
        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => restarted.ResolveAsync("b")).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", error.Code);
        Assert.Equal(reference, (await restarted.SelectionAsync("b").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task AddingOtherAllowedTargetsDoesNotRequireRestartForTheCurrentDestination()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy(new(true, [new("vpn-a.example.net", 443)]))).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        var before = await TransportProxyChanges.CaptureAsync(_store, ["a"]).ConfigureAwait(false);
        await _catalog.SaveLocalAsync(reference.Id, Proxy(new(true, [new("vpn-a.example.net", 443), new("vpn-b.example.net", 443)]))).ConfigureAwait(false);
        Assert.Empty(await before.ChangedAsync().ConfigureAwait(false));
        var restrictive = await TransportProxyChanges.CaptureAsync(_store, ["a"]).ConfigureAwait(false);
        await _catalog.SaveLocalAsync(reference.Id, Proxy(new(true, [new("vpn-b.example.net", 443)]))).ConfigureAwait(false);
        Assert.Equal(["a"], await restrictive.ChangedAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task HelloPolicyChangesUpdateApplicabilityWithoutChangingTheSelection()
    {
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!;
        var target = ConfigServices.Target(text)!;
        var source = Guid.NewGuid().ToString("D");
        ServerOffer Offer(ProxyTargets? targets) => new("1", "test", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesFeature] = JsonSerializer.SerializeToElement(new OfferedProxyCatalog(1, source, [Proxy(targets)]), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }),
        });

        await _catalog.SynchronizeAsync("a", target, Offer(null), 443).ConfigureAwait(false);
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        await _catalog.SelectAsync("b", reference).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", target, Offer(new(true, [new("vpn-a.example.net", 443)])), 443).ConfigureAwait(false);
        var summary = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.Equal(["a"], summary.CompatibleConfigs);
        Assert.Equal(reference, (await _catalog.SelectionAsync("b").ConfigureAwait(false)).Reference);
        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.ResolveAsync("b")).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", error.Code);
    }

    private static OfferedProxy Proxy(ProxyTargets? targets) => new("one", "Restricted proxy", "websocket", "relay.example.net", 443, "proxy",
        new OfferedProxyTls("relay.example.net", ""), new OfferedProxyAuth("bearer", "test"), DateTimeOffset.UtcNow, Targets: targets);

    [Fact]
    public async Task SelectedDestinationsUseTheCanonicalPolicyHostWithoutChangingTheConfiguration()
    {
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!.Replace("vpn-a.example.net", "BÜCHER.example.", StringComparison.Ordinal);
        await _store.SaveConfigAsync("a", text).ConfigureAwait(false);
        var reference = await _catalog.SaveLocalAsync(null, Proxy(new(true, [new("xn--bcher-kva.example", 443)]))).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        var resolved = await _catalog.ResolveAsync("a").ConfigureAwait(false);

        Assert.Equal("xn--bcher-kva.example", resolved!.TargetHost);
        Assert.Equal(text, await _store.GetConfigTextAsync("a").ConfigureAwait(false));
    }

    [Fact]
    public async Task UnrestrictedConnectionsPreserveThePreviousHostnameValidation()
    {
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!.Replace("vpn-a.example.net", "vpn_node.example.net", StringComparison.Ordinal);
        await _store.SaveConfigAsync("a", text).ConfigureAwait(false);
        var reference = await _catalog.SaveLocalAsync(null, Proxy(null)).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        var resolved = await _catalog.ResolveAsync("a").ConfigureAwait(false);

        Assert.Equal("vpn_node.example.net", resolved!.TargetHost);
        Assert.Equal("vpn_node.example.net", WsEndpoint.From(resolved).TargetHost);
    }
}
