using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An agent that always dials a dropped tunnel again, as the one on Windows does, is not told to turn reconnects on:
/// status and doctor say it always dials and name the longest pause, and survive-reboot gives no advice.
/// </summary>
[Collection("Output")]
public sealed class ReconnectWordsTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public ReconnectWordsTests()
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

    [Theory]
    [InlineData(true, false, "always, pause up to 60s")]
    [InlineData(true, true, "always, every 45s")]
    [InlineData(false, false, "off")]
    [InlineData(false, true, "on, every 45s")]
    public void Status_SaysWhatTheAgentDoesWithADroppedTunnel(bool always, bool periodic, string words)
    {
        StatusCommands.Print(Snapshot(always, periodic));

        Assert.Contains($"auto reconnect  {words}\n", _console.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "ok", "always, pause up to 60s")]
    [InlineData(true, true, "ok", "always, every 45s")]
    [InlineData(false, false, "!!", "off: a dropped tunnel stays down")]
    [InlineData(false, true, "ok", "every 45s")]
    public async Task Doctor_TakesAnAgentThatAlwaysDialsForSound(bool always, bool periodic, string mark, string detail)
    {
        await DiagCommands.RunAsync(new Link(Snapshot(always, periodic)), new Host(), "doctor", [], CancellationToken.None);

        var row = _console.ToString().Split('\n').Single(line => line.Contains("auto reconnect", StringComparison.Ordinal));
        Assert.Equal([mark, "auto reconnect", detail], row.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SurviveReboot_GivesNoAdviceWhereADroppedTunnelComesBack(bool always, bool periodic)
    {
        var exit = await SettingsCommands.RunAsync(new Link(Snapshot(always, periodic)), ["set", "survive-reboot", "on"]);

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("periodic-reconnect-enabled", _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurviveReboot_AsksForReconnectsWhereADroppedTunnelStaysDown()
    {
        await SettingsCommands.RunAsync(new Link(Snapshot(false, false)), ["set", "survive-reboot", "on"]);

        Assert.Contains("turn periodic-reconnect-enabled on as well", _console.ToString(), StringComparison.Ordinal);
    }

    private static StatusSnapshot Snapshot(bool always, bool periodic) =>
        new("1.0.0", null, [], PeriodicReconnect: periodic, PeriodicReconnectIntervalSeconds: 45, ReconnectAlways: always);

    // Answers the snapshot it was built with and accepts whatever is sent.
    private sealed class Link(StatusSnapshot snapshot) : IAgentLink
    {
        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = snapshot;

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args) => Task.FromResult(new IpcAck(true, string.Empty));
    }

    // A host that adds no checks of its own.
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
