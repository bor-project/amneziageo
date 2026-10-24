using System.Globalization;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server of ours is asked before the first connect and after its Endpoint or keys changed; later connects take the
/// kept answer, and the server is asked behind them once a day. A question that gets no answer changes nothing and is
/// put again after a pause that doubles; answers of another kind take a server of ours back only a few in a row, or
/// one on a refresh.
/// </summary>
public sealed class HelloCacheTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-hello-{Guid.NewGuid():N}.db");
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
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
    public async Task AServerOfOurs_IsTakenAsKeptAtTheNextConnectWithoutAQuestion()
    {
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
            Task.FromResult(new HelloReply(Offer(Interlocked.Increment(ref asked) == 1 ? 8446 : 9446), true)), time: _clock);

        await offers.AskAsync("office", Text, CancellationToken.None);
        _clock.Pass(ServerOffers.FreshFor - TimeSpan.FromMinutes(1));
        var offer = await offers.BeforeConnectAsync("office", Text, null, CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(8446, offer.WebSocketPort);
        Assert.Equal(1, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task AServerOfOursAskedADayAgo_IsAskedBehindTheConnect()
    {
        var later = new TaskCompletionSource<HelloReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
            Interlocked.Increment(ref asked) == 1 ? Task.FromResult(new HelloReply(Offer(8446), true)) : later.Task, time: _clock);

        await offers.AskAsync("office", Text, CancellationToken.None);
        _clock.Pass(ServerOffers.FreshFor);
        var connect = offers.BeforeConnectAsync("office", Text, null, CancellationToken.None);

        Assert.Same(connect, await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(8446, (await connect).WebSocketPort);
        later.SetResult(new HelloReply(Offer(9446), true));
        var kept = await KeptAsync(found => found.Offer.WebSocketPort == 9446);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), kept.Asked.ToUnixTimeSeconds());
        Assert.Equal(2, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task AConfigWhoseEndpointMoved_WaitsForTheAnswerAgain()
    {
        var later = new TaskCompletionSource<HelloReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
            Interlocked.Increment(ref asked) == 1 ? Task.FromResult(new HelloReply(Offer(8446), true)) : later.Task, time: _clock);
        var moved = Text.Replace("51820", "51821", StringComparison.Ordinal);

        await offers.AskAsync("office", Text, CancellationToken.None);
        var connect = offers.BeforeConnectAsync("office", moved, null, CancellationToken.None);
        await Task.WhenAny(connect, Task.Delay(300));

        Assert.False(connect.IsCompleted);
        later.SetResult(new HelloReply(Offer(9446), true));
        Assert.Equal(9446, (await connect).WebSocketPort);
    }

    [Fact]
    public async Task AQuestionWithoutAnAnswer_LeavesAServerOfOursAndIsPutAgainAfterAPauseThatDoubles()
    {
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
            Task.FromResult(Interlocked.Increment(ref asked) == 1 ? new HelloReply(Offer(8446), true) : new HelloReply(null, false)), time: _clock);
        await offers.AskAsync("office", Text, CancellationToken.None);
        var answered = _clock.GetUtcNow();

        _clock.Pass(ServerOffers.FreshFor);
        offers.Warm([("office", Text)]);
        var first = await KeptAsync(found => found.Misses == 1);
        offers.Warm([("office", Text)]);
        await Task.Delay(300);
        var calm = Volatile.Read(ref asked);
        _clock.Pass(ServerOffers.RetryAfter);
        offers.Warm([("office", Text)]);
        await KeptAsync(found => found.Misses == 2);
        await Task.Delay(200);
        _clock.Pass(ServerOffers.RetryAfter);
        offers.Warm([("office", Text)]);
        await Task.Delay(300);
        var doubled = Volatile.Read(ref asked);
        _clock.Pass(ServerOffers.RetryAfter);
        offers.Warm([("office", Text)]);
        var third = await KeptAsync(found => found.Misses == 3);

        Assert.True(first.Offer.Ours);
        Assert.Equal(8446, first.Offer.WebSocketPort);
        Assert.Equal(answered.ToUnixTimeSeconds(), first.Asked.ToUnixTimeSeconds());
        Assert.Equal(2, calm);
        Assert.Equal(3, doubled);
        Assert.True(third.Offer.Ours);
        Assert.Equal(4, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task AnswersOfAnotherKind_TakeAServerOfOursBackOnlyAFewInARow()
    {
        var asked = 0;
        var offers = new ServerOffers(_store, ask: (_, _) =>
            Task.FromResult(Interlocked.Increment(ref asked) == 1 ? new HelloReply(Offer(8446), true) : new HelloReply(null, true)), time: _clock);
        await offers.AskAsync("office", Text, CancellationToken.None);

        var held = new List<bool>();
        for (var answer = 1; answer < ServerOffers.ForeignAnswers; answer++)
        {
            held.Add((await offers.AskAsync("office", Text, CancellationToken.None)).Ours);
        }

        var last = await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.All(held, ours => Assert.True(ours));
        Assert.False(last.Ours);
        Assert.False((await offers.OfferAsync("office", Text, CancellationToken.None)).Ours);
    }

    [Fact]
    public async Task AnAnswerOfOurs_StartsTheCountOfAnswersOfAnotherKindAgain()
    {
        var replies = new Queue<HelloReply>();
        replies.Enqueue(new HelloReply(Offer(8446), true));
        for (var round = 0; round < 2; round++)
        {
            for (var answer = 1; answer < ServerOffers.ForeignAnswers; answer++)
            {
                replies.Enqueue(new HelloReply(null, true));
            }

            replies.Enqueue(new HelloReply(Offer(8446), true));
        }

        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()), time: _clock);
        var held = new List<bool>();
        while (replies.Count > 0)
        {
            held.Add((await offers.AskAsync("office", Text, CancellationToken.None)).Ours);
        }

        Assert.All(held, ours => Assert.True(ours));
        Assert.Equal(0, (await KeptAsync(_ => true)).Misses);
    }

    [Fact]
    public async Task AnAnswerOfAnotherKindOnARefresh_TakesAServerOfOursBackAtOnce()
    {
        var replies = new Queue<HelloReply>([new HelloReply(Offer(8446), true), new HelloReply(null, true)]);
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(replies.Dequeue()), time: _clock);

        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskEachAsync([("office", Text)], null, CancellationToken.None);

        Assert.False((await offers.OfferAsync("office", Text, CancellationToken.None)).Ours);
    }

    // Waits until the offer kept for the config is what the test waits for.
    private async Task<KeptOffer> KeptAsync(Func<KeptOffer, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (await ServerOfferStore.KeptAsync(_store, "office", Text) is { } kept && until(kept))
            {
                return kept;
            }

            Assert.True(DateTime.UtcNow < deadline, "the kept offer never came to what was waited for");
            await Task.Delay(20);
        }
    }

    private static ServerOffer Offer(int port)
    {
        const string answer = """{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":PORT}}}""";

        return ServerOffer.Parse(answer.Replace("PORT", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    // A clock the test moves on by hand.
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Pass(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }
}
