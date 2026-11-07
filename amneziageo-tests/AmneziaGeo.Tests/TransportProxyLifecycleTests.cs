using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyLifecycleTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-proxy-lifecycle-" + Guid.NewGuid().ToString("N") + ".db");
    private SqliteStateStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
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
    public async Task AnUnchangedVerifiedOfferNotifiesWhenItsCatalogWasMissing()
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("publisher", false)).ConfigureAwait(false);
        var target = ConfigServices.Target(text)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), target.ServerKey, key);
        await ServerOfferStore.WriteAsync(_store, "publisher", target, offer, DateTimeOffset.UtcNow).ConfigureAwait(false);
        var changed = new List<string>();
        var catalog = TransportProxyCatalog.For(_store);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)), transportProxies: catalog);

        await offers.AskEachAsync([("publisher", text)], name =>
        {
            changed.Add(name);
            return Task.CompletedTask;
        }, default).ConfigureAwait(false);

        Assert.Equal(["publisher"], changed);
        Assert.Single(await catalog.ListAsync().ConfigureAwait(false));
        Assert.Null((await catalog.SelectionAsync("publisher").ConfigureAwait(false)).Reference);
        Assert.True((await ServerOfferStore.ReadAsync(_store, "publisher", text).ConfigureAwait(false)).Settles(offer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AVerifiedCachedCatalogIsAvailableBeforeTheNextHello(bool selectionExists)
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store);
        if (selectionExists)
        {
            await catalog.EnsureSelectionAsync("publisher").ConfigureAwait(false);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key);
        await ServerOfferStore.WriteAsync(_store, "publisher", ConfigServices.Target(text)!, offer, DateTimeOffset.UtcNow).ConfigureAwait(false);

        var proxy = Assert.Single(await catalog.ListAsync().ConfigureAwait(false));
        await catalog.SelectAsync("publisher", proxy.Reference).ConfigureAwait(false);
        var resolved = await catalog.ResolveAsync("publisher").ConfigureAwait(false);

        Assert.NotNull(resolved);
        Assert.Equal(proxy.Reference, resolved.Record.Reference);
        Assert.Equal("publisher", resolved.Publisher);
        Assert.Equal(51820, resolved.TargetPort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilenceAndForeignRepliesKeepTheLastCatalog(bool heard)
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var reply = new HelloReply(Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key), true);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(reply));
        await offers.AskAsync("publisher", text, default).ConfigureAwait(false);
        var before = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
        reply = new HelloReply(null, heard);

        await offers.AskEachAsync([("publisher", text)], null, default).ConfigureAwait(false);

        Assert.Equal(before, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task AnOfferWithoutAHeardReplyDoesNotInitializeTheCatalog()
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key);
        var before = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, false)));

        await offers.AskAsync("publisher", text, default).ConfigureAwait(false);

        Assert.Equal(before, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task AnEphemeralVerifiedOfferIsKeptWithoutPublishingACatalog()
    {
        var text = Text();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key);
        var before = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)));

        Assert.True((await offers.AskAsync("ephemeral", text, default).ConfigureAwait(false)).Ours);

        Assert.True((await ServerOfferStore.ReadAsync(_store, "ephemeral", text).ConfigureAwait(false)).Ours);
        Assert.Equal(before, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task LegacyMigrationKeepsTheUdpPortSeparateFromTheHelloPort()
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("publisher", true,
            WebSocketHost: "wss://manual.example.net:7443/manual", WebSocketPort: 7443)).ConfigureAwait(false);
        var offer = ServerOffer.Parse("""
            {"server":"amneziageo","version":"1","client":"client","features":{"websocket":{"port":9443,"path":"offered"}}}
            """);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)));

        await offers.AskAsync("publisher", text, default).ConfigureAwait(false);

        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        var record = Assert.Single(Assert.Single(state.Publications.Where(one => one.Slot == "legacy")).Records);
        Assert.Equal(51820, record.LegacyPort);
        Assert.Equal(8443, ConfigServices.Target(text)!.Port);
        Assert.Equal(9443, record.Proxy.Port);
        Assert.Equal("offered", record.Proxy.Path);
        var resolved = await TransportProxyCatalog.For(_store).ResolveAsync("publisher").ConfigureAwait(false);
        Assert.NotNull(resolved);
        Assert.Equal(51820, resolved.TargetPort);
    }

    [Fact]
    public async Task RenameCarriesTheSelectionPublicationAndCachedOffer()
    {
        var text = Text();
        await _store.SaveConfigAsync("old", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key);
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)))
            .AskAsync("old", text, default).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store);
        var reference = Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Reference;
        await catalog.SelectAsync("old", reference).ConfigureAwait(false);
        await _store.RenameConfigAsync("old", "new").ConfigureAwait(false);

        await ConfigRename.CarryAsync(_store, "old", "new").ConfigureAwait(false);

        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.DoesNotContain(state.Selections, one => one.Config == "old");
        Assert.Equal(reference, Assert.Single(state.Selections.Where(one => one.Config == "new")).Reference);
        Assert.DoesNotContain(state.Publications, one => one.Publisher == "old");
        Assert.Contains(state.Publications, one => one.Publisher == "new" && one.Records.Any(record => record.Reference == reference));
        Assert.True((await ServerOfferStore.ReadAsync(_store, "new", text).ConfigureAwait(false)).Ours);
        Assert.False((await ServerOfferStore.ReadAsync(_store, "old", text).ConfigureAwait(false)).Ours);
        Assert.Equal("new", (await catalog.ResolveAsync("new").ConfigureAwait(false))!.Publisher);
    }

    [Fact]
    public async Task ForgetWithdrawsThePublisherAndKeepsAnotherConfigsMissingSelection()
    {
        var text = Text();
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        await _store.SaveConfigAsync("consumer", Text()).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key);
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)))
            .AskAsync("publisher", text, default).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store);
        var reference = Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Reference;
        await catalog.SelectAsync("consumer", reference).ConfigureAwait(false);
        await _store.RemoveConfigAsync("publisher").ConfigureAwait(false);

        await ConfigForget.CarryAsync(_store, "publisher").ConfigureAwait(false);

        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.DoesNotContain(state.Publications, one => one.Publisher == "publisher");
        Assert.DoesNotContain(state.Selections, one => one.Config == "publisher");
        Assert.Equal(reference, Assert.Single(state.Selections.Where(one => one.Config == "consumer")).Reference);
        Assert.False((await ServerOfferStore.ReadAsync(_store, "publisher", text).ConfigureAwait(false)).Ours);
        var missing = Assert.Single(await catalog.ListAsync().ConfigureAwait(false));
        Assert.True(missing.Missing);
        Assert.Equal(reference, missing.Reference);
        var failure = await Assert.ThrowsAsync<TransportProxyFault>(() => catalog.ResolveAsync("consumer")).ConfigureAwait(false);
        Assert.Equal("proxy-missing", failure.Code);
        await _store.SaveConfigAsync("publisher", text).ConfigureAwait(false);
        Assert.True(Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Missing);
    }

    private static string Text()
    {
        var client = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return "# AmneziaGeo Services = 8443\n[Interface]\nPrivateKey = " + client
            + "\nAddress = 10.88.0.2/32\n[Peer]\nPublicKey = " + Curve25519.PublicOf(server)
            + "\nEndpoint = vpn.example.net:51820\nAllowedIPs = 0.0.0.0/0\n";
    }

    private static ServerOffer Offer(string source, string issuer, ECDsa key)
    {
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + source + "\n" + issuer);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), issuer,
            Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var item = new OfferedProxy("one", "relay", "websocket", "cdn.example.net", 443, "relay",
            new OfferedProxyTls("cdn.example.net", ""), new OfferedProxyAuth("bearer", "test-credential"), DateTimeOffset.MinValue);
        return ServerOffer.Parse(JsonSerializer.Serialize(new
        {
            server = "amneziageo", version = "1", client = "client",
            features = new { proxies = new OfferedProxyCatalog(1, source, [item], proof) },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
