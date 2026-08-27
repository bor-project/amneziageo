using AmneziaGeo.Ipc.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The duties cross into the tunnel process as one settings row, so what it reads back has to be what the agent
/// wrote - and a row that was never written has to read as the machine that runs a single tunnel.
/// </summary>
public sealed class TunnelDutiesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingWrittenIsTheOnlyTunnel(string? text)
    {
        Assert.Equal(TunnelDuties.Sole, TunnelDuties.Parse(text));
    }

    [Fact]
    public void RoundTripsEveryDuty()
    {
        Assert.Equal(TunnelDuties.Sole, TunnelDuties.Parse(TunnelDuties.Sole.Format()));
        Assert.Equal(TunnelDuties.None, TunnelDuties.Parse(TunnelDuties.None.Format()));
        Assert.Equal(new TunnelDuties(true, false), TunnelDuties.Parse(new TunnelDuties(true, false).Format()));
        Assert.Equal(new TunnelDuties(false, true), TunnelDuties.Parse(new TunnelDuties(false, true).Format()));
    }

    [Fact]
    public void ReadsOneDutyWithoutTheOther()
    {
        Assert.Equal(new TunnelDuties(true, false), TunnelDuties.Parse("default"));
        Assert.Equal(new TunnelDuties(false, true), TunnelDuties.Parse("resolver"));
    }
}
