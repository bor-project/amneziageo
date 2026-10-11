using System.Net;

using AmneziaGeo.Windows.App;

using Microsoft.Extensions.Logging;

using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// Every tunnel of a set watches connections in a monitoring session of its own, so none takes the events of
/// another; the rule that sends all UDP into the tunnel belongs to the tunnel that carries the machine; an address
/// a rule sends through a neighbour of the set is named so, not as the route of another program.
/// </summary>
public sealed class SetWatchTests
{
    private static readonly IPAddress _remote = IPAddress.Parse("195.54.192.102");

    [Fact]
    public void TheTunnelThatCarriesTheMachine_KeepsTheSessionOfASingleTunnel()
    {
        Assert.Equal("AmneziaGeoFlow", WatchSession.Name(WatchSession.Flows, true, "bor-dev_fi"));
        Assert.Equal("AmneziaGeoDns", WatchSession.Name(WatchSession.Names, true, "bor-dev_fi"));
    }

    [Fact]
    public void AnotherTunnelOfTheSet_WatchesInASessionOfItsOwn()
    {
        var second = WatchSession.Name(WatchSession.Flows, false, "dev-test-b");
        var third = WatchSession.Name(WatchSession.Flows, false, "dev test c");

        Assert.StartsWith("AmneziaGeoFlow.devtestb-", second);
        Assert.NotEqual("AmneziaGeoFlow", second);
        Assert.NotEqual(second, third);
        Assert.Equal(second, WatchSession.Name(WatchSession.Flows, false, "dev-test-b"));
        Assert.StartsWith("AmneziaGeoDns.", WatchSession.Name(WatchSession.Names, false, "dev-test-b"));
    }

    [Fact]
    public void DevicesThatDifferOnlyInMarks_GetDifferentSessions()
    {
        Assert.NotEqual(WatchSession.Name(WatchSession.Flows, false, "dev-test-b"), WatchSession.Name(WatchSession.Flows, false, "dev_test_b"));
        Assert.All(WatchSession.Name(WatchSession.Flows, false, "\u0434\u043E\u043C (1)").Skip("AmneziaGeoFlow.".Length), c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, null, true, true)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, false, null, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, false, true, false)]
    public void AllUdp_BelongsToTheTunnelThatCarriesTheMachine(bool split, bool carriesDefault, bool? ofList, bool ofSettings, bool taken)
    {
        Assert.Equal(taken, TunnelRunner.TakesAllUdp(split, carriesDefault, ofList, ofSettings));
    }

    [Fact]
    public void AnAddressARuleSendsThroughANeighbour_IsNamedSo()
    {
        var log = new Lines();
        var tracker = new NetworkFlowTracker(null, null, true, false, null, log, heldElsewhere: _ => true, neighbour: _ => "dev-test-b");

        Assert.False(tracker.TakeDatagram(_remote));

        var (level, text) = Assert.Single(log.Written);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("dev-test-b, another tunnel of the set", text);
        Assert.DoesNotContain("a VPN server", text);
    }

    [Fact]
    public void AnAddressOnTheRouteOfAnotherProgram_IsNamedAsBefore()
    {
        var log = new Lines();
        var tracker = new NetworkFlowTracker(null, null, true, false, null, log, heldElsewhere: _ => true, neighbour: _ => null);

        Assert.False(tracker.TakeDatagram(_remote));

        var (level, text) = Assert.Single(log.Written);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("a VPN server", text);
    }

    private sealed class Lines : ILogger
    {
        public List<(LogLevel Level, string Text)> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Written.Add((logLevel, formatter(state, exception)));
        }
    }
}
