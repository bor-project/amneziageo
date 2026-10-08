using System.Diagnostics;
using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The echo of one tunnel: which address it leaves from, and that nothing beside the adapter holding it is echoed.
/// </summary>
public sealed class TunnelEchoTests
{
    private const int TimeoutMs = 3000;

    // Well under the timeout: an echo that was not sent is not waited for.
    private const int AtOnceMs = 1000;

    private const string Why = "the echo is sent by the ICMP helper of Windows";

    // TEST-NET-1: an address no adapter holds and no network carries.
    private static readonly IPAddress Nowhere = IPAddress.Parse("192.0.2.1");

    [Fact]
    public void TheEchoLeavesFromTheIpv4AddressOfTheTunnel()
    {
        Assert.Equal(IPAddress.Parse("10.237.0.3"), TunnelEcho.Source(["fd42::3/128", "10.237.0.3/32", "10.238.0.3/24"]));
        Assert.Equal(IPAddress.Parse("10.237.0.3"), TunnelEcho.Source([" 10.237.0.3 "]));
    }

    [Fact]
    public void ATunnelWithoutAnIpv4Address_HasNoEchoOfItsOwn()
    {
        Assert.Null(TunnelEcho.Source(["fd42::3/128"]));
        Assert.Null(TunnelEcho.From(["fd42::3/128", "not an address"]));
        Assert.Null(TunnelEcho.From([]));
        Assert.NotNull(TunnelEcho.From(["10.237.0.3/32"]));
    }

    [Fact]
    public async Task AnAddressOfAnotherFamily_IsNotEchoed()
    {
        Assert.Equal(-1, await TunnelEcho.RoundTripAsync(IPAddress.Loopback, IPAddress.IPv6Loopback, TimeoutMs, CancellationToken.None));
        Assert.Equal(-1, await TunnelEcho.RoundTripAsync(IPAddress.IPv6Loopback, IPAddress.Loopback, TimeoutMs, CancellationToken.None));
    }

    [WindowsFact(Why)]
    public async Task AnEchoFromTheAddressOfAnAdapter_ComesBackOverThatAdapter()
    {
        Assert.True(await TunnelEcho.RoundTripAsync(IPAddress.Loopback, IPAddress.Loopback, TimeoutMs, CancellationToken.None) >= 0);
    }

    [WindowsFact(Why)]
    public async Task AnAddressBesideTheAdapterOfTheSource_IsNotEchoed()
    {
        var clock = Stopwatch.StartNew();

        Assert.Equal(-1, await TunnelEcho.RoundTripAsync(IPAddress.Loopback, Nowhere, TimeoutMs, CancellationToken.None));
        Assert.True(clock.ElapsedMilliseconds < AtOnceMs);
    }

    [WindowsFact(Why)]
    public async Task ASourceTheMachineDoesNotHold_SendsNoEcho()
    {
        var clock = Stopwatch.StartNew();

        Assert.Equal(-1, await TunnelEcho.RoundTripAsync(Nowhere, IPAddress.Loopback, TimeoutMs, CancellationToken.None));
        Assert.True(clock.ElapsedMilliseconds < AtOnceMs);
    }

    [Fact]
    public async Task ASessionThatEnded_SendsNoEcho()
    {
        using var ended = new CancellationTokenSource();
        ended.Cancel();

        Assert.Equal(-1, await TunnelEcho.RoundTripAsync(IPAddress.Loopback, IPAddress.Loopback, TimeoutMs, ended.Token));
    }
}
