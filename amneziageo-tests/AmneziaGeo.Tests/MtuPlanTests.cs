using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The MTU a tunnel comes up with is a choice of three: the size the client picks for the link, the one the
/// config text declares, and the one a person set. A packet that comes out too large is fragmented or dropped by
/// the path, so the picked size has to leave room for the headers and for the padding a 3.1 profile adds.
/// </summary>
public sealed class MtuPlanTests
{
    private const string Plain = """
        [Interface]
        Address = 10.0.0.2/32
        Jc = 4
        S1 = 30
        H1 = 1234567
        """;

    private const string Extended = """
        [Interface]
        Address = 10.0.0.2/32
        Jc = 4
        H1 = 1234567-2234567
        ContentPaddingAddition = 0-44
        RandomTrailers = on
        """;

    private static string WithMtu(string config, int mtu) => config + "\nMTU = " + mtu + "\n";

    [Fact]
    public void Auto_LeavesRoomForTheHeaders()
    {
        Assert.Equal(1440, MtuPlan.Ceiling(Plain));
    }

    [Fact]
    public void Auto_LeavesRoomForThePaddingA31ProfileAdds()
    {
        // 1500 - 60 header - 44 padding - 16 trailer.
        Assert.Equal(1380, MtuPlan.Ceiling(Extended));
    }

    [Fact]
    public void Auto_ChargesNothingForKeysTheConfigTurnedOff()
    {
        var off = """
            [Interface]
            Address = 10.0.0.2/32
            S1 = 86
            H1 = 1701064620
            RandomTrailers = off
            DisableCookies = off
            """;

        Assert.Equal(1440, MtuPlan.Ceiling(off));
    }

    [Fact]
    public void Auto_ChargesOnlyThePaddingTheConfigNames()
    {
        var padded = """
            [Interface]
            Address = 10.0.0.2/32
            ContentPaddingAddition = 13-42
            """;

        Assert.Equal(1398, MtuPlan.Ceiling(padded));
    }

    [Fact]
    public void Auto_FollowsTheLinkWhenItCarriesLess()
    {
        Assert.Equal(1432, MtuPlan.Ceiling(Plain, 1492));
    }

    [Fact]
    public void Auto_NeverGoesOverWhatTheConfigDeclares()
    {
        Assert.Equal(1340, MtuPlan.Resolve(MtuMode.Auto, 0, WithMtu(Plain, 1340)));
    }

    [Fact]
    public void Auto_IgnoresTheStoredSize()
    {
        Assert.Equal(1440, MtuPlan.Resolve(MtuMode.Auto, 1200, Plain));
    }

    [Fact]
    public void Config_TakesTheDeclaredSizeOverTheStoredOne()
    {
        Assert.Equal(1340, MtuPlan.Resolve(MtuMode.Config, 1200, WithMtu(Plain, 1340)));
    }

    [Fact]
    public void Config_WithoutOneDeclared_TakesTheDefault()
    {
        Assert.Equal(WgConfigEditor.DefaultMtu, MtuPlan.Resolve(MtuMode.Config, 1200, Plain));
    }

    [Fact]
    public void Custom_TakesTheStoredSize()
    {
        Assert.Equal(1200, MtuPlan.Resolve(MtuMode.Custom, 1200, WithMtu(Plain, 1340)));
    }

    [Fact]
    public void Custom_WithoutAStoredSize_FallsBackToTheConfig()
    {
        Assert.Equal(1340, MtuPlan.Resolve(MtuMode.Custom, 0, WithMtu(Plain, 1340)));
    }

    [Fact]
    public void Ceiling_StaysWithinTheAllowedSizes()
    {
        Assert.Equal(MtuModes.MinMtu, MtuPlan.Ceiling(Extended, 300));
        Assert.Equal(1440, MtuPlan.Ceiling(Plain, 9000));
    }

    [Fact]
    public void Mode_ReadsWhatWasStored()
    {
        Assert.Equal(MtuMode.Auto, MtuModes.From(0));
        Assert.Equal(MtuMode.Config, MtuModes.From(1));
        Assert.Equal(MtuMode.Custom, MtuModes.From(2));
        Assert.Equal(MtuMode.Auto, MtuModes.From(7));
    }

    [Fact]
    public void Mode_ReadsWhatWasTyped()
    {
        Assert.True(MtuModes.TryParse("auto", out var auto, out var noSize));
        Assert.Equal(MtuMode.Auto, auto);
        Assert.Equal(0, noSize);

        Assert.True(MtuModes.TryParse("config", out var config, out _));
        Assert.Equal(MtuMode.Config, config);

        Assert.True(MtuModes.TryParse("1380", out var custom, out var size));
        Assert.Equal(MtuMode.Custom, custom);
        Assert.Equal(1380, size);
    }

    [Fact]
    public void Mode_TurnsDownASizeNoTunnelTakes()
    {
        Assert.False(MtuModes.TryParse("100", out _, out _));
        Assert.False(MtuModes.TryParse("9000", out _, out _));
        Assert.False(MtuModes.TryParse("wide", out _, out _));
    }

    [Fact]
    public void Mode_KeepsTheStoredOneWhenTheNameIsUnknown()
    {
        Assert.Equal(MtuMode.Custom, MtuModes.Parse("", MtuMode.Custom));
        Assert.Equal(MtuMode.Auto, MtuModes.Parse("auto", MtuMode.Custom));
    }

    [Fact]
    public void OnlyTheCustomMode_LeavesTheSizeToThePerson()
    {
        Assert.True(MtuModes.IsReadOnly(MtuMode.Auto));
        Assert.True(MtuModes.IsReadOnly(MtuMode.Config));
        Assert.False(MtuModes.IsReadOnly(MtuMode.Custom));
    }
}
