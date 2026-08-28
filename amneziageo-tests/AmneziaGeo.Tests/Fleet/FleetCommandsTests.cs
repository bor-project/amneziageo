using AmneziaGeo.Cli;
using AmneziaGeo.Cli.Fleet;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The console of the mode answers only for the mode: with it off the shared commands stay the shared ones,
/// and with it on the set is printed and moved by name.
/// </summary>
public sealed class FleetCommandsTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public FleetCommandsTests()
    {
        Output.Sink = _console;
        Output.Json = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Output.Sink = new SystemConsoleSink();
        Output.Json = false;
    }

    [Fact]
    public void TheSharedCommandsStayTheSharedOnesWhileTheModeIsOff()
    {
        var snapshot = Snapshot(fleet: null, multiServer: false);

        Assert.False(FleetCommands.Claims(snapshot, ["status"]));
        Assert.False(FleetCommands.Claims(snapshot, ["down", "bravo"]));
        Assert.False(FleetCommands.Claims(snapshot, []));
        Assert.True(FleetCommands.Claims(snapshot, ["fleet", "status"]));
    }

    [Fact]
    public void WithTheSetUpTheStatusAndOneNamedServerBelongToTheMode()
    {
        var snapshot = Snapshot(Set());

        Assert.True(FleetCommands.Claims(snapshot, ["status"]));
        Assert.True(FleetCommands.Claims(snapshot, ["down", "bravo"]));
        Assert.False(FleetCommands.Claims(snapshot, ["down"]));
        Assert.False(FleetCommands.Claims(snapshot, ["up", "bravo"]));
    }

    [Fact]
    public async Task TheModeSaysSoWhileItIsOff()
    {
        var link = new Link(Snapshot(fleet: null, multiServer: false));

        Assert.Equal(Exit.Failed, await FleetCommands.RunAsync(link, ["fleet", "status"]));
        Assert.Empty(link.Sent);
        Assert.Contains("multi-server on", _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSetIsPrintedWithItsRolesAndItsAddressedRules()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Ok, await FleetCommands.RunAsync(link, ["fleet", "status"]));

        var printed = _console.ToString();
        Assert.Contains("alpha", printed, StringComparison.Ordinal);
        Assert.Contains("bravo", printed, StringComparison.Ordinal);
        Assert.Contains("resolver", printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("main", printed, StringComparison.Ordinal);
        Assert.Contains("geosite:youtube", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneServerIsTakenOutOfTheSetByName()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Ok, await FleetCommands.RunAsync(link, ["down", "bravo"]));

        Assert.Equal(new[] { FleetOps.Disconnect, "bravo" }, link.Sent[0]);
    }

    [Fact]
    public async Task ARuleIsAddressedByItsTokenAlone()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Ok, await FleetCommands.RunAsync(link, ["fleet", "target", "main", "proxy|geosite:youtube", "bravo"]));

        Assert.Equal(new[] { FleetOps.SetTarget, "6", "geosite:youtube", "bravo", "auto" }, link.Sent[^1]);
    }

    [Fact]
    public async Task NamingNeitherEndLeavesTheRuleToTheMachine()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Ok, await FleetCommands.RunAsync(link, ["fleet", "target", "6", "geosite:youtube"]));

        Assert.Equal(new[] { FleetOps.SetTarget, "6", "geosite:youtube", "auto", "auto" }, link.Sent[^1]);
    }

    [Fact]
    public async Task ARuleEveryTunnelReadsAlikeRidesNone()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Usage, await FleetCommands.RunAsync(link, ["fleet", "target", "main", "domain:printer.local", "bravo"]));

        Assert.DoesNotContain(link.Sent, sent => sent[0] == FleetOps.SetTarget);
    }

    [Fact]
    public async Task ARuleTheListDoesNotHoldIsRefused()
    {
        var link = new Link(Snapshot(Set()));

        Assert.Equal(Exit.Usage, await FleetCommands.RunAsync(link, ["fleet", "target", "main", "geosite:github", "bravo"]));

        Assert.DoesNotContain(link.Sent, sent => sent[0] == FleetOps.SetTarget);
    }

    private static FleetSnapshot Set()
    {
        return new FleetSnapshot(
            [
                new FleetEntry("alpha", TunnelRoles.Primary, true, true, true),
                new FleetEntry("bravo", TunnelRoles.Reserve, true),
            ],
            "alpha",
            "alpha",
            new Dictionary<string, string>(StringComparer.Ordinal) { [FleetTargets.Key(6, "geosite:youtube")] = "bravo,auto" });
    }

    private static StatusSnapshot Snapshot(FleetSnapshot? fleet, bool multiServer = true)
    {
        return new StatusSnapshot(
            "1.0.0",
            null,
            [],
            [new RoutingListEntry(6, "main", 2, 0, 0)],
            MultiServer: multiServer,
            Fleet: fleet);
    }

    // Answers one snapshot, hands back the rules of the list and writes down what was sent.
    private sealed class Link(StatusSnapshot snapshot) : IAgentLink
    {
        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <summary>
        /// Every command sent, the operation first.
        /// </summary>
        public List<string[]> Sent { get; } = [];

        /// <inheritdoc/>
        public StatusSnapshot Snapshot => snapshot;

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add([op, .. args]);
            return Task.FromResult(op == IpcContract.OpGetRoutingList
                ? new IpcAck(true, "proxy|geosite:youtube\ndirect|domain:printer.local")
                : new IpcAck(true, string.Empty));
        }
    }
}
