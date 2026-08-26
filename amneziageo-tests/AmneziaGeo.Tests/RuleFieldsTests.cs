using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The "|server=...|fallback=..." tail both the agent and the window read a rule by.
/// </summary>
public sealed class RuleFieldsTests
{
    [Fact]
    public void RuleWithNoTail_AddressesNoServer()
    {
        var fields = RuleFields.Split("geoip:ru");

        Assert.Equal("geoip:ru", fields.Token);
        Assert.Equal(RuleTargetMode.Auto, fields.ServerMode);
        Assert.Equal(RuleTargetMode.Auto, fields.FallbackMode);
    }

    [Fact]
    public void RuleNamingAServer_KeepsTheNameAndItsCase()
    {
        var fields = RuleFields.Split("geoip:ru|server= DE ");

        Assert.Equal(RuleTargetMode.Server, fields.ServerMode);
        Assert.Equal("DE", fields.Server);
    }

    [Fact]
    public void BothFields_AreReadOffTheSameTail()
    {
        var fields = RuleFields.Split("geosite:openai|server=best|fallback=block");

        Assert.Equal(RuleTargetMode.Best, fields.ServerMode);
        Assert.Equal(RuleTargetMode.Block, fields.FallbackMode);
    }

    [Fact]
    public void FallbackSpelledNone_ReadsAsBlocked()
    {
        Assert.Equal(RuleTargetMode.Block, RuleFields.Split("geoip:ru|fallback=none").FallbackMode);
    }

    [Fact]
    public void FieldWithoutAValue_IsPassedOver()
    {
        var fields = RuleFields.Split("geoip:ru|server");

        Assert.Equal("geoip:ru", fields.Token);
        Assert.Equal(RuleTargetMode.Auto, fields.ServerMode);
    }

    [Fact]
    public void RuleAddressingNobody_WritesNoTail()
    {
        Assert.Equal(string.Empty, RuleFields.Tail(RuleTargetMode.Auto, string.Empty, RuleTargetMode.Auto, string.Empty));
    }

    [Fact]
    public void BothFieldsSet_WriteOneTailInOrder()
    {
        var tail = RuleFields.Tail(RuleTargetMode.Server, "de", RuleTargetMode.Direct, string.Empty);

        Assert.Equal("|server=de|fallback=direct", tail);
    }

    [Fact]
    public void FallbackAlone_WritesItsOwnField()
    {
        Assert.Equal("|fallback=best", RuleFields.Tail(RuleTargetMode.Auto, string.Empty, RuleTargetMode.Best, string.Empty));
    }

    [Theory]
    [InlineData(RuleTargetMode.Best, "")]
    [InlineData(RuleTargetMode.Server, "de")]
    [InlineData(RuleTargetMode.Direct, "")]
    [InlineData(RuleTargetMode.Block, "")]
    [InlineData(RuleTargetMode.Auto, "")]
    public void WhatIsWritten_ReadsBackTheSame(RuleTargetMode mode, string name)
    {
        var (read, readName) = RuleFields.Parse(RuleFields.Word(mode, name));

        Assert.Equal(mode, read);
        Assert.Equal(name, readName);
    }

    [Fact]
    public void TailWrittenByHand_SurvivesTheRoundTrip()
    {
        var tail = RuleFields.Tail(RuleTargetMode.Server, "fi", RuleTargetMode.Server, "de");
        var fields = RuleFields.Split("cidr:10.0.0.0/8" + tail);

        Assert.Equal("cidr:10.0.0.0/8", fields.Token);
        Assert.Equal("fi", fields.Server);
        Assert.Equal("de", fields.Fallback);
    }
}
