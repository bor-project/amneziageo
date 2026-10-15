using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The access point keeps its name out of the beacon when asked to: the console sets it before the mode and shows it.
/// </summary>
[Collection("Output")]
public sealed class HotspotHiddenTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public HotspotHiddenTests()
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
    public async Task TheConsole_SetsTheHiddenNameBeforeTheMode()
    {
        var link = new Link(new StatusSnapshot("1.0.0", null, []));

        Assert.Equal(Exit.Ok, await HotspotCommands.RunAsync(link, ["on", "--ssid", "home-ap", "--password", "12345678", "--hidden", "on"]));

        Assert.Equal([SettingKeys.HotspotSsid, SettingKeys.HotspotPassword, SettingKeys.HotspotHidden, SettingKeys.ShareMode], link.Args.Select(args => args[0]));
        Assert.Equal("on", link.Args[2][1]);
    }

    [Fact]
    public async Task TheConsole_RefusesAWordItDoesNotKnow()
    {
        var link = new Link(new StatusSnapshot("1.0.0", null, []));

        Assert.Equal(Exit.Usage, await HotspotCommands.RunAsync(link, ["on", "--hidden", "maybe"]));

        Assert.Empty(link.Args);
    }

    [Fact]
    public async Task TheShow_NamesTheHiddenName()
    {
        var link = new Link(new StatusSnapshot("1.0.0", null, []) { HotspotHidden = true });

        Assert.Equal(Exit.Ok, await HotspotCommands.RunAsync(link, ["show"]));

        Assert.Contains(_console.ToString().Split('\n'), line => line.StartsWith("hidden", StringComparison.Ordinal) && line.TrimEnd().EndsWith("on", StringComparison.Ordinal));
    }

    // Answers the snapshot it was built with and remembers the arguments of every operation sent.
    private sealed class Link(StatusSnapshot snapshot) : IAgentLink
    {
        public List<string[]> Args { get; } = [];

        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = snapshot;

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Args.Add(args);

            return Task.FromResult(new IpcAck(true, string.Empty));
        }
    }
}
