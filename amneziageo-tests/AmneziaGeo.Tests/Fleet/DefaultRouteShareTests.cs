using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A tunnel that does not carry the default must not be handed one: the halves are what the engine actually
/// routes, so leaving them on a second tunnel hands it the whole machine.
/// </summary>
public sealed class DefaultRouteShareTests
{
    [Fact]
    public void CarrierGetsHalvesInsteadOfDefault()
    {
        var shaped = TunnelRunner.SplitDefaultRoutes(["0.0.0.0/0", "::/0", "10.8.0.0/24"], carriesDefault: true);

        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1", "10.8.0.0/24"], shaped);
    }

    [Fact]
    public void NonCarrierKeepsOnlyItsOwnRanges()
    {
        var shaped = TunnelRunner.SplitDefaultRoutes(["0.0.0.0/0", "::/0", "10.8.0.0/24"], carriesDefault: false);

        Assert.Equal(["10.8.0.0/24"], shaped);
    }

    [Fact]
    public void NonCarrierLosesHalvesWrittenByHand()
    {
        var shaped = TunnelRunner.SplitDefaultRoutes(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1", "192.168.9.0/24"], carriesDefault: false);

        Assert.Equal(["192.168.9.0/24"], shaped);
    }

    [Fact]
    public void CarrierKeepsHalvesWrittenByHand()
    {
        var shaped = TunnelRunner.SplitDefaultRoutes(["0.0.0.0/1", "128.0.0.0/1", "192.168.9.0/24"], carriesDefault: true);

        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1", "192.168.9.0/24"], shaped);
    }

    [Fact]
    public void HostAddressIsItsOwnGround()
    {
        Assert.Equal(["10.0.0.2/32"], TunnelRunner.OwnNetworks(["10.0.0.2/32"]));
    }

    [Fact]
    public void AddressWithoutAPrefixIsAHostOfItsOwn()
    {
        Assert.Equal(["10.0.0.2/32"], TunnelRunner.OwnNetworks(["10.0.0.2"]));
    }

    [Fact]
    public void AddressIsMaskedDownToItsNetwork()
    {
        Assert.Equal(["10.8.0.0/24", "fd00::/64"], TunnelRunner.OwnNetworks(["10.8.0.37/24", "fd00::5/64"]));
    }

    [Fact]
    public void NonsenseLeavesNothingBehind()
    {
        Assert.Empty(TunnelRunner.OwnNetworks(["not-an-address/24", string.Empty]));
    }
}
