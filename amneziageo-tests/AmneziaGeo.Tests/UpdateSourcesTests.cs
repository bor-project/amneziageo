using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The device takes its releases from one source at a time: the one its installed copy came from, or a server of a
/// config that hands them out. A server picked by hand wins, a server that asks to be taken wins while nothing was
/// picked, a server gone gives way to the origin; the patches fit only the releases of the origin, and the source that
/// handed the setup becomes the origin once its version runs.
/// </summary>
public sealed class UpdateSourcesTests
{
    private const string Baked = "https://github.com/bor-project/amneziageo/releases/latest/download/update.json";
    private const string Home = "https://vpn.example.net:8443/u7f3/";
    private const string Work = "https://srv.example.org/updates/";
    private static readonly string HomePin = new('a', 64);
    private static readonly string WorkPin = new('b', 64);

    [Fact]
    public void TheHello_NamesTheServerOfTheReleasesWithItsPin()
    {
        var offer = Offer($$"""{"url":"https://vpn.example.net:8443/u7f3","default":true,"pin":"{{HomePin.ToUpperInvariant()}}"}""");

        Assert.Equal(new OfferedUpdates(Home, true, HomePin), offer.Updates());
        Assert.Equal(new OfferedUpdates(Work, false, string.Empty), Offer($$"""{"url":"{{Work}}","pin":"zz"}""").Updates());
        Assert.Null(Offer("""{"url":"ftp://vpn.example.net/u/"}""").Updates());
        Assert.Null(Offer("""{"url":"https://vpn.example.net/u/?a=1"}""").Updates());
        Assert.Null(Offer("""{"default":true}""").Updates());
        Assert.Null(ServerOffer.None.Updates());
    }

    [Fact]
    public void TheServers_AreOnePerAddressInTheOrderOfTheConfigs()
    {
        var servers = UpdateSources.Servers(
        [
            ("Дом", Offer($$"""{"url":"{{Home}}","pin":"{{HomePin}}"}""")),
            ("Работа", Offer($$"""{"url":"{{Work}}","default":true,"pin":"{{WorkPin}}"}""")),
            ("Дом 2", Offer($$"""{"url":"{{Home}}","default":true,"pin":"{{HomePin}}"}""")),
            ("Чужой", ServerOffer.None),
        ]);

        Assert.Equal(
            [new UpdateServer(Home, HomePin, true, "Дом"), new UpdateServer(Work, WorkPin, true, "Работа")],
            servers);
    }

    [Fact]
    public void NothingPicked_TakesTheReleasesOfTheProject()
    {
        var route = UpdateSources.Pick(Baked, UpdateOrigin.Baked, string.Empty, [Server(Home, false)]);

        Assert.Equal(new UpdateRoute(UpdateSources.OriginKey, Baked, string.Empty, false, true, string.Empty), route);
        Assert.Equal(Baked, route.Manifest(UpdateChannels.Test));
        Assert.Equal("GitHub", route.Host);
    }

    [Fact]
    public void AServerThatAsksToBeTaken_WinsWhileNothingIsPicked()
    {
        var route = UpdateSources.Pick(Baked, UpdateOrigin.Baked, string.Empty, [Server(Home, false), Server(Work, true)]);

        Assert.Equal(new UpdateRoute(Work, Work, WorkPin, true, false, string.Empty), route);
        Assert.Equal(Work + "beta/update.json", route.Manifest(UpdateChannels.Beta));
        Assert.Equal("srv.example.org", route.Host);
    }

    [Fact]
    public void APickByHand_WinsOverTheServerThatAsks()
    {
        var servers = new[] { Server(Home, false), Server(Work, true) };

        Assert.Equal(UpdateSources.OriginKey, UpdateSources.Pick(Baked, UpdateOrigin.Baked, UpdateSources.OriginKey, servers).Key);
        Assert.Equal(new UpdateRoute(Home, Home, HomePin, true, false, string.Empty), UpdateSources.Pick(Baked, UpdateOrigin.Baked, Home, servers));
    }

    [Fact]
    public void AServerPickedByHandThatHandsOutNoMore_GivesWayToTheOriginAndSaysSo()
    {
        var route = UpdateSources.Pick(Baked, UpdateOrigin.Baked, Home, [Server(Work, true)]);

        Assert.Equal(UpdateSources.OriginKey, route.Key);
        Assert.False(route.Server);
        Assert.Equal("vpn.example.net:8443", route.Gone);
        Assert.Equal(UpdateSources.GoneKey, UpdateSources.FaultKey(route, true, null));
    }

    [Fact]
    public void AnOriginServerStillNamed_IsTheOriginWithThePinOfTheHello()
    {
        var origin = new UpdateOrigin(Home, WorkPin, "1.11.0.904");
        var route = UpdateSources.Pick(Baked, origin, string.Empty, [Server(Home, true)]);

        Assert.Equal(new UpdateRoute(UpdateSources.OriginKey, Home, HomePin, true, true, string.Empty), route);
        Assert.Equal(route, UpdateSources.Pick(Baked, origin, Home, [Server(Home, false)]));
    }

    [Fact]
    public void AnOriginServerNoLongerNamed_LeavesTheReleasesOfTheProjectWithoutPatches()
    {
        var route = UpdateSources.Pick(Baked, new UpdateOrigin(Home, HomePin, "1.11.0.904"), UpdateSources.OriginKey, []);

        Assert.Equal(new UpdateRoute(UpdateSources.OriginKey, Baked, string.Empty, false, false, string.Empty), route);
    }

    [Fact]
    public void TheList_PutsTheOriginFirstAndNamesEveryOtherServer()
    {
        var servers = new[] { Server(Home, true), Server(Work, false) };

        Assert.Equal(
            [
                new UpdateSourceEntry(UpdateSources.OriginKey, "GitHub", string.Empty, false, "github.com/bor-project/amneziageo/releases"),
                new UpdateSourceEntry(Home, "vpn.example.net:8443", "Дом", true, "vpn.example.net:8443/u7f3"),
                new UpdateSourceEntry(Work, "srv.example.org", "Работа", false, "srv.example.org/updates"),
            ],
            UpdateSources.Entries(Baked, UpdateOrigin.Baked, servers));
        Assert.Equal(
            [
                new UpdateSourceEntry(UpdateSources.OriginKey, "vpn.example.net:8443", "Дом", true, "vpn.example.net:8443/u7f3"),
                new UpdateSourceEntry(Work, "srv.example.org", "Работа", false, "srv.example.org/updates"),
            ],
            UpdateSources.Entries(Baked, new UpdateOrigin(Home, HomePin, "1.11.0.904"), servers));
    }

    [Fact]
    public void TheSourceThatHandedTheSetup_BecomesTheOriginOnceItsVersionRuns()
    {
        var handed = new UpdateRoute(Home, Home, HomePin, true, false, string.Empty).Handed("1.11.0.905");

        Assert.Equal(new UpdateOrigin(Home, HomePin, "1.11.0.905"), handed);
        Assert.Equal((UpdateOrigin.Baked, handed), UpdateSources.Settle(UpdateOrigin.Baked, handed, "1.11.0.904"));
        Assert.Equal((handed, (UpdateOrigin?)null), UpdateSources.Settle(UpdateOrigin.Baked, handed, "1.11.0.905"));
        Assert.Equal((handed, (UpdateOrigin?)null), UpdateSources.Settle(UpdateOrigin.Baked, handed, "1.11.1.0"));
        Assert.Equal((handed, (UpdateOrigin?)null), UpdateSources.Settle(handed, null, "1.11.0.905"));
        Assert.Equal(UpdateOrigin.Baked with { Version = "1.11.0.906" }, UpdateSources.Pick(Baked, handed, UpdateSources.OriginKey, []).Handed("1.11.0.906"));
    }

    [Fact]
    public void TheOrigin_ReadsBackAsItWasWritten()
    {
        var origin = new UpdateOrigin(Home, HomePin, "1.11.0.905");

        Assert.Equal(origin, UpdateOrigin.Read(origin.Write()));
        Assert.Equal(UpdateOrigin.Baked, UpdateOrigin.Read(UpdateOrigin.Baked.Write()));
        Assert.Null(UpdateOrigin.Read("{"));
        Assert.Null(UpdateOrigin.Read(string.Empty));
    }

    [Fact]
    public void ThePatches_FitOnlyTheReleasesOfTheOrigin()
    {
        var asset = new UpdateAsset("AmneziaGeo-1.11.0.905-win-x64.exe", "windows", "x64", "scd", "ab", 10,
            new UpdatePatchFile("AmneziaGeo-1.11.0.905-win-x64.patch", 5, "cd", "1.11.0.904"));

        Assert.Same(asset, UpdateSources.Fitting(asset, new UpdateRoute(UpdateSources.OriginKey, Home, HomePin, true, true, string.Empty)));
        Assert.Null(UpdateSources.Fitting(asset, new UpdateRoute(Work, Work, WorkPin, true, false, string.Empty))!.Patch);
        Assert.Null(UpdateSources.Fitting(null, new UpdateRoute(Work, Work, WorkPin, true, false, string.Empty)));
    }

    [Fact]
    public void TheFault_SaysWhyAServerGaveNoRelease()
    {
        var server = new UpdateRoute(Home, Home, HomePin, true, false, string.Empty);
        var project = new UpdateRoute(UpdateSources.OriginKey, Baked, string.Empty, false, true, string.Empty);

        Assert.Equal(UpdateSources.NoReleaseKey, UpdateSources.FaultKey(server, false, new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
        Assert.Equal(UpdateSources.NoReleaseKey, UpdateSources.FaultKey(server, false, null));
        Assert.Equal(UpdateSources.NoReleaseKey, UpdateSources.FaultKey(server, false, new JsonException()));
        Assert.Equal(UpdateSources.CertificateKey, UpdateSources.FaultKey(server, false,
            new HttpRequestException(HttpRequestError.SecureConnectionError, "rejected", new AuthenticationException("the remote certificate was rejected"))));
        Assert.Equal(UpdateSources.UnreachableKey, UpdateSources.FaultKey(server, false,
            new HttpRequestException(HttpRequestError.SecureConnectionError, "cut", new IOException("unexpected EOF"))));
        Assert.Equal(UpdateSources.UnreachableKey, UpdateSources.FaultKey(server, false, new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.Equal(UpdateSources.UnreachableKey, UpdateSources.FaultKey(server, false, new TaskCanceledException()));
        Assert.Equal(string.Empty, UpdateSources.FaultKey(server, true, null));
        Assert.Equal(string.Empty, UpdateSources.FaultKey(project, false, new HttpRequestException(HttpRequestError.ConnectionError)));
    }

    [Fact]
    public void ANewFailure_StartsAgainWhileTheSameOneHoldsItsStart()
    {
        var first = UpdateSources.Next(UpdateFault.None, "a", DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var held = UpdateSources.Next(first, "a", DateTimeOffset.FromUnixTimeMilliseconds(5000));
        var other = UpdateSources.Next(held, "b", DateTimeOffset.FromUnixTimeMilliseconds(9000));

        Assert.Equal(new UpdateFault("a", 1000), held);
        Assert.Equal(new UpdateFault("b", 9000), other);
        Assert.Same(UpdateFault.None, UpdateSources.Next(other, string.Empty, DateTimeOffset.FromUnixTimeMilliseconds(9500)));
    }

    [Fact]
    public void TheWords_NameTheChannelsAndThePicks()
    {
        Assert.Equal(UpdateChannels.Test, UpdateChannels.Of(" TEST ", UpdateChannels.Stable));
        Assert.Equal(UpdateChannels.Stable, UpdateChannels.Of("nightly", UpdateChannels.Stable));
        Assert.Equal(UpdateChannels.Beta, UpdateChannels.Of(true));
        Assert.False(UpdateChannels.Prerelease(UpdateChannels.Stable));
        Assert.True(UpdateChannels.Prerelease(UpdateChannels.Test));

        Assert.Equal(string.Empty, UpdateSources.Choice(string.Empty));
        Assert.Equal(string.Empty, UpdateSources.Choice("Auto"));
        Assert.Equal(UpdateSources.OriginKey, UpdateSources.Choice("ORIGIN"));
        Assert.Equal(Home, UpdateSources.Choice("https://vpn.example.net:8443/u7f3"));
        Assert.Null(UpdateSources.Choice("vpn.example.net"));
    }

    [Fact]
    public void APin_GivesOneClientAndNoPinNone()
    {
        Assert.Null(UpdateSources.Pinned(string.Empty));
        Assert.Same(UpdateSources.Pinned(HomePin), UpdateSources.Pinned(HomePin));
    }

    [Fact]
    public async Task AServer_IsReadAtTheManifestOfTheChannel()
    {
        using var http = new HttpClient(new Answers(new Dictionary<string, string>
        {
            [Home + "test/update.json"] = """{"version":"1.11.0.905","channel":"test"}""",
        }));
        var read = new List<string>();

        var release = await UpdateSources.FetchAsync(http, new UpdateRoute(Home, Home, string.Empty, true, false, string.Empty), UpdateChannels.Test, read.Add, CancellationToken.None);

        Assert.Equal("1.11.0.905", release!.Manifest.Version);
        Assert.Equal(new Uri(Home + "test/update.json"), release.BaseUrl);
        Assert.Equal([Home + "test/update.json"], read);
        Assert.False(UpdateFeed.IsUpdate(release.Manifest.Version!, "1.11.0.906"));
    }

    [Fact]
    public async Task AServerWithoutAReleaseOfTheChannel_FailsAsNoRelease()
    {
        using var http = new HttpClient(new Answers([]));
        var route = new UpdateRoute(Home, Home, string.Empty, true, false, string.Empty);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => UpdateSources.FetchAsync(http, route, UpdateChannels.Stable, _ => { }, CancellationToken.None));

        Assert.Equal(UpdateSources.NoReleaseKey, UpdateSources.FaultKey(route, false, error));
    }

    [Fact]
    public async Task TheProject_ReadsTheBakedManifestOnTheStableChannelAndTheNewestReleaseOnTheTestOne()
    {
        const string picked = "https://example.test/v1.11.0.905-beta.1/update.json";
        using var http = new HttpClient(new Answers(new Dictionary<string, string>
        {
            [Baked] = """{"version":"1.11.0.904"}""",
            [UpdateFeed.ReleasesUrl("bor-project", "amneziageo")] =
                $$"""[{"tag_name":"v1.11.0.905-beta.1","prerelease":true,"draft":false,"assets":[{"name":"update.json","browser_download_url":"{{picked}}"}]}]""",
            [picked] = """{"version":"1.11.0.905"}""",
        }));
        var route = new UpdateRoute(UpdateSources.OriginKey, Baked, string.Empty, false, true, string.Empty);

        var stable = await UpdateSources.FetchAsync(http, route, UpdateChannels.Stable, _ => { }, CancellationToken.None);
        var test = await UpdateSources.FetchAsync(http, route, UpdateChannels.Test, _ => { }, CancellationToken.None);

        Assert.Equal("1.11.0.904", stable!.Manifest.Version);
        Assert.Equal("1.11.0.905", test!.Manifest.Version);
        Assert.Equal(new Uri(picked), test.BaseUrl);
    }

    private static UpdateServer Server(string url, bool asks) => url == Home
        ? new UpdateServer(Home, HomePin, asks, "Дом")
        : new UpdateServer(Work, WorkPin, asks, "Работа");

    private static ServerOffer Offer(string updates) =>
        ServerOffer.Parse($$$"""{"server":"amneziageo","version":"0.0.36.0","features":{"updates":{{{updates}}}}}""");

    // Answers the addresses it knows and 404 for any other.
    private sealed class Answers(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
