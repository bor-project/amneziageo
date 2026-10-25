using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The threshold of the route table is a setting of the agent: whole entries between 1000 and 100000, refused outside
/// them before anything is sent, and shown by settings show where the agent holds one.
/// </summary>
[Collection("Output")]
public sealed class SplitRouteLimitTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public SplitRouteLimitTests()
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
    [InlineData("11000", 11_000)]
    [InlineData(" 16000 ", 16_000)]
    [InlineData("1000", 1_000)]
    [InlineData("100000", 100_000)]
    public void TheThreshold_TakesWholeEntriesWithinItsBounds(string text, int limit)
    {
        Assert.True(SettingKeys.TryParseSplitRouteLimit(text, out var parsed));
        Assert.Equal(limit, parsed);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("100001")]
    [InlineData("-5")]
    [InlineData("12k")]
    [InlineData("")]
    public void TheThreshold_RefusesWhatIsNotAWholeNumberWithinItsBounds(string text)
    {
        Assert.False(SettingKeys.TryParseSplitRouteLimit(text, out _));
    }

    [Fact]
    public async Task SettingTheThreshold_SendsItAsAWholeNumber()
    {
        var link = new Link(11_000);

        Assert.Equal(Exit.Ok, await SettingsCommands.RunAsync(link, ["set", SettingKeys.SplitRouteLimit, " 16000 "]));
        Assert.Equal(new[] { IpcContract.OpSetSetting, SettingKeys.SplitRouteLimit, "16000" }, link.Sent[^1]);
    }

    [Fact]
    public async Task AThresholdOutOfBounds_IsRefusedBeforeTheAgent()
    {
        var link = new Link(11_000);

        Assert.Equal(Exit.Usage, await SettingsCommands.RunAsync(link, ["set", SettingKeys.SplitRouteLimit, "500"]));
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task SettingsShow_NamesTheThresholdTheAgentHolds()
    {
        Assert.Equal(Exit.Ok, await SettingsCommands.RunAsync(new Link(11_000), ["show"]));
        Assert.Contains(SettingKeys.SplitRouteLimit, _console.ToString());
        Assert.Contains("11000", _console.ToString());
    }

    [Fact]
    public async Task SettingsShow_LeavesTheThresholdOutWhereTheAgentHoldsNone()
    {
        Assert.Equal(Exit.Ok, await SettingsCommands.RunAsync(new Link(0), ["show"]));
        Assert.DoesNotContain(SettingKeys.SplitRouteLimit, _console.ToString());
    }

    // Answers a snapshot holding the given threshold and writes down what was sent.
    private sealed class Link(int limit) : IAgentLink
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
        public StatusSnapshot Snapshot { get; } = new StatusSnapshot("1.0.0", null, [], []) with { SplitRouteLimit = limit };

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add([op, .. args]);
            return Task.FromResult(new IpcAck(true, string.Empty));
        }
    }
}
