using System.Globalization;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The servers of the configs are asked what they offer: an answer is kept, a server that answered nothing leaves the
/// kept offer alone, a server of another kind does not hold a connect back, and a speed run measures at the server of
/// the config where it offers that.
/// </summary>
public sealed class ServerOffersTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-offers-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
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

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnAnswer_IsKeptAndASilenceLeavesIt()
    {
        var replies = new Queue<HelloReply>([new HelloReply(Offer(8446, DateTimeOffset.UtcNow.AddMinutes(5)), true), new HelloReply(null, false)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()));

        var first = await offers.AskAsync("office", Text, CancellationToken.None);
        var second = await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(8446, first.WebSocketPort);
        Assert.Equal(8446, second.WebSocketPort);
        Assert.Equal(8446, (await offers.OfferAsync("office", Text, CancellationToken.None)).WebSocketPort);
    }

    [Fact]
    public async Task AServerThatAnswersNothingOfOurs_TakesTheOfferBack()
    {
        var replies = new Queue<HelloReply>([new HelloReply(Offer(8446, DateTimeOffset.UtcNow.AddMinutes(5)), true), new HelloReply(null, true)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()));

        await offers.AskAsync("office", Text, CancellationToken.None);
        var after = await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.False(after.Ours);
        Assert.False((await offers.OfferAsync("office", Text, CancellationToken.None)).Ours);
    }

    [Fact]
    public async Task AServerOfAnotherKind_IsAskedBehindTheConnect()
    {
        var held = new TaskCompletionSource<HelloReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return asked == 1 ? Task.FromResult(new HelloReply(null, true)) : held.Task;
        });

        await offers.AskAsync("office", Text, CancellationToken.None);
        var connect = offers.BeforeConnectAsync("office", Text, null, CancellationToken.None);

        Assert.True(connect.IsCompleted);
        Assert.False((await connect).Ours);
        held.SetResult(new HelloReply(null, true));
    }

    [Fact]
    public async Task AServerOfOurs_IsAskedBeforeTheConnect()
    {
        var replies = new Queue<HelloReply>([new HelloReply(Offer(0, DateTimeOffset.UtcNow.AddMinutes(5)), true), new HelloReply(Offer(8446, DateTimeOffset.UtcNow.AddMinutes(5)), true)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()));

        await offers.AskAsync("office", Text, CancellationToken.None);
        var offer = await offers.BeforeConnectAsync("office", Text, null, CancellationToken.None);

        Assert.Equal(8446, offer.WebSocketPort);
    }

    [Fact]
    public async Task APassThatStillStands_IsTakenWithoutAskingAgain()
    {
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            asked++;
            return Task.FromResult(new HelloReply(Offer(8446, DateTimeOffset.UtcNow.AddMinutes(asked == 1 ? 5 : 10)), true));
        });

        await offers.AskAsync("office", Text, CancellationToken.None);
        var speed = await offers.SpeedAsync("office", Text, CancellationToken.None);

        Assert.NotNull(speed);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task APassAboutToEnd_IsAskedForAgain()
    {
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
        {
            asked++;
            return Task.FromResult(new HelloReply(Offer(8446, DateTimeOffset.UtcNow.AddSeconds(asked == 1 ? 10 : 300)), true));
        });

        await offers.AskAsync("office", Text, CancellationToken.None);
        var speed = await offers.SpeedAsync("office", Text, CancellationToken.None);

        Assert.NotNull(speed);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task AnAddressTheServerNames_BindsTheConfigToASubscription()
    {
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(Named("r1"), true)));

        await offers.AskAsync("office", Text, CancellationToken.None);

        var subscription = Assert.Single(await _store.ListSubscriptionsAsync());
        var member = Assert.Single(await _store.ListSubscriptionMembersAsync(null));
        Assert.Equal("vpn.example", subscription.Name);
        Assert.Equal("https://vpn.example:51820/sub/abc", subscription.Url);
        Assert.True(subscription.FromHello);
        Assert.Equal("ab12", subscription.Pin);
        Assert.NotNull(subscription.CheckedAt);
        Assert.False(subscription.Stale);
        Assert.Equal(("vpn.example", "office"), (member.Subscription, member.ConfigName));
        Assert.Equal(SubscriptionMerge.KeyRemark(Text), member.Remark);
    }

    [Fact]
    public async Task AnotherRevisionOfTheServer_MarksTheSubscriptionStale()
    {
        var replies = new Queue<HelloReply>([new HelloReply(Named("r1"), true), new HelloReply(Named("r2"), true)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()));

        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.True(Assert.Single(await _store.ListSubscriptionsAsync()).Stale);
        Assert.Equal(["vpn.example"], await SubscriptionBinding.StaleAsync(_store, CancellationToken.None));
    }

    [Fact]
    public async Task ASubscriptionAtTheAddress_TakesTheConfigIn()
    {
        await _store.SaveSubscriptionAsync(new Subscription("mine", "https://vpn.example:51820/sub/abc", Revision: "r0"));
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(Named("r1"), true)));

        await offers.AskAsync("office", Text, CancellationToken.None);

        var subscription = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("mine", Assert.Single(await _store.ListSubscriptionMembersAsync(null)).Subscription);
        Assert.False(subscription.FromHello);
        Assert.True(subscription.Stale);
    }

    [Fact]
    public async Task AConfigSubscribedElsewhere_KeepsItsSubscription()
    {
        await _store.SaveSubscriptionAsync(new Subscription("mine", "https://panel.example/sub/xyz"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("mine", SubscriptionMerge.KeyRemark(Text), "office"));
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(Named("r1"), true)));

        await offers.AskAsync("office", Text, CancellationToken.None);

        var subscription = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("https://panel.example/sub/xyz", subscription.Url);
        Assert.Equal(string.Empty, subscription.Offered);
    }

    [Fact]
    public async Task AnAddressThatMoved_IsFollowedByTheSubscriptionTheServerNamed()
    {
        var replies = new Queue<HelloReply>(
            [new HelloReply(Named("r1"), true), new HelloReply(Named("r1", "http://vpn.example:2096/sub/abc"), true)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()));

        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal("http://vpn.example:2096/sub/abc", Assert.Single(await _store.ListSubscriptionsAsync()).Url);
    }

    [Fact]
    public async Task ABindingInTheBackground_NamesTheConfig()
    {
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(Named("r1"), true)));

        offers.Warm([("office", Text)], config =>
        {
            changed.TrySetResult(config);
            return Task.CompletedTask;
        });

        Assert.Equal("office", await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TheUpload_GoesToTheServerOfTheConfigUnlessAnotherIsChosen()
    {
        var offer = Offer(8446, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(("https://my.example/up", false), ServerOffers.Upload("https://my.example/up", offer, ProbePaths.Auto));
        Assert.Equal(("https://10.9.1.1:8446/api/speed/up?ticket=t", true), ServerOffers.Upload(string.Empty, offer, ProbePaths.Tunnel));
        Assert.Equal(("https://vpn.example:8446/api/speed/up?ticket=t", true), ServerOffers.Upload(string.Empty, offer, ProbePaths.Bypass));
        Assert.Equal((string.Empty, false), ServerOffers.Upload(string.Empty, ServerOffer.None, ProbePaths.Auto));
        Assert.Equal("https://10.9.1.1:8446/api/speed/down?ticket=t", ServerOffers.Download(offer, true));
        Assert.Equal(string.Empty, ServerOffers.Download(null, false));
    }

    private static ServerOffer Named(string revision, string url = "https://vpn.example:51820/sub/abc")
    {
        const string answer = """{"server":"amneziageo","version":"1","client":"c","features":{"subscription":{"url":"URL","revision":"REVISION","pin":"ab12"}}}""";

        return ServerOffer.Parse(answer.Replace("URL", url, StringComparison.Ordinal).Replace("REVISION", revision, StringComparison.Ordinal));
    }

    private static ServerOffer Offer(int port, DateTimeOffset expires)
    {
        var websocket = port > 0 ? "\"websocket\":{\"port\":" + port.ToString(CultureInfo.InvariantCulture) + "}," : string.Empty;
        var stamp = expires.ToString("O", CultureInfo.InvariantCulture);

        const string answer = """{"server":"amneziageo","version":"1","client":"c","features":{WEBSOCKET"speed":{"inside":{"down":"https://10.9.1.1:8446/api/speed/down?ticket=t","up":"https://10.9.1.1:8446/api/speed/up?ticket=t"},"outside":{"down":"https://vpn.example:8446/api/speed/down?ticket=t","up":"https://vpn.example:8446/api/speed/up?ticket=t"},"limit":1,"expires":"STAMP"}}}""";

        return ServerOffer.Parse(answer.Replace("WEBSOCKET", websocket, StringComparison.Ordinal).Replace("STAMP", stamp, StringComparison.Ordinal));
    }
}
