using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The side a session takes when nothing names the program behind it: a program that lives for milliseconds is
/// gone before its first datagram is asked about, and no answer arrives in time in user mode.
/// </summary>
public sealed class AppGatewayUnknownTests
{
    [Fact]
    public void NothingAsked_SendsTheUnnamedSessionIntoTheTunnel()
    {
        Assert.True(AppGateway.UnknownRidesTunnel(null));
        Assert.True(AppGateway.UnknownRidesTunnel(string.Empty));
        Assert.True(AppGateway.UnknownRidesTunnel("tunnel"));
    }

    [Fact]
    public void DirectAsked_LeavesTheUnnamedSessionPastTheTunnel()
    {
        Assert.False(AppGateway.UnknownRidesTunnel("direct"));
        Assert.False(AppGateway.UnknownRidesTunnel("  DIRECT  "));
    }
}
