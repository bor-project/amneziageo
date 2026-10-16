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

    [WindowsFact("the wait raises the timer resolution through winmm")]
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

    [WindowsFact("the wait raises the timer resolution through winmm")]
    public async Task AWaitEndsWhenTheAskIsReported()
    {
        var tracker = Tracker();
        var since = Stopwatch.GetTimestamp();

        var waiting = tracker.WaitAskedAsync(Name, since, 1000);
        await Task.Delay(30);
        var clock = Stopwatch.StartNew();
        tracker.NoteAsked(Name);

        Assert.True(await waiting);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 500);
        Assert.False(await tracker.WaitAskedAsync("other.example", since, 20));
    }

    [WindowsFact("the program behind an ask is found in the process tree read through kernel32")]
    public void AReportedAskOfAMatchedProgram_MarksTheNameBeforeItCountsAsAsked()
    {
        var tracker = Tracker(ThisProgram);
        var since = Stopwatch.GetTimestamp();
        var askedAtMark = true;
        tracker.NameLearned += name => askedAtMark = tracker.AskedSince(name, since);

        tracker.Report((uint)Environment.ProcessId, Name);

        Assert.True(tracker.IsTunneled(Name));
        Assert.True(tracker.AskedSince(Name, since));
        Assert.False(askedAtMark);
    }

    [WindowsFact("the program behind an ask is found in the process tree read through kernel32")]
    public void AReportedAskOfAnotherProgram_OnlyCountsAsAsked()
    {
        var tracker = Tracker();
        var since = Stopwatch.GetTimestamp();

        tracker.Report((uint)Environment.ProcessId, Name);

        Assert.False(tracker.IsTunneled(Name));
        Assert.True(tracker.AskedSince(Name, since));
    }

    [WindowsFact("the program behind an ask is found in the process tree read through kernel32")]
    public async Task AnAskReportedWhileTheAnswerWaits_RoutesItPastTheTunnel()
    {
        var applier = new PassApplier();
        var (proxy, tracker) = Passing(applier, ThisProgram);

        var answering = proxy.AnswerAsync(Query, CancellationToken.None);
        await Task.Delay(5);
        tracker.Report((uint)Environment.ProcessId, Name);
        var answer = await answering;

        Assert.Equal([Address], DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.Equal([Address], applier.Added);
    }

    // The rule that names the test host itself.
    private static string ThisProgram => $"path={Environment.ProcessPath}";

    private static AppDnsTracker Tracker(string rule = "name=probe.exe") =>
        new(new AppMatcher([rule], NullLogger<AppMatcher>.Instance, past: true), NullLogger<AppDnsTracker>.Instance, past: true);

    // A proxy with programs kept off the tunnel, answering www.lent.example through another tunnel of the set.
    private static (DnsProxy Proxy, AppDnsTracker Tracker) Passing(PassApplier applier, string rule = "name=probe.exe")
    {
        var routing = new RoutingCache(applier, new IdleLive(), false, [], [], [], 300, NullLogger<RoutingCache>.Instance);
        var tracker = Tracker(rule);
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
