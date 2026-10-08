using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Each config has a switch of its own for the routing list, and nothing a server offers overrides it.
/// </summary>
public sealed class ConfigRoutingTests
{
    [Fact]
    public void AConfig_TakesTheListByItsSwitchAlone()
    {
        var on = new ConfigTransport("one", false);
        var off = new ConfigTransport("one", false, UseRouting: false);

        Assert.True(ConfigRouting.Allowed(null));
        Assert.True(ConfigRouting.Allowed(on));
        Assert.False(ConfigRouting.Allowed(off));
    }

    [Fact]
    public void TheBanOfAnOlderServer_ChangesNothingOfItsOffer()
    {
        Assert.True(Offer(false).Settles(Offer(true)));
        Assert.True(Offer(false).Settles(ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{}}""")));
    }

    private static ServerOffer Offer(bool allowed) =>
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"routing":{"allowed":ALLOWED}}}""".Replace("ALLOWED", allowed ? "true" : "false", StringComparison.Ordinal));
}
