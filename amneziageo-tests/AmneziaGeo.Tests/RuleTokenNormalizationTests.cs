using AmneziaGeo.Cli;
using Xunit;

namespace AmneziaGeo.Tests;

public class RuleTokenNormalizationTests
{
    [Fact]
    public void BareNetwork_GetsTheCidrPrefix()
    {
        Assert.Equal("cidr:192.168.1.0/24", Rules.Normalize("192.168.1.0/24"));
        Assert.Equal("proxy|cidr:fde1:c450:1259::/48", Rules.Normalize("proxy|fde1:c450:1259::/48"));
    }

    [Fact]
    public void BareAddress_GetsTheCidrPrefix()
    {
        Assert.Equal("cidr:192.168.1.47", Rules.Normalize("192.168.1.47"));
        Assert.Equal("direct|cidr:2606:4700:4700::1111", Rules.Normalize("direct|2606:4700:4700::1111"));
    }

    [Fact]
    public void PrefixedAndNamedTokens_StandAsTheyAre()
    {
        Assert.Equal("cidr:10.0.0.0/8", Rules.Normalize("cidr:10.0.0.0/8"));
        Assert.Equal("proxy|geoip:ru", Rules.Normalize("proxy|geoip:ru"));
        Assert.Equal("domain:example.com", Rules.Normalize("domain:example.com"));
        Assert.Equal("youtube", Rules.Normalize("youtube"));
    }

    [Fact]
    public void NormalizedAddresses_PassValidation()
    {
        string[] rules = ["proxy|192.168.1.0/24", "10.10.10.0/24"];
        var normalized = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(rules, Rules.Normalize));
        Assert.Null(Rules.FirstInvalid(normalized));
    }
}
