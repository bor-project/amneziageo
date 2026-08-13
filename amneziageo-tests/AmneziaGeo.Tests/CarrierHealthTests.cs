using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// When a websocket carrier counts as no longer carrying. The session it holds up survives on keepalives, so the
/// one reading that must never happen here is a link called healthy because something small still crosses it -
/// and the one that must never happen either is a transfer called a stall because it is an upload.
/// </summary>
public sealed class CarrierHealthTests
{
    private const int Unknown = LinkHealth.LossUnknown;

    [Fact]
    public void ALinkAnsweringEverySecond_CarriesWhateverItSends()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 30, sent: true, returned: true);

        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ASilenceTheTunnelKeepsSendingInto_NamesAStall()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 12, sent: true, returned: false);

        Assert.Contains("nothing has come back", reason);
    }

    [Fact]
    public void OneKeepaliveComingBack_NoLongerClearsTheStall()
    {
        var health = new CarrierHealth();
        health.Verdict(true, true, 0, 0, Unknown, -1);

        var reason = Feed(health, 11, sent: true, returned: false);

        Assert.Contains("nothing has come back", reason);
    }

    [Fact]
    public void TwoPacketsComingBack_LeaveTheLinkCarrying()
    {
        var health = new CarrierHealth();
        health.Verdict(true, true, 0, 0, Unknown, -1);
        health.Verdict(true, true, 0, 0, Unknown, -1);

        var reason = Feed(health, 10, sent: true, returned: false);

        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void AMachineSendingNothing_IsIdleRatherThanStalled()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 20, sent: false, returned: false);

        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ALossNoProbeEverMeasured_DegradesNothing()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 30, sent: true, returned: true, loss: Unknown, rttMs: -1);

        Assert.Equal(string.Empty, reason);
        Assert.False(health.Degrading);
    }

    [Fact]
    public void AChannelLosingHalfOfWhatItCarries_IsDegraded()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 20, sent: true, returned: true, loss: 50, rttMs: 30);

        Assert.Contains("losing 50%", reason);
    }

    [Fact]
    public void ACarrierRepeatingMostOfWhatItSends_IsTheReasonATransferStalls()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 20, sent: true, returned: true, bytesOut: 10_000, bytesRetrans: 3_000);

        Assert.Contains("send 30% of its traffic again", reason);
    }

    [Fact]
    public void ACarrierRepeatingLittle_KeepsItsShareAndNoVerdict()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 20, sent: true, returned: true, bytesOut: 10_000, bytesRetrans: 1_000);

        Assert.Equal(string.Empty, reason);
        Assert.Equal(10, health.RetransPercent);
    }

    [Fact]
    public void ANearIdleCarrier_HasNoShareWorthReading()
    {
        var health = new CarrierHealth();

        var reason = Feed(health, 20, sent: true, returned: true, bytesOut: 100, bytesRetrans: 90);

        Assert.Equal(string.Empty, reason);
        Assert.Equal(-1, health.RetransPercent);
    }

    [Fact]
    public void AReDialledCarrier_IsJudgedOnWhatItDoesNext()
    {
        var health = new CarrierHealth();
        Feed(health, 11, sent: true, returned: false);

        health.Clear();
        var reason = Feed(health, 11, sent: true, returned: false);

        Assert.Equal(string.Empty, reason);
    }

    private static string Feed(CarrierHealth health, int seconds, bool sent, bool returned, int loss = Unknown, int rttMs = -1, long bytesOut = 0, long bytesRetrans = 0)
    {
        var reason = string.Empty;
        for (var second = 0; second < seconds; second++)
        {
            reason = health.Verdict(sent, returned, bytesOut, bytesRetrans, loss, rttMs);
        }

        return reason;
    }
}
