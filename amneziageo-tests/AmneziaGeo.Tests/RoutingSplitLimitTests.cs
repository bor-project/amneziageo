using System.Collections.Concurrent;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The threshold of the route table is a field of the advanced screen of a list wherever the agent reports one
/// (Android and TV): it takes the value of the agent, an entry outside 1000..100000 is marked and never sent, an entry
/// inside it leaves as a setting of the agent, and the card of the screen names it in its subtitle.
/// </summary>
public sealed class RoutingSplitLimitTests
{
    [Fact]
    public void TheField_StaysHiddenWhereTheAgentReportsNoThreshold()
    {
        var settings = new RoutingSettingsViewModel(new Recorder(), 7);

        settings.ApplySplitRouteLimit(0);

        Assert.False(settings.ShowSplitRouteLimit);
        Assert.True(settings.CanCommit());
    }

    [Fact]
    public void TheField_TakesTheThresholdOfTheAgentWithoutAnEdit()
    {
        var settings = new RoutingSettingsViewModel(new Recorder(), 7);

        settings.ApplySplitRouteLimit(15000);

        Assert.True(settings.ShowSplitRouteLimit);
        Assert.Equal("15000", settings.SplitRouteLimit);
        Assert.False(settings.SplitRouteLimitInvalid);
        Assert.False(settings.IsDirty);
    }

    [Fact]
    public void AnEditInProgress_OutlivesTheNextSnapshot()
    {
        var settings = new RoutingSettingsViewModel(new Recorder(), 7);
        settings.ApplySplitRouteLimit(11000);
        settings.SplitRouteLimit = "20000";

        settings.ApplySplitRouteLimit(11000);

        Assert.Equal("20000", settings.SplitRouteLimit);
        Assert.True(settings.IsDirty);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("999")]
    [InlineData("100001")]
    [InlineData("")]
    public async Task AnEntryOutOfRange_IsMarkedAndNeverSent(string text)
    {
        var agent = new Recorder();
        var settings = new RoutingSettingsViewModel(agent, 7);
        await settings.LoadAsync();
        settings.ApplySplitRouteLimit(11000);

        settings.SplitRouteLimit = text;

        Assert.True(settings.SplitRouteLimitInvalid);
        Assert.False(settings.CanCommit());
        Assert.False(await settings.CommitAsync());
        Assert.Empty(agent.Settings);
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("20000")]
    [InlineData("100000")]
    public async Task AnEntryInRange_LeavesAsTheSettingOfTheAgent(string text)
    {
        var agent = new Recorder();
        var settings = new RoutingSettingsViewModel(agent, 7);
        await settings.LoadAsync();
        settings.ApplySplitRouteLimit(11000);

        settings.SplitRouteLimit = text;

        Assert.True(settings.IsDirty);
        Assert.True(await settings.CommitAsync());
        Assert.Equal([$"{SettingKeys.SplitRouteLimit}={text}"], agent.Settings);
    }

    [Fact]
    public async Task AnUntouchedThreshold_IsNotSentAgain()
    {
        var agent = new Recorder();
        var settings = new RoutingSettingsViewModel(agent, 7);
        await settings.LoadAsync();
        settings.ApplySplitRouteLimit(11000);
        settings.AllUdp = true;

        Assert.True(await settings.CommitAsync());

        Assert.Empty(agent.Settings);
    }

    [Fact]
    public void TheCard_NamesTheThresholdWhereTheScreenHoldsIt()
    {
        var routing = Routing(new Recorder());
        routing.BeginAddListCommand.Execute(null);

        routing.Apply(Snapshot(11000));

        Assert.True(routing.ShowAllUdp);
        Assert.True(routing.ShowSplitRouteLimit);
        Assert.False(routing.ShowUdpWithoutLimit);
    }

    [Fact]
    public void TheCard_KeepsItsSubtitleWhereTheAgentTakesNoThreshold()
    {
        var routing = Routing(new Recorder());
        routing.BeginAddListCommand.Execute(null);

        routing.Apply(Snapshot(0));

        Assert.False(routing.ShowSplitRouteLimit);
        Assert.True(routing.ShowUdpWithoutLimit);
    }

    [Fact]
    public void AFullList_HoldsNoThreshold()
    {
        var routing = Routing(new Recorder());
        routing.BeginAddListCommand.Execute(null);
        routing.Apply(Snapshot(11000));

        routing.RoutingSettings!.UseGlobalProxy = true;

        Assert.False(routing.ShowSplitRouteLimit);
        Assert.False(routing.ShowUdpWithoutLimit);
    }

    [Fact]
    public void TheSubtitleWithTheThreshold_IsWritten()
    {
        Assert.NotEqual("Main_AdvancedSubtitleLimit", Loc.Instance.Get("Main_AdvancedSubtitleLimit"));
        Assert.NotEqual("Main_SplitRouteLimitTitle", Loc.Instance.Get("Main_SplitRouteLimitTitle"));
        Assert.NotEqual("Main_SplitRouteLimitHint", Loc.Instance.Get("Main_SplitRouteLimitHint"));
        Assert.NotEqual("Main_SplitRouteLimitUnit", Loc.Instance.Get("Main_SplitRouteLimitUnit"));
    }

    // The window asks no region of its own: the regions are picked.
    private static RoutingViewModel Routing(IAgentConnection agent) =>
        new MainWindowViewModel(agent, new UiPreferences { PresetRegions = "de" }).Routing;

    private static StatusSnapshot Snapshot(int threshold) =>
        new("1", null, [], RoutingLists: [], BoundStatus: ConnectionStatus.Connected, SplitRouteLimit: threshold);

    // Answers every command and keeps the settings of the agent it was sent.
    private sealed class Recorder : IAgentConnection
    {
        private readonly ConcurrentQueue<string> _settings = new();

        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> Settings => [.. _settings];

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op == IpcContract.OpSetSetting)
            {
                _settings.Enqueue(string.Join('=', command.Args));
            }

            var answer = command.Op == IpcContract.OpGetRoutingSettings ? "{}" : string.Empty;
            return Task.FromResult(new IpcAck(true, answer));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
