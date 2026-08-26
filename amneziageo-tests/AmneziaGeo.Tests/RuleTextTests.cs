using AmneziaGeo.Cli;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// How the command line reads a stored rule: the bucket it belongs to, the token two spellings share, and what
/// it refuses to store.
/// </summary>
public sealed class RuleTextTests
{
    [Theory]
    [InlineData("proxy|geoip:ru", "proxy")]
    [InlineData("direct|cidr:10.0.0.0/8", "direct")]
    [InlineData("block|domain:ads.example", "block")]
    [InlineData("geoip:ru", "proxy")]
    public void RuleWithoutARole_ReadsAsProxied(string rule, string role)
    {
        Assert.Equal(role, Rules.Role(rule));
    }

    [Fact]
    public void ADomainCarryingABar_IsNotMistakenForARole()
    {
        Assert.Equal("proxy", Rules.Role("domain:a|b"));
        Assert.Equal("domain:a|b", Rules.Bare("domain:a|b"));
    }

    [Fact]
    public void TheTail_IsNotPartOfTheToken()
    {
        Assert.Equal("geoip:ru", Rules.Plain("proxy|geoip:ru|server=de|fallback=block"));
    }

    [Fact]
    public void TwoSpellingsOfOneRule_ShareTheirToken()
    {
        Assert.Equal(Rules.Plain("geosite:openai"), Rules.Plain("proxy|geosite:openai|server=fi"));
    }

    [Fact]
    public void RuleNamingAServer_IsStored()
    {
        Assert.Null(Rules.FirstInvalid(["proxy|geoip:ru|server=de|fallback=direct"]));
    }

    [Fact]
    public void TailUnderARuleThatNamesNoRole_IsRefused()
    {
        Assert.Equal("geoip:ru|server=de", Rules.FirstInvalid(["geoip:ru|server=de"]));
    }

    [Theory]
    [InlineData("proxy|geoip:ru|carrier=de")]
    [InlineData("proxy|geoip:ru|server=")]
    [InlineData("proxy|geoip:ru|server")]
    public void TailNamingNoFieldOfARule_IsRefused(string rule)
    {
        Assert.Equal(rule, Rules.FirstInvalid([rule]));
    }
}
