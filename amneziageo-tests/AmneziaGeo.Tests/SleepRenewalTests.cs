using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// When sleep has left the engine on session keys the server no longer takes.
/// </summary>
public sealed class SleepRenewalTests
{
    [Fact]
    public void ASessionTheSleepCarriedPastTheRekeyAge_IsRenewed()
    {
        // The Poco at 16:42: the peer last answered 405 s ago, and the device slept 6 min of it.
        Assert.True(HandshakeAge.OutlivedBySleep(405, 380_000));
    }

    [Fact]
    public void ASessionTheEngineItselfCountsPastTheRekeyAge_IsLeftToTheEngine()
    {
        Assert.False(HandshakeAge.OutlivedBySleep(405, 60_000));
    }

    [Fact]
    public void ASessionYoungerThanTheRekeyAge_IsLeftAlone()
    {
        Assert.False(HandshakeAge.OutlivedBySleep(HandshakeAge.RekeySeconds - 1, 100_000));
    }

    [Fact]
    public void ASessionOfADeviceThatNeverSlept_IsLeftToTheEngine()
    {
        Assert.False(HandshakeAge.OutlivedBySleep(600, 0));
    }

    [Fact]
    public void ASessionNeverAnswered_IsNotRenewed()
    {
        Assert.False(HandshakeAge.OutlivedBySleep(-1, 600_000));
    }

    [Fact]
    public void TheRekeyAgeItself_CountsAsReached()
    {
        Assert.True(HandshakeAge.OutlivedBySleep(HandshakeAge.RekeySeconds, 1_000));
    }

    [Fact]
    public void TheRekeyAgeTheConfigNames_IsTheOneCounted()
    {
        Assert.True(HandshakeAge.OutlivedBySleep(110, 100_000, 100));
        Assert.False(HandshakeAge.OutlivedBySleep(110, 100_000, 130));
    }

    [Fact]
    public void AConfigThatNamesNoRekeyAge_CountsTheDefault()
    {
        Assert.False(HandshakeAge.OutlivedBySleep(110, 100_000, 0));
        Assert.True(HandshakeAge.OutlivedBySleep(130, 100_000, 0));
    }
}
