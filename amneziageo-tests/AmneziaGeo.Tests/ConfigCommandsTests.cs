using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Access from the tunnel is set by "config inbound", so the help names the command and the table of
/// configurations shows the scope each one stands at.
/// </summary>
[Collection("Output")]
public sealed class ConfigCommandsTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigCommandsTests()
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
    public void TheHelp_NamesTheInboundCommand()
    {
        var usage = CliRunner.Usage(new Host());

        Assert.Contains("config inbound <name> off|host|network", usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTable_ShowsTheInboundScopeOfEveryConfiguration()
    {
        var link = new Link([
            new ConfigEntry("shut", "10.9.1.1:51821", false, "idle", []),
            new ConfigEntry("server", "10.9.1.1:51821", false, "idle", [], AllowInbound: true),
            new ConfigEntry("whole", "10.9.1.1:51821", false, "idle", [], AllowInbound: true, InboundNetwork: true),
        ]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["list"]));

        var lines = _console.ToString().Split('\n');
        Assert.Equal("INBOUND", Cells(lines[0])[5]);
        Assert.Equal("off", Cells(lines[1])[5]);
        Assert.Equal("host", Cells(lines[2])[5]);
        Assert.Equal("network", Cells(lines[3])[5]);
    }

    [Fact]
    public void TheHelp_NamesTheRoutingCommand()
    {
        var usage = CliRunner.Usage(new Host());

        Assert.Contains("config routing <name> on|off", usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTable_ShowsTheRoutingOfEveryConfiguration()
    {
        var link = new Link([
            new ConfigEntry("taken", "10.9.1.1:51821", false, "idle", []),
            new ConfigEntry("kept", "10.9.1.1:51821", false, "idle", [], UseRouting: false),
            new ConfigEntry("banned", "10.9.1.1:51821", false, "idle", [], RoutingLocked: true),
        ]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["list"]));

        var lines = _console.ToString().Split('\n');
        Assert.Equal("ROUTING", Cells(lines[0])[6]);
        Assert.Equal("on", Cells(lines[1])[6]);
        Assert.Equal("off", Cells(lines[2])[6]);
        Assert.Equal("locked", Cells(lines[3])[6]);
    }

    [Fact]
    public async Task ConfigRouting_ResendsTheStoredTransportWithTheSwitch()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [], UseIpv6: true, AllowInbound: true)]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["routing", "office", "off"]));
        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["routing", "office", "maybe"]));
        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["routing", "home", "on"]));

        Assert.Equal([IpcContract.OpSetWebSocket], link.Sent);
        Assert.Equal(9, link.Args[0].Length);
        Assert.Equal("on", link.Args[0][3]);
        Assert.Equal("on", link.Args[0][6]);
        Assert.Equal("off", link.Args[0][8]);
    }

    [Fact]
    public async Task ConfigWebsocket_TakesNoAddressOfItsOwn()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [])]);

        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--port", "8443"]));
        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--host", "front.example"]));
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task ConfigWebsocket_SendsTheSwitchWithTheStoredTransport()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [], UseIpv6: true, Mtu: 1300)]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["websocket", "office", "on"]));
        Assert.Equal([IpcContract.OpSetWebSocket], link.Sent);
        Assert.Equal(["office", "on", "1300", "on"], link.Args[0].Take(4));
    }

    [Fact]
    public async Task TheTable_ShowsTheFrontTheServerOffers()
    {
        var link = new Link([
            new ConfigEntry("offered", "10.9.1.1:51821", false, "idle", [], WebSocket: true, WebSocketFront: "10.9.1.1:8446"),
            new ConfigEntry("unoffered", "10.9.1.1:51821", false, "idle", [], WebSocket: true),
            new ConfigEntry("plain", "10.9.1.1:51821", false, "idle", []),
        ]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["list"]));

        var lines = _console.ToString().Split('\n');
        Assert.Equal("WEBSOCKET", Cells(lines[0])[3]);
        Assert.Equal("10.9.1.1:8446", Cells(lines[1])[3]);
        Assert.Equal("no front", Cells(lines[2])[3]);
        Assert.Equal("-", Cells(lines[3])[3]);
    }

    // Splits one printed row into its cells, which stand at least two spaces apart.
    private static string[] Cells(string line) =>
        line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Answers the snapshot it was built with, remembers what was sent and accepts it.
    private sealed class Link(IReadOnlyList<ConfigEntry> configs) : IAgentLink
    {
        /// <summary>
        /// The operations the command sent.
        /// </summary>
        public List<string> Sent { get; } = [];

        /// <summary>
        /// The arguments of each operation sent.
        /// </summary>
        public List<string[]> Args { get; } = [];

        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, configs);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add(op);
            Args.Add(args);

            return Task.FromResult(new IpcAck(true, string.Empty));
        }
    }

    // The least a host has to answer for the help text.
    private sealed class Host : ICliHost
    {
        /// <inheritdoc/>
        public string ExeName => "amneziageo";

        /// <inheritdoc/>
        public string ExtraUsage => string.Empty;

        /// <inheritdoc/>
        public TextReader? StandardInput => null;

        /// <inheritdoc/>
        public Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct) =>
            Task.FromResult<IAgentLink?>(null);

        /// <inheritdoc/>
        public string UnreachableHint() => string.Empty;

        /// <inheritdoc/>
        public Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct) => null;

        /// <inheritdoc/>
        public Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct) => null;

        /// <inheritdoc/>
        public IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot) => [];
    }
}
