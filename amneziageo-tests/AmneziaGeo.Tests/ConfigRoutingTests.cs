using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server of ours bans routing on the device in what it offers, and each config has a switch of its own.
/// </summary>
public sealed class ConfigRoutingTests
{
    [Fact]
    public void AConfig_TakesTheListOnlyWithItsSwitchOnAndNoBan()
    {
        var on = new ConfigTransport("one", false);
        var off = new ConfigTransport("one", false, UseRouting: false);
        var banned = Offer(false);
        var free = Offer(true);

        Assert.True(ConfigRouting.Allowed(null, null));
        Assert.True(ConfigRouting.Allowed(on, free));
        Assert.True(ConfigRouting.Allowed(on, ServerOffer.None));
        Assert.False(ConfigRouting.Allowed(off, free));
        Assert.False(ConfigRouting.Allowed(on, banned));
        Assert.False(ConfigRouting.Allowed(null, banned));
    }

    [Fact]
    public void TheBan_IsReadOnlyFromAnAllowedThatSaysNo()
    {
        Assert.True(Offer(false).RoutingLocked);
        Assert.False(Offer(true).RoutingLocked);
        Assert.False(ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{}}""").RoutingLocked);
        Assert.False(ServerOffer.None.RoutingLocked);
    }

    private static ServerOffer Offer(bool allowed) =>
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"routing":{"allowed":ALLOWED}}}""".Replace("ALLOWED", allowed ? "true" : "false", StringComparison.Ordinal));
}
