using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The row of unavailable sites names Discord by its hosts and by the addresses of its voice servers and leaves the
/// country-wide blocked lists out, and the ready set of the window fills a list with that row and keeps UDP to it.
/// </summary>
public sealed class RoutingDefaultsTests
{
    private const string Hosts = "geosite:discord";

    private const string Voice = "geoip:ag-discord-voice";

    [Fact]
    public void TheRowOfUnavailableSites_NamesTheVoiceOfDiscordNextToItsHosts()
    {
        var row = RoutingDefaults.Unavailable;

        Assert.Equal(Array.IndexOf(row, Hosts) + 1, Array.IndexOf(row, Voice));
        Assert.Equal(row.Length, row.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSetOfUnavailableSites_FillsAListWithTheRow()
    {
        var set = RoutingPresets.All[1];

        Assert.Equal("Closed", set.Key);
        Assert.Equal(RoutingDefaults.Unavailable, RoutingPresets.Rules(set.Proxy, []));
        Assert.Contains(Voice, set.Proxy);
    }

    [Fact]
    public void TheRowOfUnavailableSites_LeavesTheCountryBlockedListsOut()
    {
        Assert.DoesNotContain(RoutingDefaults.Unavailable, rule => rule.Contains("ru-blocked", StringComparison.Ordinal));
        Assert.Contains("geosite:youtube", RoutingDefaults.Unavailable);
    }

    [Fact]
    public void TheSetOfUnavailableSites_LeavesAllUdpOff()
    {
        Assert.False(RoutingPresets.All[1].AllUdp);
    }

    [Fact]
    public void TheSetOfUnavailableApps_LeadsTheSitesAndNamesTheAppsOfTheirServices()
    {
        var set = RoutingPresets.All[0];

        Assert.Equal("Apps", set.Key);
        Assert.Equal(
            ["app:YouTube", "app:Instagram", "app:Facebook", "app:WhatsApp", "app:X", "app:Discord", "app:ChatGPT", "app:Telegram"],
            RoutingPresets.Rules(set.Proxy, []));
        Assert.All(set.Proxy, rule => Assert.True(AppNames.IsName(rule["app:".Length..])));
        Assert.Empty(set.Direct);
        Assert.False(set.UseGlobalProxy);
        Assert.False(set.AllUdp);
        Assert.False(set.LocalSubnets);
    }
}
