using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A command a group answers for is a command the general help names: the help is where the program is read
/// from, so a new subcommand without a line in it fails here.
/// </summary>
[Collection("Output")]
public sealed class HelpCoverageTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public HelpCoverageTests()
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
    public async Task TheHelp_NamesEveryConfigSubcommand()
    {
        var usage = CliRunner.Usage(new Host());

        foreach (var name in await SubcommandsAsync(args => ConfigCommands.RunAsync(new Link(), args)))
        {
            Assert.Contains($"config {name}", usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheHelp_NamesEveryRoutingSubcommand()
    {
        var usage = CliRunner.Usage(new Host());

        foreach (var name in await SubcommandsAsync(args => RoutingCommands.RunAsync(new Link(), args)))
        {
            Assert.Contains($"routing {name}", usage, StringComparison.Ordinal);
        }
    }

    // The subcommands a group answers for, read off the usage line it prints when called with nothing.
    private async Task<IReadOnlyList<string>> SubcommandsAsync(Func<IReadOnlyList<string>, Task<int>> group)
    {
        Assert.Equal(Exit.Usage, await group([]));

        var line = _console.ToString().Split('\n').First(text => text.StartsWith("usage:", StringComparison.Ordinal));
        var open = line.IndexOf('<', StringComparison.Ordinal);
        var close = line.IndexOf('>', StringComparison.Ordinal);
        Assert.InRange(close, open + 2, line.Length - 1);
        return line[(open + 1)..close].Split('|');
    }

    // Answers an empty snapshot and accepts whatever is sent.
    private sealed class Link : IAgentLink
    {
        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, []);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args) =>
            Task.FromResult(new IpcAck(true, string.Empty));
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
