using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ConfigTransportChangesTests
{
    [Fact]
    public void DefaultsAndUnusedLegacyFieldsDoNotRequireReconnect()
    {
        var transport = new ConfigTransport("vpn", false, 0);

        Assert.False(ConfigTransportChanges.RequiresRestart(null, transport with { Ipv6Mode = Ipv6Mode.Auto }));
        Assert.True(ConfigTransportChanges.RequiresRestart(null, transport));
        Assert.False(ConfigTransportChanges.RequiresRestart(transport, transport with { ServicesPort = 9443 }));
        Assert.False(ConfigTransportChanges.RequiresRestart(transport, transport with
        {
            UseWebSocket = true,
            WebSocketHost = "wss://unused.example.net/path",
            WebSocketPort = 9443,
            Mtu = 1420,
        }));
    }

    [Theory]
    [InlineData(MtuMode.Auto, false)]
    [InlineData(MtuMode.Config, false)]
    [InlineData(MtuMode.Custom, true)]
    public void AStoredMtuOnlyAffectsItsCustomMode(MtuMode mode, bool expected)
    {
        var transport = new ConfigTransport("vpn", false, 1420, MtuMode: mode);

        Assert.Equal(expected, ConfigTransportChanges.RequiresRestart(transport, transport with { Mtu = 1380 }));
    }

    [Fact]
    public void SessionSettingsStillRequireReconnect()
    {
        var transport = new ConfigTransport("vpn", false, 0);

        Assert.True(ConfigTransportChanges.RequiresRestart(transport, transport with { UseIpv6 = true }));
        Assert.True(ConfigTransportChanges.RequiresRestart(transport, transport with { UseRouter = false }));
        Assert.True(ConfigTransportChanges.RequiresRestart(transport, transport with { UseRouting = false }));
        Assert.True(ConfigTransportChanges.RequiresRestart(transport, transport with { AllowSignal = false }));
        Assert.True(ConfigTransportChanges.RequiresRestart(transport, transport with { MtuMode = MtuMode.Config }));
    }

    [Fact]
    public void SettingsAppliedLivePreserveTheirPlatformPolicy()
    {
        var transport = new ConfigTransport("vpn", false, 0);
        var updated = transport with { AllowInbound = true, InboundNetwork = true, HoldTraffic = false };

        Assert.True(ConfigTransportChanges.RequiresRestart(transport, updated));
        Assert.False(ConfigTransportChanges.RequiresRestart(transport, updated, inboundApplied: true, holdApplied: true));
    }

    [Theory]
    [InlineData(true, Ipv6Mode.On, false)]
    [InlineData(true, Ipv6Mode.Off, true)]
    [InlineData(false, Ipv6Mode.On, true)]
    [InlineData(false, Ipv6Mode.Off, false)]
    [InlineData(null, Ipv6Mode.On, true)]
    public void Ipv6PolicyChangesOnlyRestartWhenTheEffectiveSettingChanges(bool? available, Ipv6Mode mode, bool restart)
    {
        var transport = new ConfigTransport("vpn", false, 0) { Ipv6Mode = Ipv6Mode.Auto };
        var updated = transport with { Ipv6Mode = mode };
        Assert.Equal(restart, ConfigTransportChanges.RequiresRestart(transport, updated, ipv6Available: available));
        Assert.Equal(restart, ConfigTransportChanges.RequiresRestart(updated, transport, ipv6Available: available));
    }
}
