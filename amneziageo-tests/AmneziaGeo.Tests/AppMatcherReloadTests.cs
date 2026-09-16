using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rules by application are replaced on a running matcher, so a list edited while the tunnel stands is in
/// force without reconnecting it.
/// </summary>
public class AppMatcherReloadTests
{
    private static AppMatcher Matcher(params string[] rules) => new(rules, NullLogger<AppMatcher>.Instance);

    [Fact]
    public void AnEditedRuleSetIsPutInForce()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload(["name=first.exe", "pkg=Some.App_publisher"]));
        Assert.True(matcher.HasMatchers);
    }

    [Fact]
    public void TheSameRulesAgainChangeNothing()
    {
        var matcher = Matcher("name=first.exe");

        Assert.False(matcher.Reload(["NAME=first.exe".ToLowerInvariant()]));
    }

    [Fact]
    public void RemovingEveryRuleLeavesNothingMatched()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload([]));
        Assert.False(matcher.HasMatchers);
    }
}
