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

public sealed class TransportProxyRefreshTests : IAsyncLifetime
{
    private const string FirstCredential = "416a993d-8408-49f3-a48f-72135ee15d31";
    private const string SecondCredential = "c6a02bb8-b270-4352-9574-1a89f21c8380";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-proxy-refresh-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _source = Guid.NewGuid().ToString("D");
    private readonly ECDsa _identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publisher = Config("a.example", 51993);
    private readonly string _consumer = Config("b.example", 51994);
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        await _store.SaveConfigAsync("A", _publisher).ConfigureAwait(false);
        await _store.SaveConfigAsync("B", _consumer).ConfigureAwait(false);
        _catalog = TransportProxyCatalog.For(_store);
        await PublishAsync("original", FirstCredential).ConfigureAwait(false);
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        await _catalog.SelectAsync("B", reference).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _identity.Dispose();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AForeignPublicationRefreshesTheRunningChoiceWithoutApplyingAPendingSelection(bool pendingNone)
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var pending = pendingNone ? null : await _catalog.SaveLocalAsync(null,
            Definition("pending", FirstCredential)).ConfigureAwait(false);
        await _catalog.SelectAsync("B", pending).ConfigureAwait(false);
        await _store.SaveConfigAsync("B", _consumer.Replace("b.example:51994", "pending.example:12345", StringComparison.Ordinal)).ConfigureAwait(false);
        await PublishAsync("changed", SecondCredential).ConfigureAwait(false);

        var refresh = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(refresh);
        Assert.Equal("", refresh.Fault);
        Assert.Equal(running.Record.Reference, refresh.Reference);
        Assert.True(refresh.AppliesTo("session-one", "B", running.Record.Reference));
        Assert.Equal("changed", refresh.Proxy!.Record.Proxy.Path);
        Assert.Equal(SecondCredential, refresh.Proxy.Record.Proxy.Auth.Credential);
        Assert.Equal("A", refresh.Proxy.Publisher);
        Assert.Equal("b.example", refresh.Proxy.TargetHost);
        Assert.Equal(51994, refresh.Proxy.TargetPort);
        Assert.Equal(pending, (await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
        Assert.Equal("original", running.Record.Proxy.Path);
    }

    [Fact]
    public async Task WithdrawalAndRestoreKeepTheRunningReferenceAndAnExplicitFault()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        await PublishAsync("", "", withdraw: true).ConfigureAwait(false);

        var removed = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(removed);
        Assert.Equal("proxy-missing", removed.Fault);
        Assert.Null(removed.Proxy);
        Assert.Equal(running.Record.Reference, removed.Reference);
        Assert.True(removed.AppliesTo("session-one", "B", running.Record.Reference));
        var rejected = removed.ApplyTo("session-one", "B", running);
        Assert.Same(running, rejected.Proxy);
        Assert.Equal("proxy-missing", rejected.Fault);
        Assert.Equal(running.Record.Reference, (await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
        await PublishAsync("restored", SecondCredential).ConfigureAwait(false);

        var restored = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(restored);
        Assert.Equal("", restored.Fault);
        Assert.Equal(removed.Reference, restored.Reference);
        Assert.Equal("restored", restored.Proxy!.Record.Proxy.Path);
        var repaired = restored.ApplyTo("session-one", "B", rejected.Proxy, rejected.Fault);
        Assert.Equal("", repaired.Fault);
        Assert.Equal("restored", repaired.Proxy.Record.Proxy.Path);
    }

    [Fact]
    public async Task AServerHelloCannotApplyAPendingLocalEditOrAdoptAProxyForDirectUdp()
    {
        var reference = await _catalog.SaveLocalAsync(null, Definition("local", FirstCredential)).ConfigureAwait(false);
        await _catalog.SelectAsync("B", reference).ConfigureAwait(false);
        var local = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        await _catalog.SaveLocalAsync(reference.Id, Definition("pending-local", SecondCredential)).ConfigureAwait(false);
        await PublishAsync("server-change", SecondCredential).ConfigureAwait(false);

        Assert.Null(await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, local).ConfigureAwait(false));
        Assert.Null(await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, null).ConfigureAwait(false));
        Assert.Null(await TransportProxyRefresh.CreateAsync(_store, "", "B", _consumer, local with { Record = local.Record with { Local = false } }).ConfigureAwait(false));
    }

    [Fact]
    public async Task ARefreshCannotCrossASessionConfigurationOrRunningReference()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var first = TransportProxyRefresh.NewSessionId();
        var second = TransportProxyRefresh.NewSessionId();
        var refresh = await TransportProxyRefresh.CreateAsync(_store, first, "B", _consumer, running).ConfigureAwait(false);

        Assert.NotEqual(first, second);
        Assert.NotNull(refresh);
        Assert.False(refresh.AppliesTo(second, "B", running.Record.Reference));
        Assert.False(refresh.AppliesTo(first, "A", running.Record.Reference));
        Assert.False(refresh.AppliesTo(first, "B", running.Record.Reference with { Id = "other" }));
        Assert.False(refresh.AppliesTo(first, "B", null));
        var stale = refresh.ApplyTo(second, "B", running, "proxy-missing");
        Assert.Same(running, stale.Proxy);
        Assert.Equal("proxy-missing", stale.Fault);
        var wrongDefinition = refresh with { Proxy = running with { Record = running.Record with { Reference = running.Record.Reference with { Id = "other" } } } };
        Assert.False(wrongDefinition.AppliesTo(first, "B", running.Record.Reference));
    }

    [Fact]
    public void RedeliveryUsesTheCurrentDurableSessionAndCannotReviveAClearedRequest()
    {
        var oldIntent = new Session("B", FirstCredential);
        var durable = new Session("B", SecondCredential);

        Assert.Same(durable, TransportProxyRefresh.RequestForStart(oldIntent, durable, redelivery: true));
        Assert.Null(TransportProxyRefresh.RequestForStart(oldIntent, default(Session), redelivery: true));
        Assert.Same(oldIntent, TransportProxyRefresh.RequestForStart(oldIntent, durable, redelivery: false));
        Assert.Same(durable, TransportProxyRefresh.RequestForStart(default(Session), durable, redelivery: false));
    }

    [Fact]
    public async Task PublicDiagnosticsDoNotRenderTheResolvedCredential()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var refresh = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(refresh);
        Assert.DoesNotContain(FirstCredential, refresh.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecondCredential, refresh.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrelatedPublicationCannotReplaceTheRunningReference()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var other = Config("c.example", 51995);
        await _store.SaveConfigAsync("C", other).ConfigureAwait(false);
        await _catalog.SelectAsync("B", null).ConfigureAwait(false);
        await PublishAsync("unrelated", SecondCredential, source: Guid.NewGuid().ToString("D"), publisher: "C", text: other).ConfigureAwait(false);

        var refresh = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(refresh);
        Assert.Equal(running.Record.Reference, refresh.Reference);
        Assert.Equal("original", refresh.Proxy!.Record.Proxy.Path);
        Assert.Equal(FirstCredential, refresh.Proxy.Record.Proxy.Auth.Credential);
        Assert.Equal("A", refresh.Proxy.Publisher);
        Assert.Null((await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task ALateHelloRestartsOnlyTheMatchingConnectingCarrier()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        await PublishAsync("late-route", SecondCredential).ConfigureAwait(false);
        var refresh = (await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false))!;
        var fresh = refresh.ApplyTo("session-one", "B", running);

        Assert.True(TransportProxyRefresh.ChangesDial(true, "session-one", "B", running, "", "session-one", "B", fresh.Proxy, fresh.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(false, "session-one", "B", running, "", "session-one", "B", fresh.Proxy, fresh.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-two", "B", running, "", "session-one", "B", fresh.Proxy, fresh.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-one", "C", running, "", "session-one", "B", fresh.Proxy, fresh.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-one", "B", null, "", "session-one", "B", fresh.Proxy, fresh.Fault));
    }

    [Fact]
    public async Task RepeatedSnapshotsAndDisplayMetadataCannotCreateARedialLoop()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var roundtrip = JsonSerializer.Deserialize<ResolvedTransportProxy>(JsonSerializer.Serialize(running))!;
        var display = roundtrip with
        {
            Publisher = "another-publisher",
            Record = roundtrip.Record with { Proxy = roundtrip.Record.Proxy with { Name = "Renamed", Updated = DateTimeOffset.UtcNow } },
        };

        Assert.NotSame(running, roundtrip);
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-one", "B", running, "", "session-one", "B", roundtrip, ""));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-one", "B", running, "", "session-one", "B", display, ""));
        var changed = display with { Record = display.Record with { Proxy = display.Record.Proxy with { Port = 7443 } } };
        Assert.True(TransportProxyRefresh.ChangesDial(true, "session-one", "B", running, "", "session-one", "B", changed, ""));
    }

    [Fact]
    public async Task ALateWithdrawalTerminatesTheConnectingProxyWithoutTurningItIntoUdp()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        await PublishAsync("", "", withdraw: true).ConfigureAwait(false);
        var refresh = (await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false))!;
        var missing = refresh.ApplyTo("session-one", "B", running);

        Assert.Same(running, missing.Proxy);
        Assert.Equal("proxy-missing", missing.Fault);
        Assert.True(TransportProxyRefresh.ChangesDial(true, "session-one", "B", running, "", "session-one", "B", missing.Proxy, missing.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "session-one", "B", missing.Proxy, missing.Fault,
            "session-one", "B", missing.Proxy, missing.Fault));
        Assert.False(TransportProxyRefresh.ChangesDial(false, "session-one", "B", running, "", "session-one", "B", missing.Proxy, missing.Fault));
    }

    [Fact]
    public async Task ALateHelloCannotReplaceRunningXhttpWithThePendingWebSocket()
    {
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var pending = await _catalog.SaveLocalAsync(null, new OfferedProxy("pending", "Pending WS", "websocket",
            "pending.example.net", 7443, "pendingws", new OfferedProxyTls("pending.example.net", ""),
            new OfferedProxyAuth("bearer", "pendingCredential"), DateTimeOffset.MinValue)).ConfigureAwait(false);
        await _catalog.SelectAsync("B", pending).ConfigureAwait(false);
        await _store.SaveConfigAsync("B", _consumer.Replace("b.example:51994", "pending.example:12345", StringComparison.Ordinal)).ConfigureAwait(false);
        await PublishAsync("late-xhttp", SecondCredential).ConfigureAwait(false);

        var refresh = (await TransportProxyRefresh.CreateAsync(_store, "running-session", "B", _consumer, running).ConfigureAwait(false))!;
        var updated = refresh.ApplyTo("running-session", "B", running);
        var pendingWs = WsEndpoint.From((await _catalog.ResolveAsync("B").ConfigureAwait(false))!);

        Assert.Equal("xhttp", updated.Proxy.Record.Proxy.Transport);
        Assert.Equal("late-xhttp", updated.Proxy.Record.Proxy.Path);
        Assert.Equal(running.Record.Reference, updated.Proxy.Record.Reference);
        Assert.Equal("b.example", updated.Proxy.TargetHost);
        Assert.Equal(51994, updated.Proxy.TargetPort);
        Assert.Equal("running-session", refresh.SessionId);
        Assert.False(refresh.LegacyWebSocket);
        Assert.Null(TransportProxyRefresh.CreateLegacy("running-session", "B", _consumer, running, pendingWs, LegacyOffer(7443, "pendingws")));
        Assert.False(refresh.AppliesToLegacy("running-session", "B", running, pendingWs));
        Assert.Equal(pending, (await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
        Assert.True(TransportProxyRefresh.ChangesDial(true, "running-session", "B", running, "",
            "running-session", "B", updated.Proxy, ""));
    }

    [Fact]
    public async Task AnOlderWebSocketRefreshUsesTheFrozenConfigAndOnlyItsConnectingSession()
    {
        var front = TransportProxyRefresh.LegacyFront("wss://b.example:8443/original", 8443, true);
        await _catalog.SelectAsync("B", null).ConfigureAwait(false);
        await _store.SaveConfigAsync("B", _consumer.Replace("b.example:51994", "pending.example:12345", StringComparison.Ordinal)).ConfigureAwait(false);
        var refresh = TransportProxyRefresh.CreateLegacy("legacy-session", "B", _consumer, null, front, LegacyOffer(9443, "newfront"))!;
        var roundtrip = JsonSerializer.Deserialize<TransportProxyRefresh>(JsonSerializer.Serialize(refresh))!;

        Assert.True(roundtrip.AppliesToLegacy("legacy-session", "B", null, front));
        Assert.Equal("b.example", roundtrip.Front!.Value.Host);
        Assert.Equal(9443, roundtrip.Front.Value.Port);
        Assert.Equal("newfront", roundtrip.Front.Value.PathPrefix);
        Assert.False(roundtrip.AppliesToLegacy("new-session", "B", null, front));
        Assert.False(roundtrip.AppliesToLegacy("legacy-session", "A", null, front));
        Assert.False(roundtrip.AppliesToLegacy("legacy-session", "B", null, null));
        Assert.False(roundtrip.AppliesTo("legacy-session", "B", new ProxyReference("source", "proxy")));
        Assert.True(TransportProxyRefresh.ChangesDial(true, "legacy-session", "B", null, "",
            "legacy-session", "B", null, "", front, roundtrip.Front));
        Assert.False(TransportProxyRefresh.ChangesDial(false, "legacy-session", "B", null, "",
            "legacy-session", "B", null, "", front, roundtrip.Front));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "new-session", "B", null, "",
            "legacy-session", "B", null, "", front, roundtrip.Front));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "legacy-session", "B", null, "",
            "legacy-session", "B", null, "", roundtrip.Front, roundtrip.Front));
        Assert.Null((await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public void AHeadHelloCannotRemoveAnOlderWebSocketOrAdoptOneForDirectUdp()
    {
        var front = TransportProxyRefresh.LegacyFront("b.example", 8443, true);
        var withdrawn = TransportProxyRefresh.CreateLegacy("legacy-session", "B", _consumer, null, front, LegacyOffer(0, ""));

        Assert.Null(withdrawn);
        Assert.False(new TransportProxyRefresh("legacy-session", "B", null, null, LegacyWebSocket: true)
            .AppliesToLegacy("legacy-session", "B", null, front));
        Assert.False(TransportProxyRefresh.ChangesDial(true, "legacy-session", "B", null, "",
            "legacy-session", "B", null, "", null, front));
        Assert.Null(TransportProxyRefresh.CreateLegacy("legacy-session", "B", _consumer, null, null, LegacyOffer(8443, "newfront")));
        Assert.Null(TransportProxyRefresh.CreateLegacy("", "B", _consumer, null, front, LegacyOffer(8443, "newfront")));
        Assert.Null(TransportProxyRefresh.CreateLegacy("legacy-session", "B", _consumer, null, front, ServerOffer.None));
    }

    [Fact]
    public async Task AVerifiedSourceUpgradeRefreshesItsUnsignedRunningAliasWithoutApplyingPendingNone()
    {
        var source = Guid.NewGuid().ToString("D");
        await PublishAsync("unsigned", FirstCredential, source: source, signed: false).ConfigureAwait(false);
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false), one => !one.Missing).Reference;
        await _catalog.SelectAsync("B", reference).ConfigureAwait(false);
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        await _catalog.SelectAsync("B", null).ConfigureAwait(false);
        await PublishAsync("signed", SecondCredential, source: source).ConfigureAwait(false);

        var refresh = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(refresh);
        Assert.Equal("", refresh.Fault);
        Assert.Equal(reference, refresh.Reference);
        Assert.Equal(new ProxyReference(ProxySourceIdentity.Signed(source), reference.Id), refresh.CanonicalReference);
        Assert.Equal(refresh.CanonicalReference, refresh.Proxy!.Record.Reference);
        Assert.True(refresh.AppliesTo("session-one", "B", reference));
        Assert.Equal("signed", refresh.ApplyTo("session-one", "B", running).Proxy.Record.Proxy.Path);
        Assert.Null((await _catalog.SelectionAsync("B").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task AnotherWgIssuerCannotPromoteTheRunningUnsignedReference()
    {
        var source = Guid.NewGuid().ToString("D");
        await PublishAsync("unsigned", FirstCredential, source: source, signed: false).ConfigureAwait(false);
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false), one => !one.Missing).Reference;
        await _catalog.SelectAsync("B", reference).ConfigureAwait(false);
        var running = (await _catalog.ResolveAsync("B").ConfigureAwait(false))!;
        var other = Config("c.example", 51995);
        await _store.SaveConfigAsync("C", other).ConfigureAwait(false);
        await PublishAsync("other-issuer", SecondCredential, source: source, publisher: "C", text: other).ConfigureAwait(false);
        await _store.RemoveConfigAsync("A").ConfigureAwait(false);
        await ConfigForget.CarryAsync(_store, "A").ConfigureAwait(false);

        var refresh = await TransportProxyRefresh.CreateAsync(_store, "session-one", "B", _consumer, running).ConfigureAwait(false);

        Assert.NotNull(refresh);
        Assert.Equal("proxy-missing", refresh.Fault);
        Assert.Null(refresh.Proxy);
        Assert.Null(refresh.CanonicalReference);
        Assert.Equal(reference, refresh.Reference);
    }

    private async Task PublishAsync(string path, string credential, bool withdraw = false, string? source = null,
        bool signed = true, string publisher = "A", string? text = null)
    {
        var body = text ?? _publisher;
        var advertised = source ?? _source;
        var issuer = ConfigServices.Target(body)!.ServerKey;
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + advertised + "\n" + issuer);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(_identity.ExportSubjectPublicKeyInfo()), issuer,
            Convert.ToBase64String(_identity.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var offer = ServerOffer.Parse(JsonSerializer.Serialize(new
        {
            server = "amneziageo", version = "1", client = "client",
            features = new { proxies = new OfferedProxyCatalog(1, advertised, withdraw ? [] : [Definition(path, credential)], signed ? proof : null) },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)))
            .AskAsync(publisher, body, default).ConfigureAwait(false);
    }

    private static OfferedProxy Definition(string path, string credential) => new("one", "CDN", "xhttp", "cdn.example.net", 443, path,
        new OfferedProxyTls("cdn.example.net", ""), new OfferedProxyAuth("vless", credential), DateTimeOffset.MinValue,
        new OfferedXHttp("packet-up", "GET", "header"));

    private static ServerOffer LegacyOffer(int port, string path) => new("1", "phone", new Dictionary<string, JsonElement>
    {
        [ServerOffer.WebSocketFeature] = JsonSerializer.SerializeToElement(new { port, path }),
    });

    private static string Config(string host, int port)
    {
        var client = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return "# AmneziaGeo Services = 8443\n[Interface]\nPrivateKey = " + client
            + "\nAddress = 10.88.0.2/32\n[Peer]\nPublicKey = " + Curve25519.PublicOf(server)
            + "\nEndpoint = " + host + ":" + port + "\nAllowedIPs = 0.0.0.0/0\n";
    }

    private sealed record Session(string Config, string Credential);
}
