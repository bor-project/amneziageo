using System.Net;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Localization;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class ProxyRuntimeProfileTests
{
    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public void ExplicitHttpModeReachesTheNativeProfile(string method, bool certificate)
    {
        var proxy = ProxyTransportModesTests.Http(method, certificate);
        var selected = Selected(proxy);
        using var json = JsonDocument.Parse(ProxyRuntimeProfile.Create(selected, 10222, IPAddress.Parse("192.0.2.10")));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal("http", root.GetProperty("transport").GetString());
        Assert.Equal(method, root.GetProperty("http").GetProperty("method").GetString());
        Assert.Equal(method == "GET" ? "header" : "body", root.GetProperty("http").GetProperty("dataPlacement").GetString());
        Assert.Equal(certificate, root.GetProperty("front").GetProperty("tls").GetProperty("enabled").GetBoolean());
        Assert.Equal("192.0.2.10", root.GetProperty("front").GetProperty("dialAddress").GetString());
        Assert.Equal(proxy.Host, root.GetProperty("front").GetProperty("host").GetString());
        Assert.Equal("udp.example", root.GetProperty("target").GetProperty("host").GetString());
        Assert.Equal(51820, root.GetProperty("target").GetProperty("port").GetInt32());
        Assert.Equal("127.0.0.1", root.GetProperty("listen").GetProperty("host").GetString());
        Assert.Equal(10222, root.GetProperty("listen").GetProperty("port").GetInt32());
        Assert.Equal("hmac-sha256-v2", root.GetProperty("auth").GetProperty("scheme").GetString());
    }

    [Fact]
    public void LegacyConfirmedGetRetainsThePlainScheme()
    {
        var proxy = ProxyTransportModesTests.Http("GET", true) with { Transport = "xhttp", Http = null,
            Xhttp = new OfferedXHttp("packet-up", "GET", "header", Scheme: "http", Version: 2),
            Auth = new("vless", "22222222222222222222222222222222") };
        using var json = JsonDocument.Parse(ProxyRuntimeProfile.Create(Selected(proxy), 10222, IPAddress.Loopback));
        Assert.False(json.RootElement.GetProperty("front").GetProperty("tls").GetProperty("enabled").GetBoolean());
        Assert.Equal("GET", json.RootElement.GetProperty("http").GetProperty("method").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", json.RootElement.GetProperty("auth").GetProperty("credential").GetString());
    }

    [Fact]
    public void QuicUsesBearerAndASeparateAvailabilityProfile()
    {
        var proxy = Quic();
        using var json = JsonDocument.Parse(ProxyRuntimeProfile.Probe(proxy, IPAddress.Loopback));
        Assert.Equal("quic", json.RootElement.GetProperty("transport").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("quic").GetProperty("version").GetInt32());
        Assert.Equal("framed-stream", json.RootElement.GetProperty("quic").GetProperty("datagrams").GetString());
        Assert.Equal("bearer", json.RootElement.GetProperty("auth").GetProperty("scheme").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("target").GetProperty("port").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("listen").GetProperty("port").GetInt32());
        Assert.Equal("bad-proxy", Assert.Throws<TransportProxyFault>(() => ProxyRuntimeProfile.Probe(
            proxy with { Tls = proxy.Tls with { Enabled = false } }, IPAddress.Loopback)).Code);
    }

    [Theory]
    [InlineData("proxy-auth")]
    [InlineData("proxy-tls")]
    [InlineData("proxy-target-denied")]
    [InlineData("proxy-bad-answer")]
    [InlineData("proxy-rejected")]
    public void AuthoritativeFailuresAreTransportRejections(string code)
    {
        Assert.Equal(ConnectFailureReason.TransportRejected, TunnelRunner.ProxyFailureReason(code));
        Assert.Equal(code, XHttpProcess.ReadFailure("PROXY 2 failed " + code));
        Assert.Equal("", XHttpProcess.ReadFailure("PROXY 2 failed unrecognized-data"));
        Assert.Equal("", XHttpProcess.ReadFailure("noise PROXY 2 failed " + code));
        Assert.Equal(ConnectFailureReason.UnderlayUnreachable, TunnelRunner.ProxyFailureReason("proxy-no-answer"));
    }

    [Fact]
    public async Task ModernCancelledStartDoesNotLaunchTheChild()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XHttpProcess.StartTransportAsync(
            Selected(Quic()), IPAddress.Loopback, stop.Token)).ConfigureAwait(false);
    }

    [Fact]
    public void RejectedConnectionsHaveAnExplicitMessage()
    {
        Assert.Equal(Loc.Instance.Get("Proxies_Rejected"), TransportProxyWords.Fault("proxy-rejected"));
        Assert.NotEqual(TransportProxyWords.Fault("proxy-no-answer"), TransportProxyWords.Fault("proxy-rejected"));
    }

    private static OfferedProxy Quic() => new("quic", "test", "quic", "proxy.example", 443, "transport",
        new("proxy.example", ""), new("bearer", "fixture-credential"), DateTimeOffset.MinValue,
        Quic: new(1, "webtransport", "framed-stream"));

    private static ResolvedTransportProxy Selected(OfferedProxy proxy) => new(
        new CatalogTransportProxy(new ProxyReference("22222222-2222-2222-2222-222222222222", proxy.Id), proxy),
        "test", "udp.example", 51820);
}
