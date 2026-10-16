using System.Diagnostics;
using System.Net;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A name a program routed through the tunnel asks is looked up behind the tunnel from its first query: the answer
/// waits for the report of its asker.
/// </summary>
public sealed class DnsAppTests
{
    private const string Name = "www.lent.example";
    private const string LentAddress = "192.0.2.7";

    // A resolver nothing of this set answers from.
    private static readonly IPAddress Nowhere = IPAddress.Parse("192.0.2.53");
    private static readonly byte[] Query = DnsMessage.BuildQuery(Name, 1);

    [WindowsFact("the wait for the asker raises the timer resolution through winmm")]
    public async Task AnAskOfSuchAProgramReportedWhileTheAnswerWaits_IsLookedUpBehindTheTunnel()
    {
        var (proxy, tracker, lent) = Tunneling(ThisProgram);
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var answering = proxy.AnswerAsync(Query, patience.Token);
        await Task.Delay(5);
        tracker.Report((uint)Environment.ProcessId, Name);
        var answer = await answering;

        // The lookup took the resolver behind the tunnel, whatever that one answers here, and not the other tunnel
        // of the set.
        Assert.DoesNotContain(LentAddress, DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.Equal(0, lent.Asked);
        Assert.True(tracker.IsTunneled(Name));
    }

    [WindowsFact("the wait for the asker raises the timer resolution through winmm")]
    public async Task AnAnswerWhoseAskerIsNotReported_WaitsAShortWhileAndIsLookedUpAsBefore()
    {
        var (proxy, _, lent) = Tunneling();

        var clock = Stopwatch.StartNew();
        var answer = await proxy.AnswerAsync(Query, CancellationToken.None);

        Assert.Equal([LentAddress], DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.InRange(clock.ElapsedMilliseconds, 19, 3000);
        Assert.Equal(1, lent.Asked);
    }

    [WindowsFact("the wait for the asker raises the timer resolution through winmm")]
    public async Task AnAskOfAnotherProgram_EndsTheWaitAndKeepsTheNameWhereItWas()
    {
        var (proxy, tracker, lent) = Tunneling();

        var answering = proxy.AnswerAsync(Query, CancellationToken.None);
        await Task.Delay(5);
        tracker.Report((uint)Environment.ProcessId, Name);
        var answer = await answering;

        Assert.Equal([LentAddress], DnsMessage.Addresses(answer!).Select(address => address.ToString()));
        Assert.Equal(1, lent.Asked);
        Assert.False(tracker.IsTunneled(Name));
    }

    // The rule that names the test host itself.
    private static string ThisProgram => $"path={Environment.ProcessPath}";

    // A proxy with programs routed through the tunnel, answering www.lent.example through another tunnel of the set
    // unless a program of the rule asked it.
    private static (DnsProxy Proxy, AppDnsTracker Tracker, Lent Lent) Tunneling(string rule = "name=probe.exe")
    {
        var tracker = new AppDnsTracker(new AppMatcher([rule], NullLogger<AppMatcher>.Instance), NullLogger<AppDnsTracker>.Instance);
        var proxy = new DnsProxy([], [], Nowhere, Nowhere, null, [], true, [], [], null, NullLogger<DnsProxy>.Instance, stripV6: false, appDns: tracker, listen: false, dnsTransport: DnsTransports.Plain);
        var lent = new Lent();
        proxy.SetLentNames(name => name.EndsWith("lent.example", StringComparison.Ordinal) ? "second" : null, lent.CarryAsync);
        return (proxy, tracker, lent);
    }

    // The other tunnel of the set: answers every name with one address and counts the asks.
    private sealed class Lent
    {
        private int _asked;

        public int Asked => _asked;

        public Task<IReadOnlyList<string>> CarryAsync(string owner, string name)
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult<IReadOnlyList<string>>([LentAddress]);
        }
    }
}
