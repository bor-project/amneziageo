using System.Diagnostics;
using System.Net;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A name a program kept off the tunnel asked takes its route past the tunnel before the answer leaves.
/// </summary>
public sealed class DnsPassTests
{
    private const string Name = "www.lent.example";
    private const string Address = "192.0.2.7";

    private static readonly IPAddress Resolver = IPAddress.Parse("10.8.1.1");
    private static readonly byte[] Query = DnsMessage.BuildQuery(Name, 1);

    [Fact]
    public async Task AnAnswerForANameSuchAProgramAsked_RoutesItPastTheTunnel()
    {
        var applier = new PassApplier();
        var (proxy, tracker) = Passing(applier);
        tracker.MarkName(Name);

        var answer = await proxy.AnswerAsync(Query, CancellationToken.None);

        Assert.Equal([Address], DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.Equal([Address], applier.Added);
    }

    [Fact]
    public async Task AnAnswerForANameAnotherProgramAsked_TakesNoRoute()
    {
        var applier = new PassApplier();
        var (proxy, tracker) = Passing(applier);
        tracker.NoteAsked(Name);

        var answer = await proxy.AnswerAsync(Query, CancellationToken.None);

        Assert.Equal([Address], DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.Empty(applier.Added);
    }

    [Fact]
    public async Task AnAnswerWhoseAskerIsNotReported_WaitsAShortWhileAndLeaves()
    {
        var applier = new PassApplier();
        var (proxy, _) = Passing(applier);

        var clock = Stopwatch.StartNew();
        var answer = await proxy.AnswerAsync(Query, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.InRange(clock.ElapsedMilliseconds, 19, 3000);
        Assert.Empty(applier.Added);
    }

    [Fact]
    public async Task WithProgramsKeptOffTheTunnel_TheSystemKeepsNoAnswer()
    {
        var (proxy, tracker) = Passing(new PassApplier());
        tracker.NoteAsked(Name);

        var answer = await proxy.AnswerAsync(Query, CancellationToken.None);

        Assert.Equal(0, DnsMessage.MinTtl(answer!));
    }

    [Fact]
    public async Task AnAskReportedLate_RoutesWhatTheNameAnsweredWith()
    {
        var applier = new PassApplier();
        var (proxy, tracker) = Passing(applier);
        tracker.NoteAsked(Name);
        await proxy.AnswerAsync(Query, CancellationToken.None);
        Assert.Empty(applier.Added);

        proxy.PassKnown(Name);

        Assert.Equal([Address], applier.Added);
    }

    [Fact]
    public void AnAskCountsFromTheMomentItWasNoted()
    {
        var tracker = Tracker();
        var before = Stopwatch.GetTimestamp();

        tracker.NoteAsked("Www.Lent.Example.");

        Assert.True(tracker.AskedSince(Name, before));
        Assert.False(tracker.AskedSince(Name, Stopwatch.GetTimestamp() + Stopwatch.Frequency));
        Assert.False(tracker.AskedSince("other.example", before));
    }

    [Fact]
    public async Task AWaitEndsWhenTheAskIsReported()
    {
        var tracker = Tracker();
        var since = Stopwatch.GetTimestamp();

        var waiting = tracker.WaitAskedAsync(Name, since, 1000);
        await Task.Delay(30);
        tracker.NoteAsked(Name);

        Assert.True(await waiting);
        Assert.False(await tracker.WaitAskedAsync("other.example", since, 20));
    }

    private static AppDnsTracker Tracker() =>
        new(new AppMatcher(["name=probe.exe"], NullLogger<AppMatcher>.Instance, past: true), NullLogger<AppDnsTracker>.Instance, past: true);

    // A proxy with programs kept off the tunnel, answering www.lent.example through another tunnel of the set.
    private static (DnsProxy Proxy, AppDnsTracker Tracker) Passing(PassApplier applier)
    {
        var routing = new RoutingCache(applier, new IdleLive(), false, [], [], [], 300, NullLogger<RoutingCache>.Instance);
        var tracker = Tracker();
        var proxy = new DnsProxy([], [], Resolver, Resolver, null, [], true, [], [], null, NullLogger<DnsProxy>.Instance, stripV6: false, routing: routing, listen: false, passDns: tracker);
        proxy.SetLentNames(
            name => name.EndsWith("lent.example", StringComparison.Ordinal) ? "second" : null,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([Address]));
        return (proxy, tracker);
    }

    // Keeps the routes it was asked for, and nothing else.
    private sealed class PassApplier : IRouteApplier
    {
        private ulong _nextId = 1;

        public List<string> Added { get; } = [];

        public int Generation => 0;

        public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
        {
            outId = _nextId++;
            inId = _nextId++;
            generation = 0;
            return true;
        }

        public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
        {
            outId = _nextId++;
            inId = _nextId++;
            generation = 0;
            return true;
        }

        public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
        {
            interfaceIndex = 7;
            Added.Add(address.ToString());
            return true;
        }

        public void RemoveRoute(IPAddress address, uint interfaceIndex)
        {
        }

        public bool TryTunnel(IPAddress address) => true;

        public IReadOnlyList<IPAddress> AddTunnel(IReadOnlyList<IPAddress> addresses) => addresses;

        public void RemoveTunnel(IReadOnlyCollection<IPAddress> addresses)
        {
        }

        public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
        {
        }
    }

    private sealed class IdleLive : ILiveDestinations
    {
        public LiveDestinations Snapshot() => new([], []);
    }
}
