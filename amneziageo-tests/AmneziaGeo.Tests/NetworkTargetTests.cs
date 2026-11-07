using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class NetworkTargetTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-network-target-" + Guid.NewGuid().ToString("N") + ".db");
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        _catalog = TransportProxyCatalog.For(_store);
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("xhttp", "169.254.193.2")]
    [InlineData("xhttp", "server.private.invalid")]
    [InlineData("websocket", "169.254.193.2")]
    [InlineData("websocket", "server.private.invalid")]
    public async Task AForeignSelectedCarrierChecksItsPublicHostWithoutResolvingTheUdpTarget(string transport, string udpHost)
    {
        var publisher = await ConfigAsync("publisher", "origin.example.net").ConfigureAwait(false);
        await ConfigAsync("consumer", udpHost).ConfigureAwait(false);
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = Guid.NewGuid().ToString("D");
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + source + "\n" + publisher.ServerKey);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), publisher.ServerKey,
            Convert.ToBase64String(identity.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var offer = new ServerOffer("1", "client", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesFeature] = JsonSerializer.SerializeToElement(new OfferedProxyCatalog(1,
                source, [Proxy(transport, "cdn.example.net")], proof), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        Assert.True(await _catalog.SynchronizeAsync("publisher", publisher, offer).ConfigureAwait(false));
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        await _catalog.SelectAsync("consumer", reference).ConfigureAwait(false);
        await _store.SetSettingAsync(TunnelPaths.EndpointIpKey("consumer"), "169.254.193.3").ConfigureAwait(false);
        var lookedUp = new List<string>();
        var publicAddress = IPAddress.Parse("203.0.113.17");

        var target = await ConfigRunner.NetworkTargetAsync(_store, "consumer", (host, _) =>
        {
            lookedUp.Add(host);
            Assert.Equal("cdn.example.net", host);
            return Task.FromResult<IPAddress?>(publicAddress);
        }).ConfigureAwait(false);

        Assert.True(target.Proxied);
        Assert.Equal(publicAddress, target.Address);
        Assert.Equal(["cdn.example.net"], lookedUp);
        Assert.True(target.Ready(address => address.Equals(publicAddress), () => throw new InvalidOperationException("Direct underlay fallback")));
        Assert.False(target.Ready(_ => false, () => true));
    }

    [Theory]
    [InlineData("169.254.193.2", "198.51.100.9", "169.254.193.2")]
    [InlineData("server.private.invalid", "198.51.100.9", "198.51.100.9")]
    [InlineData("server.private.invalid", null, null)]
    public async Task ExplicitNoneKeepsTheDirectEndpointAndItsExistingCache(string host, string? cached, string? expected)
    {
        await ConfigAsync("consumer", host).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("consumer", true,
            WebSocketHost: "wss://legacy.example.net:9443/oldfront")).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", null).ConfigureAwait(false);
        if (cached is not null)
        {
            await _store.SetSettingAsync(TunnelPaths.EndpointIpKey("consumer"), cached).ConfigureAwait(false);
        }

        var target = await ConfigRunner.NetworkTargetAsync(_store, "consumer",
            (_, _) => throw new InvalidOperationException("A direct destination uses its existing address policy")).ConfigureAwait(false);

        Assert.False(target.Proxied);
        Assert.Equal(expected is null ? null : IPAddress.Parse(expected), target.Address);
        Assert.True(target.Ready(_ => true, () => true));
        Assert.False(target.Ready(_ => false, () => false));
    }

    [Fact]
    public async Task LegacyWebSocketMigrationChecksTheChosenFront()
    {
        await ConfigAsync("consumer", "169.254.193.2").ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("consumer", true,
            WebSocketHost: "wss://account:password@legacy.example.net:9443/oldfront")).ConfigureAwait(false);
        var hosts = new List<string>();

        var target = await ConfigRunner.NetworkTargetAsync(_store, "consumer", (host, _) =>
        {
            hosts.Add(host);
            return Task.FromResult<IPAddress?>(IPAddress.Parse("203.0.113.18"));
        }).ConfigureAwait(false);

        Assert.Equal(["legacy.example.net"], hosts);
        Assert.True(target.Proxied);
        Assert.Equal(IPAddress.Parse("203.0.113.18"), target.Address);
        Assert.True((await _catalog.ResolveAsync("consumer").ConfigureAwait(false))!.Record.Legacy);
    }

    [Fact]
    public async Task AnUnresolvedProxyCannotUseTheUdpCacheOrTheDirectUnderlayFallback()
    {
        await SelectLocalAsync("unresolved.example.net").ConfigureAwait(false);
        await _store.SetSettingAsync(TunnelPaths.EndpointIpKey("consumer"), "198.51.100.9").ConfigureAwait(false);

        var target = await ConfigRunner.NetworkTargetAsync(_store, "consumer", (host, _) =>
        {
            Assert.Equal("unresolved.example.net", host);
            return Task.FromResult(default(IPAddress));
        }).ConfigureAwait(false);

        Assert.True(target.Proxied);
        Assert.Null(target.Address);
        Assert.False(target.Ready(_ => throw new InvalidOperationException("No proxy address"),
            () => throw new InvalidOperationException("Direct underlay fallback")));
    }

    [Theory]
    [InlineData(false, "proxy-missing")]
    [InlineData(true, "bad-proxy")]
    public async Task ARejectedSelectionFailsBeforeAnyAddressOrDirectFallback(bool invalid, string expected)
    {
        var reference = await SelectLocalAsync("proxy.example.net").ConfigureAwait(false);
        if (invalid)
        {
            var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
            var corrupted = state with
            {
                Revision = state.Revision + 1,
                Locals = [.. state.Locals.Select(one => one.Reference == reference ? one with { Proxy = one.Proxy with { Port = 0 } } : one)],
            };
            Assert.True(await _store.TrySaveTransportProxyStateAsync(corrupted, state.Revision).ConfigureAwait(false));
        }
        else
        {
            await _catalog.RemoveLocalAsync(reference).ConfigureAwait(false);
        }

        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => ConfigRunner.NetworkTargetAsync(_store, "consumer",
            (_, _) => throw new InvalidOperationException("Rejected selection reached DNS"))).ConfigureAwait(false);

        Assert.Equal(expected, error.Code);
        Assert.Equal(reference, (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task TheNextNetworkLookResolvesAnUpdatedProxyHost()
    {
        var reference = await SelectLocalAsync("old.example.net").ConfigureAwait(false);
        var hosts = new List<string>();
        var oldAddress = IPAddress.Parse("203.0.113.19");
        var newAddress = IPAddress.Parse("203.0.113.20");
        Task<IPAddress?> Resolve(string host, CancellationToken ct)
        {
            hosts.Add(host);
            return Task.FromResult<IPAddress?>(host == "old.example.net" ? oldAddress : newAddress);
        }

        var before = await ConfigRunner.NetworkTargetAsync(_store, "consumer", Resolve).ConfigureAwait(false);
        Assert.False(before.Ready(address => address.Equals(newAddress), () => true));
        await _catalog.SaveLocalAsync(reference.Id, Proxy("xhttp", "new.example.net")).ConfigureAwait(false);

        var after = await ConfigRunner.NetworkTargetAsync(_store, "consumer", Resolve).ConfigureAwait(false);

        Assert.Equal(["old.example.net", "new.example.net"], hosts);
        Assert.True(after.Ready(address => address.Equals(newAddress), () => false));
        Assert.Equal(reference, (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
    }

    private async Task<ProxyReference> SelectLocalAsync(string host)
    {
        await ConfigAsync("consumer", "server.private.invalid").ConfigureAwait(false);
        var reference = await _catalog.SaveLocalAsync(null, Proxy("xhttp", host)).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", reference).ConfigureAwait(false);
        return reference;
    }

    private async Task<ServiceTarget> ConfigAsync(string name, string host)
    {
        var text = "[Interface]\nPrivateKey = " + Key("client-" + name) + "\nAddress = 10.8.0.2/32\n[Peer]\nPublicKey = "
            + Key("server-" + name) + "\nEndpoint = " + host + ":51820\nAllowedIPs = 0.0.0.0/0\n";
        await _store.SaveConfigAsync(name, text).ConfigureAwait(false);
        return ConfigServices.Target(text)!;
    }

    private static string Key(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static OfferedProxy Proxy(string transport, string host) => new("one", "Proxy", transport, host, 443, "transport",
        new OfferedProxyTls(host, ""), new OfferedProxyAuth(transport == "xhttp" ? "vless" : "bearer",
            transport == "xhttp" ? "6caf6428-8823-4165-bbc3-10f07eef6ab9" : "credential"), DateTimeOffset.MinValue,
        transport == "xhttp" ? new OfferedXHttp("packet-up", "GET", "header") : null);
}
