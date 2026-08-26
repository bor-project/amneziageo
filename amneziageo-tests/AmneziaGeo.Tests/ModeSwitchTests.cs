using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Who is left standing when several servers stop working at once: the server carrying everything, and nobody
/// beside it.
/// </summary>
public sealed class ModeSwitchTests
{
    private static readonly string[] Order = ["fi", "de", "nl"];

    [Fact]
    public void WithNothingUp_NothingStaysAndNothingGoes()
    {
        var settle = ModeSwitch.Settle(Order, [], null);

        Assert.Equal(string.Empty, settle.Keeper);
        Assert.Empty(settle.Dropped);
    }

    [Fact]
    public void ServerCarryingEverything_StaysUpAndTheRestGoDown()
    {
        var settle = ModeSwitch.Settle(Order, ["fi", "de", "nl"], "de");

        Assert.Equal("de", settle.Keeper);
        Assert.Equal(["fi", "nl"], settle.Dropped);
    }

    [Fact]
    public void WithNobodyCarryingEverything_TheFirstServerUpThePriorityStays()
    {
        var settle = ModeSwitch.Settle(Order, ["nl", "de"], null);

        Assert.Equal("de", settle.Keeper);
        Assert.Equal(["nl"], settle.Dropped);
    }

    [Fact]
    public void ServerCarryingEverythingThatIsNotOurs_LeavesThePriorityToSayWhoStays()
    {
        var settle = ModeSwitch.Settle(Order, ["nl", "de"], "fi");

        Assert.Equal("de", settle.Keeper);
        Assert.Equal(["nl"], settle.Dropped);
    }

    [Fact]
    public void ServerThePriorityDoesNotKnow_StaysWhenItIsTheOnlyThingUp()
    {
        var settle = ModeSwitch.Settle([], ["se"], null);

        Assert.Equal("se", settle.Keeper);
        Assert.Empty(settle.Dropped);
    }

    [Fact]
    public void OneServerUp_HasNothingToTakeDown()
    {
        var settle = ModeSwitch.Settle(Order, ["de"], "de");

        Assert.Equal("de", settle.Keeper);
        Assert.Empty(settle.Dropped);
    }
}
