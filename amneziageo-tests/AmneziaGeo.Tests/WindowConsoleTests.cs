using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The console that runs inside the process of the window, as on Android, answers in the language asked for or in the
/// language of the window, and leaves the language of the window alone.
/// </summary>
[Collection("Output")]
public sealed class WindowConsoleTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public WindowConsoleTests()
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
    public async Task ACommandWithALanguage_LeavesTheWindowInItsOwn()
    {
        var window = Loc.Instance.Culture.TwoLetterISOLanguageName;
        var other = window == "ru" ? "en" : "ru";
        var changes = 0;
        void OnChange() => changes++;

        Loc.Instance.CultureChanged += OnChange;
        try
        {
            var code = await CliRunner.RunAsync(["--lang", other, "down"], new Host(window), CancellationToken.None);

            Assert.Equal(Exit.Ok, code);
        }
        finally
        {
            Loc.Instance.CultureChanged -= OnChange;
        }

        Assert.Equal(0, changes);
        Assert.Equal(window, Loc.Instance.Culture.TwoLetterISOLanguageName);
        Assert.Contains(Loc.GetIn(other, "Status_Disconnected"), _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandWithoutALanguage_AnswersInTheLanguageOfTheWindow()
    {
        var held = Loc.Instance.Culture.TwoLetterISOLanguageName;
        var window = held == "ru" ? "en" : "ru";

        await CliRunner.RunAsync(["down"], new Host(window), CancellationToken.None);

        Assert.Equal(held, Loc.Instance.Culture.TwoLetterISOLanguageName);
        Assert.Contains(Loc.GetIn(window, "Status_Disconnected"), _console.ToString(), StringComparison.Ordinal);
    }

    // An agent that answers every command with a resource key.
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
            Task.FromResult(new IpcAck(true, IpcMessage.Key("Status_Disconnected")));
    }

    // A host inside the window of the language given.
    private sealed class Host(string window) : ICliHost
    {
        /// <inheritdoc/>
        public string ExeName => "amneziageo";

        /// <inheritdoc/>
        public string ExtraUsage => string.Empty;

        /// <inheritdoc/>
        public TextReader? StandardInput => null;

        /// <inheritdoc/>
        public string? WindowLanguage => window;

        /// <inheritdoc/>
        public Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct) =>
            Task.FromResult<IAgentLink?>(new Link());

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
