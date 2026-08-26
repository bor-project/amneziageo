using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Where the console writes, whether it prints JSON and whether it prints at all are static, so the classes that
/// drive it run one at a time rather than over each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    /// <summary>
    /// The collection the console tests belong to.
    /// </summary>
    public const string Name = "console";
}

/// <summary>
/// The console as a test drives it: the command goes in through the front door and what it printed comes back.
/// </summary>
internal static class CliConsole
{
    /// <summary>
    /// Runs one command line against the agent and returns its exit code and everything it printed.
    /// </summary>
    public static async Task<(int Code, string Text)> RunAsync(FakeCliAgent agent, params string[] args)
    {
        var sink = new BufferConsoleSink();
        Output.Sink = sink;
        var code = await CliRunner.RunAsync(args, new FakeCliHost(agent), CancellationToken.None);
        return (code, sink.ToString());
    }

    /// <summary>
    /// Puts the console back the way a test found it: where it writes and how is static.
    /// </summary>
    public static void Restore()
    {
        Output.Sink = new SystemConsoleSink();
        Output.Json = false;
        Output.Quiet = false;
    }
}

/// <summary>
/// The agent as the console sees it: the snapshot it reports, the rules one list holds, and whatever else it is
/// told to answer with. Every operation sent to it is kept.
/// </summary>
internal sealed class FakeCliAgent(StatusSnapshot snapshot, IReadOnlyList<string> rules) : IAgentLink
{
    private readonly List<(string Op, string[] Args)> _sent = [];
    private readonly Dictionary<string, IpcAck> _answers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event Action<StatusSnapshot>? SnapshotReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public StatusSnapshot Snapshot => snapshot;

    /// <summary>
    /// Every operation the command sent, in order.
    /// </summary>
    public IReadOnlyList<(string Op, string[] Args)> Sent => _sent;

    /// <summary>
    /// The rules the command stored, without the list they belong to.
    /// </summary>
    public IReadOnlyList<string> Saved => _sent
        .Where(entry => string.Equals(entry.Op, IpcContract.OpSaveRoutingList, StringComparison.Ordinal))
        .Select(entry => entry.Args.Skip(2).ToArray())
        .LastOrDefault() ?? [];

    /// <summary>
    /// Answers one operation with a payload of its own.
    /// </summary>
    public void Answers(string op, string payload)
    {
        _answers[op] = new IpcAck(true, payload);
    }

    /// <inheritdoc />
    public Task<IpcAck> SendAsync(string op, params string[] args)
    {
        _sent.Add((op, args));
        if (_answers.TryGetValue(op, out var answer))
        {
            return Task.FromResult(answer);
        }

        return Task.FromResult(op switch
        {
            IpcContract.OpGetRoutingList => new IpcAck(true, string.Join('\n', rules)),
            IpcContract.OpSaveRoutingList => new IpcAck(true, "saved"),
            _ => new IpcAck(false, $"nothing answers to {op}"),
        });
    }
}

/// <summary>
/// The platform the console runs on, reduced to the agent behind it.
/// </summary>
internal sealed class FakeCliHost(IAgentLink link) : ICliHost
{
    /// <inheritdoc />
    public string ExeName => "amneziageo";

    /// <inheritdoc />
    public string ExtraUsage => string.Empty;

    /// <inheritdoc />
    public TextReader? StandardInput => null;

    /// <inheritdoc />
    public Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct)
    {
        return Task.FromResult<IAgentLink?>(link);
    }

    /// <inheritdoc />
    public string UnreachableHint()
    {
        return "no agent stands behind a test";
    }

    /// <inheritdoc />
    public Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        return null;
    }

    /// <inheritdoc />
    public Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot)
    {
        return [];
    }
}
