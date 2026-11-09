using System.Text.Json;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class OfferedProxyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private const string Source = "286a6fc4-d184-4e58-a344-a25768c8206d";

    [Fact]
    public void AnAbsentCatalogDiffersFromAnEmptyOrMalformedCatalog()
    {
        var old = ServerOffer.Parse("""{"server":"amneziageo","version":"1","features":{"websocket":{"port":443,"path":"legacy"}}}""");
        var empty = Offer(new OfferedProxyCatalog(1, Source, []));
        var malformed = ServerOffer.Parse("""{"server":"amneziageo","version":"1","features":{"proxies":[]}}""");
        Assert.False(old.HasProxyCatalog);
        Assert.Null(old.ProxyCatalog);
        Assert.Equal(443, old.WebSocketPort);
        Assert.True(empty.HasProxyCatalog);
        Assert.Empty(empty.ProxyCatalog!.Items);
        Assert.True(malformed.HasProxyCatalog);
        Assert.Null(malformed.ProxyCatalog);
    }

    [Fact]
    public void MultipleProxiesAndTheCdnHostSurviveTheOfferRoundTrip()
    {
        var ws = Proxy("one");
        var xhttp = Proxy("two") with
        {
            Transport = "xhttp", Auth = new OfferedProxyAuth("vless", "3204d931-b4b1-429d-bea1-23972505b3f5"),
            Xhttp = new OfferedXHttp("packet-up", "GET", "header"),
        };
        var read = ServerOffer.Parse(Offer(new OfferedProxyCatalog(1, Source, [ws, xhttp])).ToPayload()).ProxyCatalog!;
        Assert.Equal([ws, xhttp], read.Items);
        Assert.Equal("cdn.example.net", read.Items[1].Host);
        Assert.NotEqual(new ProxyReference(Source, "one"), new ProxyReference("a241c7dd-e467-4fac-9e4d-a4dc12644804", "one"));
    }

    [Fact]
    public void ARepeatedIdOrUnsupportedTransportInvalidatesTheSnapshot()
    {
        Assert.Null(Offer(new OfferedProxyCatalog(1, Source, [Proxy("one"), Proxy("one")])).ProxyCatalog);
        Assert.Null(Offer(new OfferedProxyCatalog(1, Source, [Proxy("one") with { Transport = "other" }])).ProxyCatalog);
        Assert.Null(Offer(new OfferedProxyCatalog(3, Source, [])).ProxyCatalog);
    }

    [Theory]
    [InlineData("POST", "header")]
    [InlineData("GET", "body")]
    public void XHttpAcceptsOnlyTheGetHeaderProfile(string method, string placement)
    {
        var proxy = Proxy("one") with
        {
            Transport = "xhttp", Auth = new OfferedProxyAuth("vless", "3204d931-b4b1-429d-bea1-23972505b3f5"),
            Xhttp = new OfferedXHttp("packet-up", method, placement),
        };
        Assert.Null(Offer(new OfferedProxyCatalog(1, Source, [proxy])).ProxyCatalog);
    }

    private static OfferedProxy Proxy(string id) => new(
        id, "Proxy " + id, "websocket", "cdn.example.net", 443, "transport",
        new OfferedProxyTls("cdn.example.net", ""), new OfferedProxyAuth("bearer", "credential"),
        DateTimeOffset.Parse("2026-11-04T10:00:00Z", null));

    private static ServerOffer Offer(OfferedProxyCatalog catalog) => new(
        "1", "phone", new Dictionary<string, JsonElement> { [ServerOffer.ProxiesFeature] = JsonSerializer.SerializeToElement(catalog, Web) });
}
