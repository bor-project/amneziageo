using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// How the machine's one list is dealt out to the tunnels that are up: what each of them is handed, when the
/// count is done again, and what the journal says about it.
/// </summary>
public sealed class RoutingDistributorTests : IAsyncLifetime
{
    // One range no rule addresses anywhere, one a rule sends to a server by name.
    private const string Unaddressed = "10.1.0.0/16";
    private const string Addressed = "10.2.0.0/16";

    private MachineHarness _machine = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _machine = await MachineHarness.StartAsync();
        await _machine.LibraryAsync("fi", "de", "nl");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _machine.DisposeAsync();
    }

    [Fact]
    public async Task ServerCarryingEverything_TakesTheRulesThatNameNobody()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");

        await _machine.DistributeAsync();

        Assert.Equal([Unaddressed], await _machine.CarriedAsync("fi"));
        Assert.Equal([Addressed], await _machine.CarriedAsync("de"));
    }

    [Fact]
    public async Task WithOneTunnelUp_EverythingRidesIt()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi");

        await _machine.DistributeAsync();

        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("fi"));
    }

    [Fact]
    public async Task ServerThatFell_LeavesWhatItCarriedOnThePrimary()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Drop("de");
        await _machine.DistributeAsync();

        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("fi"));
    }

    [Fact]
    public async Task PrimaryChanging_TakesTheRulesThatNameNobodyWithIt()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Control.ClaimDefaultRoute("de", preferred: true);
        await _machine.DistributeAsync();

        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("de"));
        Assert.Empty(await _machine.CarriedAsync("fi"));
    }

    [Fact]
    public async Task ConfigBeingRaised_IsDealtItsShareBeforeItStands()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Named(Addressed, "de"));
        _machine.Raise("fi");

        var role = await _machine.DistributeAsync(raising: "de");

        Assert.Equal([Addressed], await _machine.CarriedAsync("de"));
        Assert.True(role.Split);
        Assert.False(role.Preferred);
    }

    [Fact]
    public async Task RuleToldToBlockWhileItsServerIsDown_ReachesEveryTunnelAsBlocked()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Named(Addressed, "nl", RuleTargetMode.Block));
        _machine.Raise("fi", "de");

        await _machine.DistributeAsync();

        Assert.Equal([Addressed], await _machine.BlockedAsync("fi"));
        Assert.Equal([Addressed], await _machine.BlockedAsync("de"));
        Assert.Empty(await _machine.CarriedAsync("fi"));
    }

    [Fact]
    public async Task Journal_SaysOnceWhereARuleGoes()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");

        await _machine.DistributeAsync();
        await _machine.DistributeAsync();
        await _machine.DistributeAsync();

        Assert.Equal(2, _machine.Journal.Verdicts.Count);
    }

    [Fact]
    public async Task Journal_SaysItAgainOnlyWhenTheRuleChangesSide()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Drop("de");
        await _machine.DistributeAsync();

        Assert.Equal(3, _machine.Journal.Verdicts.Count);
        Assert.Contains(Addressed, _machine.Journal.Verdicts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_WarnsOnceAboutAServerTheLibraryDoesNotHold()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Named(Addressed, "se"));
        _machine.Raise("fi");

        await _machine.DistributeAsync();
        await _machine.DistributeAsync();

        Assert.Single(_machine.Journal.Verdicts);
        Assert.StartsWith("Warning|", _machine.Journal.Verdicts[0], StringComparison.Ordinal);
        Assert.Equal([Addressed], await _machine.CarriedAsync("fi"));
    }

    [Fact]
    public async Task WithSeveralServersOff_EveryTunnelCarriesTheWholeListAndTheJournalSaysNothing()
    {
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");

        await _machine.DistributeAsync();

        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("fi"));
        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("de"));
        Assert.Empty(_machine.Journal.Verdicts);
    }

    [Fact]
    public async Task RuleOutsideTheProxyBucket_KeepsItsBucketWhicheverServerItNames()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(new GeoRule(GeoRuleKind.Cidr, Addressed, RouteRole.Direct, RuleTargetMode.Server, "de"));
        _machine.Raise("fi", "de");

        await _machine.DistributeAsync();

        Assert.Empty(await _machine.CarriedAsync("fi"));
        Assert.Empty(await _machine.CarriedAsync("de"));
        Assert.Empty(_machine.Journal.Verdicts);
    }

    [Fact]
    public async Task SeveralServersSwitchedOff_LeaveTheServerCarryingEverythingUpAndTakeTheRestDown()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de", "nl");
        await _machine.DistributeAsync();
        _machine.Control.ClaimDefaultRoute("de", preferred: true);

        await _machine.ModeOffAsync();
        var settle = await _machine.SwitchModeAsync();

        Assert.Equal("de", settle.Keeper);
        Assert.Equal(["fi", "nl"], settle.Dropped);
        Assert.True(_machine.Control.IsRunning("de"));
        Assert.False(_machine.Control.IsRunning("fi"));
        Assert.False(_machine.Control.IsRunning("nl"));
    }

    [Fact]
    public async Task ServerLeftOnItsOwn_CarriesTheWholeListAgain()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();
        Assert.Equal([Unaddressed], await _machine.CarriedAsync("fi"));

        await _machine.ModeOffAsync();
        var settle = await _machine.SwitchModeAsync();

        Assert.Equal("fi", settle.Keeper);
        Assert.Equal([Unaddressed, Addressed], await _machine.CarriedAsync("fi"));
        Assert.False(_machine.Control.IsRunning("de"));
    }

    [Fact]
    public async Task SeveralServersSwitchedOn_RaiseNothingAndDealTheListToWhatIsAlreadyUp()
    {
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        await _machine.ModeOnAsync();
        var settle = await _machine.SwitchModeAsync();

        Assert.Equal("fi", settle.Keeper);
        Assert.Empty(settle.Dropped);
        Assert.True(_machine.Control.IsRunning("fi"));
        Assert.True(_machine.Control.IsRunning("de"));
        Assert.False(_machine.Control.IsRunning("nl"));
        Assert.Equal([Unaddressed], await _machine.CarriedAsync("fi"));
        Assert.Equal([Addressed], await _machine.CarriedAsync("de"));
    }

    [Fact]
    public async Task ModeGoingOff_LeavesThePriorityAndTheCardsSwitchedOffWaitingForIt()
    {
        await _machine.ModeOnAsync();
        await _machine.SwitchOffAsync("nl");
        await _machine.ListAsync(Rule(Unaddressed));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        await _machine.ModeOffAsync();
        await _machine.SwitchModeAsync();

        Assert.Equal(["fi", "de", "nl"], await _machine.OrderAsync());
        Assert.Equal(["nl"], await _machine.SwitchedOffAsync());
    }

    private static GeoRule Rule(string cidr)
    {
        return new GeoRule(GeoRuleKind.Cidr, cidr);
    }

    private static GeoRule Named(string cidr, string server, RuleTargetMode fallbackMode = RuleTargetMode.Auto)
    {
        return new GeoRule(GeoRuleKind.Cidr, cidr, RouteRole.Proxy, RuleTargetMode.Server, server, fallbackMode);
    }
}
