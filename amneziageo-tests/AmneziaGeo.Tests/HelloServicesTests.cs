using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server of ours names where its services answer: the port at its host and the place inside the tunnel. The next
/// question goes to that port first, then to the one of the file, then to the port of the Endpoint, so a file handed
/// out before the services moved finds them; the answer stays kept under the point of the file.
/// </summary>
public sealed class HelloServicesTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string Moved = "[Interface]\nPrivateKey = a\n# AmneziaGeo Services = 8446\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string Inside = "https://10.9.9.1:9080";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-services-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheNextQuestion_GoesToThePortTheServerNamed_AndTheAnswerStaysUnderThePointOfTheFile()
    {
        var asked = new List<int>();
        var offers = new ServerOffers(_store, ask: (point, _) =>
        {
            asked.Add(point.Port);

            return Task.FromResult(new HelloReply(Offer(9080, string.Empty), true));
        });

        await offers.AskAsync("home", Text, CancellationToken.None);
        await offers.AskAsync("home", Text, CancellationToken.None);

        Assert.Equal([51820, 9080], asked);
        Assert.True((await ServerOfferStore.KeptAsync(_store, "home", Text))!.Offer.Ours);
    }

    [Fact]
    public async Task APortTheServerNamedThatKeepsSilent_LeavesTheQuestionToThePortOfTheFile()
    {
        var asked = new List<int>();
        var offers = new ServerOffers(_store, ask: (point, _) =>
        {
            asked.Add(point.Port);

            return Task.FromResult(point.Port == 9080 ? new HelloReply(null, false) : new HelloReply(Offer(9080, string.Empty), true));
        });

        await offers.AskAsync("home", Text, CancellationToken.None);
        var offer = await offers.AskAsync("home", Text, CancellationToken.None);

        Assert.Equal([51820, 9080, 51820], asked);
        Assert.True(offer.Ours);
    }

    [Fact]
    public async Task AFileWhoseLineNamesAPortNoLongerServed_FindsTheServicesAtThePortOfTheEndpoint()
    {
        var asked = new List<int>();
        var offers = new ServerOffers(_store, ask: (point, _) =>
        {
            asked.Add(point.Port);

            return Task.FromResult(point.Port == 51820 ? new HelloReply(Offer(9080, string.Empty), true) : new HelloReply(null, true));
        });

        var offer = await offers.AskAsync("home", Moved, CancellationToken.None);

        Assert.Equal([8446, 51820], asked);
        Assert.True(offer.Ours);
        Assert.True((await ServerOfferStore.KeptAsync(_store, "home", Moved))!.Offer.Ours);
    }

    [Fact]
    public async Task AServerThatNamedWhereItsServicesAnswerInside_IsAskedThereWhileTheTunnelCarries()
    {
        var past = 0;
        var inside = new List<string>();
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(Offer(9080, Inside), Interlocked.Increment(ref past) > 0)),
            carried: _ => true,
            askInside: (point, _) =>
            {
                inside.Add(point.Origin);

                return Task.FromResult(new HelloReply(Offer(9080, Inside), true));
            });

        await offers.AskAsync("home", Text, CancellationToken.None);
        await offers.AskAsync("home", Text, CancellationToken.None);

        Assert.Equal(1, past);
        Assert.Equal([Inside], inside);
    }

    [Fact]
    public async Task AServerSilentPastTheTunnel_IsAskedOnceInsideAtTheAddressTheTunnelDials()
    {
        var inside = new List<string>();
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(null, false)),
            carried: _ => true,
            askInside: (point, _) =>
            {
                inside.Add(point.Origin);

                return Task.FromResult(new HelloReply(Offer(9080, Inside), true));
            },
            dialed: (_, _) => Task.FromResult<string?>("203.0.113.7"));

        var offer = await offers.AskAsync("home", Text, CancellationToken.None);

        Assert.Equal(["https://203.0.113.7:51820"], inside);
        Assert.True(offer.Ours);
        Assert.Equal(9080, offer.ServicesPort());
    }

    [Fact]
    public async Task AConfigWhoseTunnelIsDown_IsNotAskedAtTheAddressItDials()
    {
        var inside = 0;
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(null, false)),
            carried: _ => false,
            askInside: (_, _) => Task.FromResult(new HelloReply(Offer(9080, Inside), Interlocked.Increment(ref inside) > 0)),
            dialed: (_, _) => Task.FromResult<string?>("203.0.113.7"));

        var offer = await offers.AskAsync("home", Text, CancellationToken.None);

        Assert.Equal(0, inside);
        Assert.False(offer.Ours);
    }

    [Fact]
    public void TheOffer_NamesThePortOfTheServicesAndAnAddressInsideAlone()
    {
        var named = Offer(9080, Inside);
        var plain = Offer(8446, "http://10.8.0.1:8446/api/hello");
        var odd = Offer(70000, "https://vpn.example:9080");

        Assert.Equal((9080, Inside), (named.ServicesPort(), named.ServicesInside()));
        Assert.Equal("http://10.8.0.1:8446", plain.ServicesInside());
        Assert.Equal((0, string.Empty), (odd.ServicesPort(), odd.ServicesInside()));
        Assert.Equal((0, string.Empty), (ServerOffer.None.ServicesPort(), ServerOffer.None.ServicesInside()));
        Assert.Equal(51820, ConfigServices.EndpointPort(Moved));
        Assert.Equal(0, ConfigServices.EndpointPort("[Peer]\nPublicKey = b\n"));
    }

    // An answer of a server of ours that names where its services answer.
    private static ServerOffer Offer(int port, string inside) =>
        ServerOffer.Parse($"{{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{{\"services\":{{\"port\":{port},\"inside\":\"{inside}\"}}}}}}");
}
