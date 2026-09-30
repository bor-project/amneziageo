using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// When frequent handshakes name a dead link. A session renewed often while traffic keeps coming back is the rekey
/// schedule of one side or the other, and it must never read as a fault or be torn down for one.
/// </summary>
public sealed class LinkChurnTests
{
    private const int Unknown = LinkHealth.LossUnknown;

    [Fact]
    public void FrequentHandshakes_WithTheEchoesAnswered_AreNotChurn()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 15, lossPercent: 0, rxBytesPerSecond: 0);

        Assert.Equal(4, reading.HandshakesPerMinute);
        Assert.False(reading.Churning);
    }

    [Fact]
    public void FrequentHandshakes_WithTheEchoesLost_AreChurn()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 15, lossPercent: 80, rxBytesPerSecond: 0);

        Assert.True(reading.Churning);
    }

    [Fact]
    public void FrequentHandshakes_WithNothingToEchoAndNothingReceived_AreChurn()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 15, lossPercent: Unknown, rxBytesPerSecond: 50);

        Assert.True(reading.Churning);
    }

    [Fact]
    public void FrequentHandshakes_WithNothingToEchoAndTrafficArriving_AreNotChurn()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 15, lossPercent: Unknown, rxBytesPerSecond: 10_000);

        Assert.False(reading.Churning);
    }

    [Fact]
    public void RareHandshakes_OnADeadLink_AreNotChurn()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 60, lossPercent: 100, rxBytesPerSecond: 0);

        Assert.False(reading.Churning);
    }

    [Fact]
    public void HandshakesSpreadOverSleep_AreCountedOnTheirOwnTimes()
    {
        var reading = Awake(handshakeEverySeconds: 120, lossPercent: 80);

        Assert.Equal(1, reading.HandshakesPerMinute);
        Assert.False(reading.Churning);
    }

    [Fact]
    public void FrequentHandshakes_OnTheWallClockToo_AreChurn()
    {
        var reading = Awake(handshakeEverySeconds: 5, lossPercent: 80);

        Assert.Equal(12, reading.HandshakesPerMinute);
        Assert.True(reading.Churning);
    }

    [Fact]
    public void AConfigThatRenewsItsSessionOften_IsJudgedByItsOwnSchedule()
    {
        var reading = TwoMinutes(handshakeEverySeconds: 15, lossPercent: 80, rxBytesPerSecond: 0, LinkHealth.ChurnPerMinuteFor(20));

        Assert.False(reading.Churning);
    }

    [Theory]
    [InlineData(0, LinkHealth.ChurnPerMinute)]
    [InlineData(120, LinkHealth.ChurnPerMinute)]
    [InlineData(30, 4)]
    [InlineData(20, 6)]
    public void TheRateJudged_IsTwiceTheConfigsOwnSchedule(int rekeyAfterSeconds, int churnPerMinute)
    {
        Assert.Equal(churnPerMinute, LinkHealth.ChurnPerMinuteFor(rekeyAfterSeconds));
    }

    [Theory]
    [InlineData("RekeyAfterTime = 100-135", 100)]
    [InlineData("rekeyaftertime=40", 40)]
    [InlineData("RekeyTimeout = 5-6", 0)]
    [InlineData("MTU = 1280", 0)]
    public void TheShortestRekeyInterval_IsReadFromTheConfig(string line, int seconds)
    {
        var config = $"[Interface]\nPrivateKey = key\n{line}\n\n[Peer]\nEndpoint = 192.0.2.1:51820\n";

        Assert.Equal(seconds, WgConfigEditor.GetRekeyAfterSeconds(config));
    }

    // Two minutes awake in five-second samples, each with a handshake the given wall seconds after the last.
    private static LinkReading Awake(int handshakeEverySeconds, int lossPercent)
    {
        var now = 0L;
        var meter = new LinkMeter(() => now);
        var handshake = 1_000_000L;
        var reading = LinkReading.Empty;
        for (var second = 0; second <= LinkHealth.WindowSeconds; second += 5)
        {
            now = second * 1000L;
            handshake += handshakeEverySeconds;
            reading = meter.Sample(0, 0, handshake, lossPercent);
        }

        return reading;
    }

    // Two minutes of five-second samples: a handshake every given number of seconds and a steady receive rate.
    private static LinkReading TwoMinutes(int handshakeEverySeconds, int lossPercent, long rxBytesPerSecond, int churnPerMinute = LinkHealth.ChurnPerMinute)
    {
        var now = 0L;
        var meter = new LinkMeter(() => now) { ChurnPerMinute = churnPerMinute };
        var handshake = 1_000L;
        var reading = LinkReading.Empty;
        for (var second = 0; second <= LinkHealth.WindowSeconds; second += 5)
        {
            now = second * 1000L;
            if (second % handshakeEverySeconds == 0)
            {
                handshake++;
            }

            reading = meter.Sample(second * rxBytesPerSecond, 0, handshake, lossPercent);
        }

        return reading;
    }
}
