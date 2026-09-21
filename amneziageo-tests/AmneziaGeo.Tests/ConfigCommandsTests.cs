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
    public async Task ConfigWebsocket_TurnsDownAPortTheClientCannotDial()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [])]);

        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--port", "70000"]));
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task ConfigWebsocket_TurnsDownAnAddressTheClientWillNotDial()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [])]);

        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--host", "https://my.example/p"]));
        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--host", "my.example:99999"]));
        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--host", "wss://my.example/p?x=1"]));
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task ConfigWebsocket_TakesAFrontTheClientDials()
    {
        var link = new Link([new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [])]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["websocket", "office", "on", "--host", "wss://front.example:8443/secret", "--port", "8443"]));
        Assert.Equal([IpcContract.OpSetWebSocket], link.Sent);
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
