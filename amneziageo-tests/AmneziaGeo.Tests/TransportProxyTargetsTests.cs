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

    [Fact]
    public async Task OldSharedLocalsMigrateOnceWithoutChangingCredentialsOrDisabledChoices()
    {
        var source = Guid.NewGuid().ToString("D");
        var reference = new ProxyReference(source, "shared");
        var record = new CatalogTransportProxy(reference, Proxy(null) with { Id = reference.Id }, Local: true);
        var orphan = record with { Reference = reference with { Id = "orphan" }, Proxy = record.Proxy with { Id = "orphan" } };
        var old = new TransportProxyState
        {
            Version = 1, Revision = 1, LocalSource = source, Locals = [record, orphan],
            Selections = [new("a", reference), new("b", reference, Enabled: false)],
        };
        Assert.True(await _store.TrySaveTransportProxyStateAsync(old, 0).ConfigureAwait(false));
        Assert.NotNull(await _catalog.ResolveAsync("a").ConfigureAwait(false));
        Assert.Null(await _catalog.ResolveAsync("b").ConfigureAwait(false));
        var migrated = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Equal(2, migrated.Version);
        Assert.Equal(3, migrated.Locals.Count);
        Assert.Equal(orphan, migrated.Locals.Single(one => one.Reference == orphan.Reference));
        Assert.All(migrated.Locals.Where(one => one.OwnerConfig is not null), one =>
        {
            Assert.Equal(record.Proxy.Auth, one.Proxy.Auth);
            Assert.Equal(record.Proxy.Tls, one.Proxy.Tls);
            Assert.Equal(record.Proxy.Host, one.Proxy.Host);
            Assert.Equal(record.Proxy.Port, one.Proxy.Port);
            Assert.Equal(one.Reference, migrated.Selections.Single(choice => choice.Config == one.OwnerConfig).Reference);
        });
        var own = migrated.Selections.Single(one => one.Config == "b");
        Assert.False(own.Enabled);
        Assert.NotEqual(reference, own.Reference);
        Assert.Equal("proxy-target-denied", (await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.SelectAsync("a", own.Reference)).ConfigureAwait(false)).Code);
        await _catalog.ListAsync().ConfigureAwait(false);
        Assert.Equal(migrated.Revision, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task DraftChecksValidateOwnershipAndDestinationWithoutSavingOrSelecting()
    {
        var resolved = await _catalog.ResolveDraftAsync("a", null, Proxy(null)).ConfigureAwait(false);
        Assert.Equal("vpn-a.example.net", resolved.TargetHost);
        Assert.Equal("a", resolved.Record.OwnerConfig);
        Assert.Empty((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
        var reference = await _catalog.SaveLocalAsync(null, Proxy(null), ownerConfig: "a").ConfigureAwait(false);
        var denied = await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.ResolveDraftAsync("b", reference.Id, Proxy(null))).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", denied.Code);
        var incompatible = await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.ResolveDraftAsync("a", reference.Id,
            Proxy(new(true, [new("vpn-b.example.net", 443)])))).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", incompatible.Code);
        Assert.All((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Selections, one => Assert.Null(one.Reference));
    }

    [Fact]
    public async Task AnEmptyRestrictedPublicationDeniesEveryDestinationWithoutDiscardingTheCatalog()
    {
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!;
        var denied = Proxy(new(true, [])) with { Scope = "local", Auth = new("bearer", "test", "Authorization") };
        var catalog = new OfferedProxyCatalog(2, Guid.NewGuid().ToString("D"), [denied]);
        var body = JsonSerializer.SerializeToElement(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        Assert.NotNull(OfferedProxyCatalog.Read(body));
        await _catalog.SynchronizeAsync("a", ConfigServices.Target(text)!, new ServerOffer("1", "test",
            new Dictionary<string, JsonElement> { [ServerOffer.ProxiesV2Feature] = body })).ConfigureAwait(false);
        var summary = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.Empty(summary.CompatibleConfigs!);
        Assert.Equal("local", summary.Scope);
        Assert.Equal("proxy-target-denied", (await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.SelectAsync("a", summary.Reference)).ConfigureAwait(false)).Code);
    }

    [Fact]
    public async Task AnUnsavedDestinationIsCheckedAgainstTheSameServerPolicy()
    {
        var proxy = Proxy(new(true, [new("vpn-b.example.net", 443)]));
        Assert.Equal("proxy-target-denied", (await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.ResolveDraftAsync("a", null, proxy)).ConfigureAwait(false)).Code);
        var result = await _catalog.ResolveDraftAsync("a", null, proxy, target: new("vpn-b.example.net", 443)).ConfigureAwait(false);
        Assert.Equal("vpn-b.example.net", result.TargetHost);
        Assert.Equal(443, result.TargetPort);
        Assert.Equal("vpn-a.example.net", ConfigServices.Host(await _store.GetConfigTextAsync("a").ConfigureAwait(false)));
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
