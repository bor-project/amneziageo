using AmneziaGeo.Decl;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The row of unavailable sites names Discord by its hosts and by the addresses of its voice servers, and the ready
/// set of the window fills a list with that row.
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
        var set = RoutingPresets.All[0];

        Assert.Equal("Closed", set.Key);
        Assert.Equal(RoutingDefaults.Unavailable, RoutingPresets.Rules(set.Proxy, []));
        Assert.Contains(Voice, set.Proxy);
    }
}
