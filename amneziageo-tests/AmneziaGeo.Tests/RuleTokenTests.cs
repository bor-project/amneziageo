using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rule token as it travels between the agent and everything that edits a routing list: the match, the role,
/// and the servers the match rides.
/// </summary>
public sealed class RuleTokenTests
{
    [Theory]
    [InlineData("geoip:ru", RouteRole.Proxy)]
    [InlineData("proxy|geoip:ru", RouteRole.Proxy)]
    [InlineData("direct|geoip:ru", RouteRole.Direct)]
    [InlineData("block|geoip:ru", RouteRole.Block)]
    public void TokenWithoutATail_ReadsTheWayItAlwaysDid(string token, RouteRole role)
    {
        var rule = GeoConfigurator.ParseRoleRule(token);

        Assert.NotNull(rule);
        Assert.Equal(GeoRuleKind.GeoIp, rule.Kind);
        Assert.Equal("ru", rule.Value);
        Assert.Equal(role, rule.Role);
        Assert.Equal(RuleTargetMode.Auto, rule.ServerMode);
        Assert.Equal(string.Empty, rule.Server);
        Assert.Equal(RuleTargetMode.Auto, rule.FallbackMode);
    }

    [Fact]
    public void RuleWithoutAServer_FormatsTheWayItAlwaysDid()
    {
        Assert.Equal("proxy|geoip:ru", GeoConfigurator.FormatWithRole(new GeoRule(GeoRuleKind.GeoIp, "ru")));
        Assert.Equal("direct|cidr:10.0.0.0/8", GeoConfigurator.FormatWithRole(new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct)));
    }

    [Theory]
    [InlineData("proxy|geoip:netflix|server=de", RuleTargetMode.Server, "de", RuleTargetMode.Auto, "")]
    [InlineData("proxy|geoip:netflix|server=best", RuleTargetMode.Best, "", RuleTargetMode.Auto, "")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=fi", RuleTargetMode.Server, "de", RuleTargetMode.Server, "fi")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=best", RuleTargetMode.Server, "de", RuleTargetMode.Best, "")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=direct", RuleTargetMode.Server, "de", RuleTargetMode.Direct, "")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=block", RuleTargetMode.Server, "de", RuleTargetMode.Block, "")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=auto", RuleTargetMode.Server, "de", RuleTargetMode.Auto, "")]
    [InlineData("proxy|geoip:netflix|SERVER=de|Fallback=BLOCK", RuleTargetMode.Server, "de", RuleTargetMode.Block, "")]
    public void TokenCarriesTheServersItNames(string token, RuleTargetMode serverMode, string server, RuleTargetMode fallbackMode, string fallback)
    {
        var rule = GeoConfigurator.ParseRoleRule(token);

        Assert.NotNull(rule);
        Assert.Equal("netflix", rule.Value);
        Assert.Equal(serverMode, rule.ServerMode);
        Assert.Equal(server, rule.Server);
        Assert.Equal(fallbackMode, rule.FallbackMode);
        Assert.Equal(fallback, rule.Fallback);
    }

    [Fact]
    public void BlockingFallbackStillReadsByItsOldSpelling()
    {
        var rule = GeoConfigurator.ParseRoleRule("proxy|geoip:netflix|server=de|fallback=none");

        Assert.NotNull(rule);
        Assert.Equal(RuleTargetMode.Block, rule.FallbackMode);
        Assert.Equal("proxy|geoip:netflix|server=de|fallback=block", GeoConfigurator.FormatWithRole(rule));
    }

    [Theory]
    [InlineData("proxy|geoip:netflix")]
    [InlineData("proxy|geoip:netflix|server=de")]
    [InlineData("proxy|geoip:netflix|server=best")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=fi")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=best")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=direct")]
    [InlineData("proxy|geoip:netflix|server=de|fallback=block")]
    [InlineData("proxy|geoip:netflix|fallback=block")]
    [InlineData("direct|cidr:10.0.0.0/8")]
    public void TokenSurvivesTheRoundTrip(string token)
    {
        var rule = GeoConfigurator.ParseRoleRule(token);

        Assert.NotNull(rule);
        Assert.Equal(token, GeoConfigurator.FormatWithRole(rule));
    }

    [Theory]
    [InlineData("direct|geoip:ru|server=de|fallback=fi")]
    [InlineData("block|domain:x.com|server=de")]
    public void RuleOutsideTheProxyBucket_KeepsNoServer(string token)
    {
        var rule = GeoConfigurator.ParseRoleRule(token);

        Assert.NotNull(rule);
        Assert.Equal(RuleTargetMode.Auto, rule.ServerMode);
        Assert.Equal(string.Empty, rule.Server);
        Assert.Equal(RuleTargetMode.Auto, rule.FallbackMode);
        Assert.Equal(string.Empty, rule.Fallback);
    }

    [Theory]
    [InlineData("proxy|geoip:netflix|server=direct")]
    [InlineData("proxy|geoip:netflix|server=block")]
    public void ServerAskedToLeaveTheTunnel_FallsBackToAuto(string token)
    {
        var rule = GeoConfigurator.ParseRoleRule(token);

        Assert.NotNull(rule);
        Assert.Equal(RuleTargetMode.Auto, rule.ServerMode);
        Assert.Equal("proxy|geoip:netflix", GeoConfigurator.FormatWithRole(rule));
    }

    [Fact]
    public void UnknownFieldInTheTail_IsDropped()
    {
        var rule = GeoConfigurator.ParseRoleRule("proxy|geoip:netflix|weird=1|server=de");

        Assert.NotNull(rule);
        Assert.Equal("de", rule.Server);
        Assert.Equal("proxy|geoip:netflix|server=de", GeoConfigurator.FormatWithRole(rule));
    }

    [Fact]
    public void PortableFormat_LeavesTheServerNamesBehind()
    {
        var rule = new GeoRule(GeoRuleKind.GeoIp, "netflix", RouteRole.Proxy, RuleTargetMode.Server, "de", RuleTargetMode.Server, "fi");

        Assert.Equal("proxy|geoip:netflix", GeoConfigurator.FormatPortable(rule));
    }

    [Fact]
    public void MergeKeepsTheStoredRuleAndAddsWhatIsNew()
    {
        var stored = new[]
        {
            new GeoRule(GeoRuleKind.GeoIp, "netflix", RouteRole.Proxy, RuleTargetMode.Server, "de"),
            new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct),
        };

        var merged = GeoConfigurator.MergeRules(stored, ["proxy|geoip:netflix", "proxy|geoip:hulu", "direct|cidr:10.0.0.0/8"]);

        Assert.Equal(["proxy|geoip:netflix|server=de", "direct|cidr:10.0.0.0/8", "proxy|geoip:hulu"], merged);
    }

    [Fact]
    public void MergeKeepsATokenItCannotRead()
    {
        var merged = GeoConfigurator.MergeRules([], ["nonsense", "nonsense"]);

        Assert.Equal(["nonsense"], merged);
    }
}
