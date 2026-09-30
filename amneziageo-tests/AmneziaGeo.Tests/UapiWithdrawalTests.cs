using System.Diagnostics;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Withdrawals queued for a device that has closed are dropped, not sent to its pipe.
/// </summary>
public sealed class UapiWithdrawalTests
{
    private const string Peer = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void DroppedWithdrawals_AreNotSent()
    {
        using var uapi = new UapiClient(NullLogger<UapiClient>.Instance);
        uapi.QueueRemoveAllowedIps("ag-test-closed", Peer, ["192.0.2.7/32", "192.0.2.8/32"]);

        uapi.DropWithdrawals();
        var clock = Stopwatch.StartNew();
        uapi.FlushWithdrawals();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"flushing took {clock.Elapsed}");
    }
}
