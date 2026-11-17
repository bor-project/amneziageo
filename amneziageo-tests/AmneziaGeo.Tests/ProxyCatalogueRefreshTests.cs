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

public sealed class ProxyCatalogueRefreshTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-proxy-refresh-" + Guid.NewGuid().ToString("N") + ".db");
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
    public async Task RefreshAsksFreshPublicationsOfEveryRegisteredConfiguration()
    {
        var owner = Text("owner.example.net");
        var other = Text("other.example.net");
        await _store.SaveConfigAsync("owner", owner).ConfigureAwait(false);
        await _store.SaveConfigAsync("other", other).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = Guid.NewGuid().ToString("D");
        var old = Offer(source, ConfigServices.Target(other)!.ServerKey, key, "old.example.net");
        await ServerOfferStore.WriteAsync(_store, "other", ConfigServices.Target(other)!, old, DateTimeOffset.UtcNow).ConfigureAwait(false);
        var calls = new List<string>();
        var offers = new ServerOffers(_store, ask: (target, _) =>
        {
            lock (calls)
            {
                calls.Add(target.ServerKey);
            }

            return Task.FromResult(new HelloReply(target.ServerKey == ConfigServices.Target(other)!.ServerKey
                ? Offer(source, target.ServerKey, key, "new.example.net") : new ServerOffer("1", "fixture", new Dictionary<string, JsonElement>()), true));
        });

        var result = await offers.RefreshProxiesAsync("owner", [("owner", owner), ("other", other)], null, default).ConfigureAwait(false);

        Assert.Contains(ConfigServices.Target(owner)!.ServerKey, calls);
        Assert.Contains(ConfigServices.Target(other)!.ServerKey, calls);
        Assert.Equal("new.example.net", Assert.Single(result.Items).Host);
        Assert.Null((await TransportProxyCatalog.For(_store).SelectionAsync("owner").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task RefreshWaitsForAllAnswersAndLimitsConcurrencyToFour()
    {
        var targets = new List<(string Config, string? Text)>();
        for (var index = 0; index < 8; index++)
        {
            var name = "config-" + index;
            var text = Text("vpn" + index + ".example.net");
            await _store.SaveConfigAsync(name, text).ConfigureAwait(false);
            targets.Add((name, text));
        }

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var offers = new ServerOffers(_store, ask: async (_, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            var previous = Volatile.Read(ref maximum);
            while (count > previous)
            {
                var observed = Interlocked.CompareExchange(ref maximum, count, previous);
                if (observed == previous)
                {
                    break;
                }

                previous = observed;
            }

            if (count == 4)
            {
                entered.TrySetResult(true);
            }

            try
            {
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return new HelloReply(new ServerOffer("1", "fixture", new Dictionary<string, JsonElement>()), true);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var refresh = offers.RefreshProxiesAsync("config-0", targets, null, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.Equal(4, Volatile.Read(ref active));
            Assert.False(refresh.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
        }

        await refresh.ConfigureAwait(false);
        Assert.Equal(4, maximum);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task UnknownOwnerIsRejectedBeforeAnyHello()
    {
        var text = Text("vpn.example.net");
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        var calls = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HelloReply(null, false));
        });

        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => offers.RefreshProxiesAsync("missing",
            [("owner", text)], null, default)).ConfigureAwait(false);

        Assert.Equal("unknown-config", error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FailedRefreshKeepsLocalRecordsServerCatalogAndDisabledSelection()
    {
        var text = Text("vpn.example.net");
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key, "relay.example.net");
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)))
            .AskAsync("owner", text, default).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store);
        var reference = Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Reference;
        await catalog.SelectAsync("owner", reference, enabled: false).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("owner", true)).ConfigureAwait(false);
        var local = new OfferedProxy("local", "local", "websocket", "local.example.net", 443, "local",
            new OfferedProxyTls("local.example.net", ""), new OfferedProxyAuth("bearer", "local-credential"), DateTimeOffset.MinValue);
        var localReference = await catalog.SaveLocalAsync(null, local, ownerConfig: "owner").ConfigureAwait(false);
        await catalog.ListAsync().ConfigureAwait(false);
        var before = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
        var calls = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("fixture unavailable");
        });

        var result = await offers.RefreshProxiesAsync("owner", [("owner", text)], null, default).ConfigureAwait(false);

        Assert.True(calls > 0);
        Assert.Equal(new ProxyCatalogRefreshFailure("owner", "hello-no-answer"), Assert.Single(result.Failures));
        Assert.Equal(before, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        Assert.Contains(result.Items, one => one.Reference == reference && !one.Missing);
        Assert.Contains(result.Items, one => one.Reference == localReference && one.Local);
        var selected = await catalog.SelectionAsync("owner").ConfigureAwait(false);
        Assert.Equal(reference, selected.Reference);
        Assert.False(selected.Enabled);
        Assert.False(await ServerOffers.UsesProxyAsync(_store, "owner", default).ConfigureAwait(false));
    }

    [Fact]
    public async Task AndroidConfigurationProviderOwnsRefreshWithoutSqliteConfigRows()
    {
        var owner = Text("owner.example.net");
        var other = Text("other.example.net");
        var configurations = new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = owner, ["other"] = other };
        var catalog = TransportProxyCatalog.For(_store, _ => Task.FromResult<IReadOnlyDictionary<string, string>>(configurations));
        Assert.Empty(await _store.ListConfigNamesAsync().ConfigureAwait(false));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ownerSource = Guid.NewGuid().ToString("D");
        var otherSource = Guid.NewGuid().ToString("D");
        var called = new HashSet<string>(StringComparer.Ordinal);
        var offers = new ServerOffers(_store, ask: (target, _) =>
        {
            lock (called)
            {
                called.Add(target.ServerKey);
            }

            var isOwner = target.ServerKey == ConfigServices.Target(owner)!.ServerKey;
            lock (key)
            {
                return Task.FromResult(new HelloReply(Offer(isOwner ? ownerSource : otherSource, target.ServerKey, key,
                    isOwner ? "owner-relay.example.net" : "other-relay.example.net"), true));
            }
        }, transportProxies: catalog);

        var result = await offers.RefreshProxiesAsync("owner", [("owner", owner), ("other", other)], null, default).ConfigureAwait(false);

        Assert.Equal(2, called.Count);
        Assert.Contains(result.Items, one => one.Host == "owner-relay.example.net");
        Assert.Contains(result.Items, one => one.Host == "other-relay.example.net");
        Assert.Empty(await _store.ListConfigNamesAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task PlatformProviderRejectsAnOwnerOnlyTheSqliteStoreStillNames()
    {
        var text = Text("vpn.example.net");
        await _store.SaveConfigAsync("removed", text).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store, _ => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal)));
        var calls = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HelloReply(null, false));
        }, transportProxies: catalog);

        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => offers.RefreshProxiesAsync("removed",
            [("removed", text)], null, default)).ConfigureAwait(false);

        Assert.Equal("unknown-config", error.Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilenceAndForeignAnswersRetainThePublishedCatalog(bool heard)
    {
        var text = Text("vpn.example.net");
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(Guid.NewGuid().ToString("D"), ConfigServices.Target(text)!.ServerKey, key, "relay.example.net");
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)))
            .AskAsync("owner", text, default).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_store);
        var before = Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Reference;
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(null, heard)));

        var result = await offers.RefreshProxiesAsync("owner", [("owner", text)], null, default).ConfigureAwait(false);

        Assert.Equal(before, Assert.Single(result.Items).Reference);
        Assert.False(result.Items[0].Missing);
        Assert.Equal(heard ? "hello-bad-answer" : "hello-no-answer", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task ManualRefreshUsesPublicHttpsForAnInactiveProxiedProfileAlone()
    {
        var text = Text("vpn.example.net") + "# AmneziaGeo Services TLS Name = hello.example.net\n# AmneziaGeo Services TLS Pin = fixture-pin\n";
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = Guid.NewGuid().ToString("D");
        var point = ConfigServices.Target(text)!;
        var old = Offer(source, point.ServerKey, key, "old.example.net");
        var catalog = TransportProxyCatalog.For(_store);
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(old, true)))
            .AskAsync("owner", text, default).ConfigureAwait(false);
        var reference = Assert.Single(await catalog.ListAsync().ConfigureAwait(false)).Reference;
        await catalog.SelectAsync("owner", reference, enabled: true).ConfigureAwait(false);
        var manual = new List<ServiceTarget>();
        var ordinary = 0;
        var confined = 0;
        var offers = new ServerOffers(_store, carried: _ => false,
            ask: (_, _) =>
            {
                Interlocked.Increment(ref ordinary);
                return Task.FromResult(new HelloReply(null, false));
            },
            askConfined: (_, _, _) =>
            {
                Interlocked.Increment(ref confined);
                return Task.FromResult(new HelloReply(null, false));
            },
            askManual: (target, _) =>
            {
                manual.Add(target);
                return Task.FromResult(new HelloReply(Offer(source, point.ServerKey, key, "new.example.net"), true));
            });

        var result = await offers.RefreshProxiesAsync("owner", [("owner", text)], null, default).ConfigureAwait(false);

        Assert.Empty(result.Failures);
        Assert.Equal("new.example.net", Assert.Single(result.Items).Host);
        var asked = Assert.Single(manual);
        Assert.False(asked.InsideTunnel);
        Assert.Equal("", asked.Origin);
        Assert.Equal("https://vpn.example.net:8443", ServerHello.Origin(asked));
        Assert.Equal("hello.example.net", asked.TlsName);
        Assert.Equal("fixture-pin", asked.TlsPin);
        Assert.Equal(point.PrivateKey, asked.PrivateKey);
        Assert.Equal(point.ServerKey, asked.ServerKey);
        var selection = await catalog.SelectionAsync("owner").ConfigureAwait(false);
        Assert.Equal(reference, selection.Reference);
        Assert.True(selection.Enabled);
        var denied = await Assert.ThrowsAsync<TransportProxyFault>(() => offers.AskAsync("owner", text, default)).ConfigureAwait(false);
        Assert.Equal("proxy-service-unavailable", denied.Code);
        await offers.AskEachAsync([("owner", text)], null, default).ConfigureAwait(false);
        Assert.Equal(0, ordinary);
        Assert.Equal(0, confined);
        Assert.Single(manual);
    }

    [Fact]
    public async Task PartialRefreshWithdrawsOnlyTheSuccessfulPublicationAndKeepsTheFailedOne()
    {
        var owner = Text("owner.example.net");
        var other = Text("other.example.net");
        await _store.SaveConfigAsync("owner", owner).ConfigureAwait(false);
        await _store.SaveConfigAsync("other", other).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ownerPoint = ConfigServices.Target(owner)!;
        var otherPoint = ConfigServices.Target(other)!;
        var source = Guid.NewGuid().ToString("D");
        var ownerOffer = Offer(source, ownerPoint.ServerKey, key, "old-owner.example.net");
        var otherOffer = Offer(Guid.NewGuid().ToString("D"), otherPoint.ServerKey, key, "old-other.example.net");
        var catalog = TransportProxyCatalog.For(_store);
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(ownerOffer, true)))
            .AskAsync("owner", owner, default).ConfigureAwait(false);
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(otherOffer, true)))
            .AskAsync("other", other, default).ConfigureAwait(false);
        var reference = Assert.Single(await catalog.ListAsync().ConfigureAwait(false), one => one.Host == "old-other.example.net").Reference;
        await catalog.SelectAsync("owner", reference, enabled: false).ConfigureAwait(false);
        var withdrawn = Offer(source, ownerPoint.ServerKey, key, "unused.example.net", empty: true);
        var offers = new ServerOffers(_store, ask: (target, _) => Task.FromResult(target.ServerKey == ownerPoint.ServerKey
            ? new HelloReply(withdrawn, true) : new HelloReply(null, false, "hello-tls")));

        var result = await offers.RefreshProxiesAsync("owner", [("owner", owner), ("other", other)], null, default).ConfigureAwait(false);

        Assert.Equal(new ProxyCatalogRefreshFailure("other", "hello-tls"), Assert.Single(result.Failures));
        Assert.DoesNotContain(result.Items, one => one.Host == "old-owner.example.net");
        Assert.Contains(result.Items, one => one.Reference == reference && !one.Missing);
        Assert.Equal(otherOffer.Features.Keys, (await offers.OfferAsync("other", other, default).ConfigureAwait(false)).Features.Keys);
        var selected = await catalog.SelectionAsync("owner").ConfigureAwait(false);
        Assert.Equal(reference, selected.Reference);
        Assert.False(selected.Enabled);
    }

    [Fact]
    public async Task RejectedSourceProofReportsFailureAndRetainsThePreviousRecords()
    {
        var text = Text("owner.example.net");
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = Guid.NewGuid().ToString("D");
        var point = ConfigServices.Target(text)!;
        var old = Offer(source, point.ServerKey, key, "old.example.net");
        await new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(old, true)))
            .AskAsync("owner", text, default).ConfigureAwait(false);
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rejected = Offer(source, point.ServerKey, wrong, "untrusted.example.net", empty: true);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(rejected, true)));

        var result = await offers.RefreshProxiesAsync("owner", [("owner", text)], null, default).ConfigureAwait(false);

        Assert.NotEmpty(result.Failures);
        Assert.StartsWith("proxy-source-", result.Failures[0].Code);
        Assert.Equal("old.example.net", Assert.Single(result.Items).Host);
    }

    [Fact]
    public async Task CancellingRefreshDoesNotReportCompletion()
    {
        var text = Text("vpn.example.net");
        await _store.SaveConfigAsync("owner", text).ConfigureAwait(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offers = new ServerOffers(_store, ask: async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HelloReply(null, false);
        });
        using var cancellation = new CancellationTokenSource();
        var refresh = offers.RefreshProxiesAsync("owner", [("owner", text)], null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh).ConfigureAwait(false);
    }

    private static string Text(string host)
    {
        var client = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return "# AmneziaGeo Services = 8443\n[Interface]\nPrivateKey = " + client
            + "\nAddress = 10.88.0.2/32\n[Peer]\nPublicKey = " + Curve25519.PublicOf(server)
            + "\nEndpoint = " + host + ":51820\nAllowedIPs = 0.0.0.0/0\n";
    }

    private static ServerOffer Offer(string source, string issuer, ECDsa key, string host, bool empty = false)
    {
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + source + "\n" + issuer);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), issuer,
            Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var item = new OfferedProxy("one", "relay", "websocket", host, 443, "relay",
            new OfferedProxyTls(host, ""), new OfferedProxyAuth("bearer", "test-credential"), DateTimeOffset.MinValue);
        return ServerOffer.Parse(JsonSerializer.Serialize(new
        {
            server = "amneziageo", version = "1", client = "client",
            features = new { proxies = new OfferedProxyCatalog(1, source, empty ? [] : [item], proof) },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
