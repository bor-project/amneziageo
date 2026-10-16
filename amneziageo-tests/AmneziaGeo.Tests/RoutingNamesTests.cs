using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Routing lists may share a name: the console takes a list by its id, or by a name only one list carries, and
/// sends the agent ids.
/// </summary>
[Collection("Output")]
public sealed class RoutingNamesTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingNamesTests()
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
    public async Task ANameTwoListsCarry_IsRefusedWithTheirIds()
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await RoutingCommands.RunAsync(link, ["use", "main"]));

        Assert.Contains("ids 6, 8", _console.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(link.Sent, sent => sent[0] == IpcContract.OpAssignRouting);
    }

    [Theory]
    [InlineData("8", "8")]
    [InlineData("home", "9")]
    public async Task AnIdOrANameOneListCarries_TakesThatList(string key, string id)
    {
        var link = new Link();

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["use", key]));

        Assert.Equal([IpcContract.OpAssignRouting, id], link.Sent[^1]);
    }

    [Fact]
    public async Task TheOrder_GoesToTheAgentAsIds()
    {
        var link = new Link();

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["order", "8", "home", "6"]));

        Assert.Equal([IpcContract.OpReorderRoutingLists, "8", "9", "6"], link.Sent[^1]);
    }

    [Fact]
    public async Task AnExport_NamesTheListsByIdAndRefusesANameTwoListsCarry()
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await BundleCommands.RunAsync(link, ["export", "--list", "main"]));
        Assert.Empty(link.Sent);

        await BundleCommands.RunAsync(link, ["export", "--list", "8", "--list", "home"]);

        var sent = Assert.Single(link.Sent, one => one[0] == IpcContract.OpExportBundle);
        Assert.Contains("\"routingLists\":[\"8\",\"9\"]", sent[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("use main")]
    [InlineData("show main")]
    [InlineData("set main")]
    [InlineData("add main proxy|cidr:10.0.0.0/24")]
    [InlineData("delete-rule main proxy|cidr:10.0.0.0/24")]
    [InlineData("remove main")]
    [InlineData("update main")]
    [InlineData("order home main")]
    [InlineData("settings main")]
    [InlineData("configure main")]
    public async Task ANameTwoListsCarry_IsRefusedInOneLine(string command)
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await RoutingCommands.RunAsync(link, command.Split(' ')));

        Assert.Equal("2 routing lists are called 'main': ids 6, 8; name one by its id", Assert.Single(Lines()));
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task AnExportOfANameTwoListsCarry_IsRefusedInOneLine()
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await BundleCommands.RunAsync(link, ["export", "--list", "main"]));

        Assert.Equal("2 routing lists are called 'main': ids 6, 8; name one by its id", Assert.Single(Lines()));
    }

    [Theory]
    [InlineData("use nowhere")]
    [InlineData("show nowhere")]
    [InlineData("order home nowhere")]
    public async Task ANameNoListCarries_IsRefusedInOneLine(string command)
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await RoutingCommands.RunAsync(link, command.Split(' ')));

        Assert.Equal("routing list 'nowhere' not found", Assert.Single(Lines()));
        Assert.Empty(link.Sent);
    }

    // The lines the console printed.
    private string[] Lines() => _console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Answers one snapshot with two lists called "main" and one called "home", and writes down what was sent.
    private sealed class Link : IAgentLink
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
        public StatusSnapshot Snapshot { get; } = new(
            "1.0.0",
            null,
            [],
            [new RoutingListEntry(6, "main", 0, 0, 0), new RoutingListEntry(8, "main", 0, 0, 0), new RoutingListEntry(9, "home", 0, 0, 0)]);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add([op, .. args]);
            return Task.FromResult(new IpcAck(true, "{}"));
        }
    }
}
