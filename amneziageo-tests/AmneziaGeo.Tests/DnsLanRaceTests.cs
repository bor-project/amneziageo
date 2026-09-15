using System.Diagnostics;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The race across the own network's resolvers takes an answer with addresses, and a resolver that stays silent
/// costs only a short grace once another has settled the name.
/// </summary>
public sealed class DnsLanRaceTests
{
    private static readonly byte[] Query = DnsMessage.BuildQuery("vpn.corp.example", 1);

    private static Func<CancellationToken, Task<byte[]>> Answers(byte[] response, int afterMs = 0) =>
        async ct =>
        {
            await Task.Delay(afterMs, ct).ConfigureAwait(false);
            return response;
        };

    private static Func<CancellationToken, Task<byte[]>> Silent() =>
        async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return [];
        };

    [Fact]
    public async Task ASilentResolver_CostsOnlyTheGrace_OnceAnotherSettlesTheName()
    {
        var missing = DnsMessage.BuildNxDomain(Query);
        var watch = Stopwatch.StartNew();

        var answer = await DnsProxy.RaceAsync([Silent(), Answers(missing)], graceMs: 50);

        Assert.Equal(missing, answer);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AnAnswerWithAddresses_WinsOverAnEarlierSettledOne_WithinTheGrace()
    {
        var addresses = DnsMessage.BuildAAnswer(Query, ["192.0.2.1"], 60);

        var answer = await DnsProxy.RaceAsync([Answers(DnsMessage.BuildNxDomain(Query)), Answers(addresses, afterMs: 20)], graceMs: 2000);

        Assert.Equal(addresses, answer);
    }

    [Fact]
    public async Task AFailedAnswer_StartsNoGrace()
    {
        var addresses = DnsMessage.BuildAAnswer(Query, ["192.0.2.1"], 60);

        var answer = await DnsProxy.RaceAsync([Answers(DnsMessage.BuildServFail(Query)), Answers(addresses, afterMs: 300)], graceMs: 50);

        Assert.Equal(addresses, answer);
    }

    [Fact]
    public async Task ASettledAnswer_IsPreferredOverAnEarlierFailure()
    {
        var missing = DnsMessage.BuildNxDomain(Query);

        var answer = await DnsProxy.RaceAsync([Answers(DnsMessage.BuildServFail(Query)), Answers(missing, afterMs: 20), Silent()], graceMs: 50);

        Assert.Equal(missing, answer);
    }
}
