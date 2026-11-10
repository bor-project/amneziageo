using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class ProxyTlsSecurityTests
{
    [Theory]
    [InlineData(System.Net.Sockets.AddressFamily.InterNetwork, 10, 20, 10)]
    [InlineData(System.Net.Sockets.AddressFamily.InterNetworkV6, 10, 20, 20)]
    [InlineData(System.Net.Sockets.AddressFamily.InterNetworkV6, 10, null, null)]
    [InlineData(System.Net.Sockets.AddressFamily.InterNetwork, null, 10, null)]
    public void TunnelBindingUsesTheIndexOfTheDestinationAddressFamily(System.Net.Sockets.AddressFamily family, int? ipv4, int? ipv6, int? expected)
    {
        Assert.Equal((uint?)expected, AmneziaGeo.Windows.App.RouteManager.FamilyIndex(family, ipv4, ipv6));
    }

    [Theory]
    [InlineData("websocket", "GET")]
    [InlineData("http", "GET")]
    [InlineData("http", "POST")]
    public async Task ColdKnownTlsProxyStartsWithoutAnExternalOptionalHello(string transport, string method)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-cold-proxy-{Guid.NewGuid():N}.db");
        var store = new SqliteStateStore(path);
        await store.InitializeAsync().ConfigureAwait(false);
        try
        {
            var text = $"[Interface]\nPrivateKey = {Key()}\n[Peer]\nPublicKey = {Key()}\nEndpoint = hidden.example:51820\n";
            await store.SaveConfigAsync("vpn", text).ConfigureAwait(false);
            var definition = transport == "http" ? ProxyTransportModesTests.Http(method, true)
                : new OfferedProxy("proxy", "Proxy", "websocket", "front.example", 443, "relay", new("front.example", ""),
                    new("bearer", "fixture"), DateTimeOffset.MinValue);
            var catalog = new TransportProxyCatalog(store);
            var reference = await catalog.SaveLocalAsync(null, definition).ConfigureAwait(false);
            await catalog.SelectAsync("vpn", reference).ConfigureAwait(false);
            var before = (await store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
            var outside = 0;
            var refused = false;
            var offers = new ServerOffers(store, note: (_, error) => refused |= error is TransportProxyFault,
                ask: (_, _) =>
                {
                    outside++;
                    return Task.FromResult(new HelloReply(null, false));
                });
            Assert.False((await offers.BeforeConnectAsync("vpn", text, null, CancellationToken.None).ConfigureAwait(false)).Ours);
            Assert.True(refused);
            Assert.Equal(0, outside);
            Assert.Equal(reference, (await catalog.ResolveAsync("vpn").ConfigureAwait(false))!.Record.Reference);
            Assert.Equal(before, (await store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        }
        finally
        {
            store.ClearPool();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ARefusedTunnelBindingNeverConnectsOrResolvesAnotherPath()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var held = default(System.Net.Sockets.Socket);
        await Assert.ThrowsAsync<IOException>(async () =>
            await TunnelServiceConnection.ConnectAsync(new System.Net.DnsEndPoint("127.0.0.1", port), (_, socket, _) =>
            {
                held = socket;
                return ValueTask.FromResult(false);
            }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.NotNull(held);
        Assert.True(held.SafeHandle.IsClosed);
        Assert.False(listener.Pending());
        var bound = false;
        await Assert.ThrowsAsync<IOException>(async () =>
            await TunnelServiceConnection.ConnectAsync(new System.Net.DnsEndPoint("hidden.example", port), (_, _, _) =>
            {
                bound = true;
                return ValueTask.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.False(bound);
    }

    [Fact]
    public async Task CancellingTlsHandshakeClosesTheConnectionBeforeAnyHttpProof()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var record = new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "proxy"),
            ProxyTransportModesTests.Http("POST", true) with { Port = port });
        var accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        var checking = TransportProxyProbes.CheckAsync(record, cancel.Token, connect: async (_, ct) =>
        {
            var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
            return socket.GetStream();
        });
        using var peer = await accepted.ConfigureAwait(false);
        var preamble = new byte[5];
        await peer.GetStream().ReadExactlyAsync(preamble, deadline.Token).ConfigureAwait(false);
        Assert.Equal(0x16, preamble[0]);
        await cancel.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking).ConfigureAwait(false);
        using var received = new MemoryStream();
        received.Write(preamble);
        await peer.GetStream().CopyToAsync(received, deadline.Token).ConfigureAwait(false);
        var bytes = received.ToArray();
        Assert.NotEmpty(bytes);
        Assert.DoesNotContain("X-WG-", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(record.Proxy.Auth.Credential, System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine("tcp:accepted -> tls:client-hello -> cancellation -> socket:closed; no HTTP proof");
    }

    [Theory]
    [InlineData("localhost", false, true)]
    [InlineData("wrong.example", false, false)]
    [InlineData("localhost", true, false)]
    public async Task HelloRequiresTheTrustedCertificateIdentity(string name, bool wrongPin, bool accepted)
    {
        using var panel = new TlsPanel(status: 404);
        var point = new ServiceTarget("localhost", panel.Port, Key(), Key())
        {
            TlsName = name, TlsPin = wrongPin ? new string('0', 64) : panel.Pin,
        };
        var reply = await ServerHello.AskAsync(point, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(accepted, reply.Heard);
        Assert.Equal(accepted ? 1 : 0, panel.Served);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 10)]
    public async Task PinnedHelloRejectsExpiredAndFutureCertificatesBeforeProof(int begins, int ends)
    {
        using var panel = new TlsPanel(status: 404, leafBegins: begins, leafEnds: ends);
        var point = new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = panel.Pin };
        var reply = await ServerHello.AskAsync(point, CancellationToken.None).ConfigureAwait(false);
        Assert.False(reply.Heard);
        Assert.Equal(0, panel.Served);
    }

    [Fact]
    public async Task TrustedCaCompletesTlsWithoutPin()
    {
        using var panel = new TlsPanel(status: 404);
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(System.Net.IPAddress.Loopback, panel.Port).ConfigureAwait(false);
        using var tls = new SslStream(socket.GetStream());
        var options = ProxyTls.Options("localhost", "");
        options.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        options.CertificateChainPolicy.CustomTrustStore.Add(panel.Root);
        await tls.AuthenticateAsClientAsync(options).ConfigureAwait(false);
        Assert.True(tls.IsAuthenticated);
    }

    [Fact]
    public async Task HelloUsesTheExplicitTlsNameWhileDialingTheConfiguredAddress()
    {
        using var panel = new TlsPanel("hello.example", status: 404);
        var point = new ServiceTarget("127.0.0.1", panel.Port, Key(), Key()) { TlsName = "hello.example", TlsPin = panel.Pin };
        var reply = await ServerHello.AskAsync(point, CancellationToken.None).ConfigureAwait(false);
        Assert.True(reply.Heard);
        Assert.Equal("hello.example", panel.ServerName);
        Assert.Equal(1, panel.Served);
    }

    [Fact]
    public async Task UntrustedHelloSendsNoProof()
    {
        using var panel = new TlsPanel(status: 404);
        var reply = await ServerHello.AskAsync(new ServiceTarget("localhost", panel.Port, Key(), Key()), CancellationToken.None).ConfigureAwait(false);
        Assert.False(reply.Heard);
        Assert.Equal(0, panel.Served);
    }

    [Theory]
    [InlineData(-2, -1, false)]
    [InlineData(1, 2, false)]
    [InlineData(-1, 1, true)]
    public void PinNeverOverridesCertificateDates(int begins, int ends, bool accepted)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(begins), DateTimeOffset.UtcNow.AddDays(ends));
        var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var options = ProxyTls.Options("localhost", pin);
        Assert.Equal(accepted, options.RemoteCertificateValidationCallback!(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task PublicPlainHelloIsRejectedBeforeConnect()
    {
        var dialed = false;
        var point = new ServiceTarget("localhost", 443, Key(), Key()) { Origin = "http://localhost:12345" };
        await Assert.ThrowsAsync<TransportProxyFault>(() => ServerHello.AskAsync(point, (_, _) =>
        {
            dialed = true;
            throw new InvalidOperationException();
        }, CancellationToken.None)).ConfigureAwait(false);
        Assert.False(dialed);
    }

    [Fact]
    public async Task SelectedProxyBlocksExternalHelloAndSpeedWithoutRewritingLegacyState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-tls-policy-{Guid.NewGuid():N}.db");
        var store = new SqliteStateStore(path);
        await store.InitializeAsync().ConfigureAwait(false);
        try
        {
            var text = $"[Interface]\nPrivateKey = {Key()}\n[Peer]\nPublicKey = {Key()}\nEndpoint = hidden.example:51820\n";
            await store.SaveConfigAsync("vpn", text).ConfigureAwait(false);
            await store.SetConfigTransportAsync(new ConfigTransport("vpn", true, WebSocketHost: "ws://front.example:80/path")).ConfigureAwait(false);
            var catalog = new TransportProxyCatalog(store);
            var selection = await catalog.SelectionAsync("vpn").ConfigureAwait(false);
            var before = await store.GetTransportProxyStateAsync().ConfigureAwait(false);
            var legacy = Assert.Single(before.Locals);
            Assert.False(legacy.Proxy.Tls.Enabled);
            var fault = await Assert.ThrowsAsync<TransportProxyFault>(() => catalog.ResolveAsync("vpn")).ConfigureAwait(false);
            Assert.Equal("bad-proxy", fault.Code);
            var outside = 0;
            var offers = new ServerOffers(store, ask: (_, _) =>
            {
                outside++;
                return Task.FromResult(new HelloReply(null, true));
            });
            await Assert.ThrowsAsync<TransportProxyFault>(() => offers.AskAsync("vpn", text, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsAsync<TransportProxyFault>(() => offers.SpeedAsync("vpn", text, CancellationToken.None)).ConfigureAwait(false);
            Assert.Equal(0, outside);
            var after = await store.GetTransportProxyStateAsync().ConfigureAwait(false);
            Assert.Equal(selection, Assert.Single(after.Selections));
            Assert.Equal(legacy, Assert.Single(after.Locals));
            Assert.Equal(before.ToPayload(), after.ToPayload());
            var portable = await TransportProxyPortability.ExportAsync(store, "vpn").ConfigureAwait(false);
            await store.SaveConfigAsync("imported", text).ConfigureAwait(false);
            await TransportProxyPortability.ApplyAsync(store, "imported", portable).ConfigureAwait(false);
            Assert.Equal("bad-proxy", (await Assert.ThrowsAsync<TransportProxyFault>(() => catalog.ResolveAsync("imported")).ConfigureAwait(false)).Code);
            var imported = (await store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals.Single(one => one.LegacyPublisher == "imported");
            Assert.False(imported.Proxy.Tls.Enabled);
            Assert.Equal(legacy.Proxy.Auth, imported.Proxy.Auth);
        }
        finally
        {
            store.ClearPool();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
