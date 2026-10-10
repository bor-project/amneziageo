using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A request that has to be proven takes a certificate the machine proves, one that names the host asked and chains
/// to a root the client carries, or the one pinned; where the head hands a way, the request travels it.
/// </summary>
public sealed class ProvenHttpTests
{
    [Fact]
    public void TheRootsTheClientCarries_AreTheTwoOfLetsEncrypt()
    {
        var marks = CarriedRoots.Isrg.Select(root => Convert.ToHexString(SHA256.HashData(root.RawData))).ToList();

        Assert.Equal(
            ["96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6", "69729B8E15A86EFC177A57AFB7171DFC64ADD28C2FCA8CF1507E34453CCB1470"],
            marks);
    }

    [Fact]
    public async Task ACertificateTheMachineDoesNotProve_IsTakenUnderARootTheClientCarries()
    {
        using var panel = new TlsPanel(body: "feed");
        using var http = new GeoHttp(new HttpClient(), NullLogger<GeoHttp>.Instance, roots: [panel.Root]);

        using var response = await http.SendVerifiedAsync(Get(panel), HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("feed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACertificateUnderARootTheClientDoesNotCarry_StaysAnError()
    {
        using var panel = new TlsPanel();
        using var other = new TlsPanel();
        using var http = new GeoHttp(new HttpClient(), NullLogger<GeoHttp>.Instance, roots: [other.Root]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendVerifiedAsync(Get(panel), HttpCompletionOption.ResponseContentRead, CancellationToken.None));
    }

    [Fact]
    public async Task ACertificateOfAnotherName_StaysAnErrorUnderACarriedRoot()
    {
        using var panel = new TlsPanel(name: "other.test");
        using var http = new GeoHttp(new HttpClient(), NullLogger<GeoHttp>.Instance, roots: [panel.Root]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendVerifiedAsync(Get(panel), HttpCompletionOption.ResponseContentRead, CancellationToken.None));
    }

    [Fact]
    public async Task ThePinnedCertificate_IsTakenWhereNoRootProvesIt()
    {
        using var panel = new TlsPanel(body: "feed");
        using var other = new TlsPanel();
        using var http = new GeoHttp(new HttpClient(), NullLogger<GeoHttp>.Instance, roots: [other.Root]);

        using var response = await http.SendPinnedAsync(Get(panel), panel.Pin, HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        Assert.Equal("feed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheWayTheHeadHands_CarriesTheProvenRequests()
    {
        using var panel = new TlsPanel(body: "feed");
        var dialed = 0;
        using var http = new GeoHttp(
            new HttpClient(new Refusing()),
            NullLogger<GeoHttp>.Instance,
            async (context, ct) =>
            {
                Interlocked.Increment(ref dialed);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), ct);

                return new NetworkStream(socket, ownsSocket: true);
            },
            [panel.Root]);

        using var plain = await http.SendVerifiedAsync(Get(panel), HttpCompletionOption.ResponseContentRead, CancellationToken.None);
        using var pinned = await http.SendPinnedAsync(Get(panel), panel.Pin, HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        Assert.Equal("feed", await plain.Content.ReadAsStringAsync());
        Assert.Equal("feed", await pinned.Content.ReadAsStringAsync());
        Assert.Equal(2, dialed);
    }

    private static HttpRequestMessage Get(TlsPanel panel) => new(HttpMethod.Get, panel.Url("/sub/abc"));

    // The client of the head itself, which a request with a way handed must not touch.
    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the client of the head was asked");
    }
}
