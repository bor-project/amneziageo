using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Access from the tunnel opens the machine at its own tunnel addresses, and only to what the tunnel carries:
/// the rule names the ranges instead of standing open to every remote address.
/// </summary>
public sealed class InboundFirewallTests
{
    [Fact]
    public void TheRule_OpensTheTunnelAddressesToTheTunnelRangesAlone()
    {
        var rule = InboundFirewall.Rule("home", "10.8.2.12/32", "10.8.2.0/24,192.168.1.0/24");

        Assert.Contains("dir=in action=allow", rule, StringComparison.Ordinal);
        Assert.Contains("localip=10.8.2.12/32", rule, StringComparison.Ordinal);
        Assert.Contains("remoteip=10.8.2.0/24,192.168.1.0/24", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("remoteip=any", rule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRule_CarriesTheNameOfTheTunnelItBelongsTo()
    {
        Assert.Contains("name=\"AmneziaGeo inbound: home\"", InboundFirewall.Rule("home", "10.8.2.12/32", "10.8.2.0/24"),
            StringComparison.Ordinal);
    }
}
