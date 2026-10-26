using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A routing list holds a token once: adding it in another role moves it to that role, and an edit that changes
/// nothing is not saved, so the running tunnel is not handed the same rules again. A list is turned to carry
/// everything but the picked and back, and keeps its mode through an edit.
/// </summary>
[Collection("Output")]
public sealed class RoutingCommandsTests : IDisposable
{
    // Traffic settings of a list that tunnels only what it names, and of one made to carry everything.
    private const string Split = """{"exclusions":"","allUdp":false,"mode":"split","useGlobalProxy":false}""";
    private const string Full = """{"exclusions":"","allUdp":false,"mode":"full","useGlobalProxy":true}""";

    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingCommandsTests()
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
    public async Task AddingAListedTokenInAnotherRole_MovesItToThatRole()
    {
        var link = new Link("proxy|geosite:youtube\ndirect|domain:bing.com");

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["add", "main", "block|domain:bing.com"]));

        Assert.Equal(new[] { IpcContract.OpSaveRoutingList, "6", "main", "proxy|geosite:youtube", "block|domain:bing.com" }, link.Sent[^1]);
    }

    [Fact]
    public async Task AnEditThatChangesNothing_IsNotSaved()
    {
        var link = new Link("proxy|geosite:youtube\ndirect|domain:bing.com");

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["add", "main", "direct|domain:bing.com"]));

        Assert.DoesNotContain(link.Sent, sent => sent[0] == IpcContract.OpSaveRoutingList);
    }

    [Fact]
    public async Task AList_IsTurnedToCarryEverythingButThePicked()
    {
        var link = new Link(string.Empty);

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["configure", "main", "--global-proxy", "on"]));

        Assert.Equal(new[] { IpcContract.OpSetRoutingSettings, "6", string.Empty, "off", "full", "on" }, link.Sent[^1]);
    }

    [Fact]
    public async Task AListMadeToCarryEverything_KeepsItsModeThroughAnEdit()
    {
        var link = new Link(string.Empty, Full);

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["configure", "main", "--all-udp", "on"]));

        Assert.Equal(new[] { IpcContract.OpSetRoutingSettings, "6", string.Empty, "on", "full", "on" }, link.Sent[^1]);
    }

    [Fact]
    public async Task AListMadeToCarryEverything_IsTurnedOff()
    {
        var link = new Link(string.Empty, Full);

        Assert.Equal(Exit.Ok, await RoutingCommands.RunAsync(link, ["configure", "main", "--global-proxy", "off"]));

        Assert.Equal(new[] { IpcContract.OpSetRoutingSettings, "6", string.Empty, "off", "split", "off" }, link.Sent[^1]);
    }

    // Answers one snapshot holding the list "main", hands back its rules and settings and writes down what was sent.
    private sealed class Link(string rules, string settings = Split) : IAgentLink
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
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, [], [new RoutingListEntry(6, "main", 2, 0, 0)]);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add([op, .. args]);
            return Task.FromResult(op switch
            {
                IpcContract.OpGetRoutingList => new IpcAck(true, rules),
                IpcContract.OpGetRoutingSettings => new IpcAck(true, settings),
                _ => new IpcAck(true, string.Empty),
            });
        }
    }
}
