using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The log commands take the resolver log as a table of its own, and the help names it.
/// </summary>
[Collection("Output")]
public sealed class LogCommandsTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public LogCommandsTests()
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
    public async Task TheResolverLog_IsATableOfTheLogCommands()
    {
        var link = new Link();

        var exit = await DiagCommands.RunAsync(link, new Host(), "log", ["clear", "--table", "dns"], CancellationToken.None);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal([IpcContract.OpClearLog], link.Sent);
        Assert.Equal(["dns"], Assert.Single(link.Args));
    }

    [Fact]
    public async Task ATableNobodyKeeps_IsRefused()
    {
        var link = new Link();

        var exit = await DiagCommands.RunAsync(link, new Host(), "log", ["clear", "--table", "names"], CancellationToken.None);

        Assert.Equal(Exit.Usage, exit);
        Assert.Empty(link.Sent);
    }

    [Fact]
    public void TheHelp_NamesTheResolverLog()
    {
        var usage = CliRunner.Usage(new Host());

        Assert.Contains("log tail [--table ageo|dns|routes|checks]", usage, StringComparison.Ordinal);
        Assert.Contains("log clear [--table ageo|dns|routes|checks]", usage, StringComparison.Ordinal);
        Assert.Contains("log export [--table ageo|dns|routes|checks]", usage, StringComparison.Ordinal);
    }

    // Remembers what was sent and accepts it.
    private sealed class Link : IAgentLink
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
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, []);

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
