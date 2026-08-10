using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the websocket carrier puts on the wire. The front is another program's server, so neither the shape of
/// the upgrade nor the shape of a frame is ours to choose: a request it does not recognise reads to the user as
/// a network that refuses the tunnel, which is the one failure the carrier exists to remove.
/// </summary>
public sealed class WsCarrierTests
{
    [Fact]
    public void TheUpgrade_AsksForThePathThePrefixNames()
    {
        var request = WsCarrier.Handshake(new WsEndpoint("front.example.com", 8443, "secret", string.Empty), 51820, "dGhlIHNhbXBsZSBub25jZQ==");

        Assert.StartsWith("GET /secret/events HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Host: front.example.com:8443\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Version: 13\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Basic", request, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", request, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrontWithoutAPrefix_TakesTheDefaultPathAndCarriesItsCredentials()
    {
        var request = WsCarrier.Handshake(new WsEndpoint("front.example.com", 443, string.Empty, "bob:hunter2"), 51820, "key");

        Assert.StartsWith("GET /v1/events HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:hunter2"))}\r\n", request, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToken_SaysWhichUdpPortTheServerHandsTheTunnelTo()
    {
        var request = WsCarrier.Handshake(new WsEndpoint("front.example.com", 443, string.Empty, string.Empty), 51820, "key");

        var token = request.Split("authorization.bearer.")[1].Split("\r\n")[0];
        var parts = token.Split('.');
        var claims = Encoding.UTF8.GetString(Web(parts[1]));

        Assert.Equal(3, parts.Length);
        Assert.Equal("{\"typ\":\"JWT\",\"alg\":\"HS256\"}", Encoding.UTF8.GetString(Web(parts[0])));
        Assert.Contains("\"p\":{\"Udp\":{\"timeout\":null}}", claims, StringComparison.Ordinal);
        Assert.Contains("\"r\":\"127.0.0.1\"", claims, StringComparison.Ordinal);
        Assert.Contains("\"rp\":51820", claims, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatagram_TravelsUnmaskedBecauseTheFrontUnmasksNothing()
    {
        var datagram = Encoding.ASCII.GetBytes("a datagram the tunnel sends");
        var frame = new byte[datagram.Length + 4];

        var length = WsCarrier.Encode(frame, datagram, 0x2);

        Assert.Equal(datagram.Length + 2, length);
        Assert.Equal(0x82, frame[0]);
        Assert.Equal((byte)datagram.Length, frame[1]);
        Assert.Equal(datagram, frame.AsSpan(2, datagram.Length).ToArray());
    }

    [Fact]
    public void ADatagramTooLongForOneByte_CarriesItsLengthInTwoMore()
    {
        var datagram = Encoding.ASCII.GetBytes(new string('x', 1420));
        var frame = new byte[datagram.Length + 4];

        var length = WsCarrier.Encode(frame, datagram, 0x2);

        Assert.Equal(datagram.Length + 4, length);
        Assert.Equal(126, frame[1]);
        Assert.Equal(1420, (frame[2] << 8) | frame[3]);
        Assert.Equal(datagram, frame.AsSpan(4, datagram.Length).ToArray());
    }

    private static byte[] Web(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
