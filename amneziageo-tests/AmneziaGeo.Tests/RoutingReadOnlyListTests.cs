using System.Collections.Concurrent;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A list the server keeps read-only: its card names it and flips nothing, its screen shows it with no way to change or
/// delete it, a version the agent took on its own is read again, and its traffic block is never sent.
/// </summary>
public sealed class RoutingReadOnlyListTests
{
    [Fact]
    public void TheCardOfAReadOnlyList_NamesItBeforeItsModes()
    {
        var card = new RoutingListSummaryViewModel { ReadOnly = true, AllUdp = true };

        Assert.Equal([Loc.Instance.Get("Main_CardTagReadOnly"), Loc.Instance.Get("Main_CardTagUdp")], card.Tags.Select(tag => tag.Text));
        Assert.NotEqual("Main_CardTagReadOnly", card.Tags[0].Text);
        Assert.False(card.Tags[0].On);
        Assert.False(card.Tags[0].Interactive);
        Assert.True(card.Tags[0].Plain);
    }

    [Fact]
    public void TheUdpTagOfAReadOnlyList_ShowsTheModeOfTheServerAndFlipsNothing()
    {
        var card = new RoutingListSummaryViewModel { ReadOnly = true, AllUdp = true };

        var udp = card.Tags[1];

        Assert.True(udp.On);
        Assert.False(udp.Interactive);
        Assert.True(udp.Plain);
    }

    [Fact]
    public void AListTheServerOpens_FlipsItsUdpTagAgain()
    {
        var card = new RoutingListSummaryViewModel { ReadOnly = true };
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.ReadOnly = false;

        Assert.Contains(nameof(RoutingListSummaryViewModel.Tags), raised);
        var udp = Assert.Single(card.Tags);
        Assert.True(udp.Interactive);
        Assert.False(udp.Plain);
    }

    [Fact]
    public async Task AnOpenReadOnlyList_IsNamedAtTheTitleAndOffersNoDelete()
    {
        var routing = Routing(new Recorder());
        routing.Apply(Snapshot(Entry(readOnly: true)));

        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);

        Assert.True(routing.ShowListReadOnly);
        Assert.False(routing.ShowDeleteCard);
        Assert.True(routing.RoutingEditor!.IsReadOnly);
        Assert.True(routing.RoutingSettings!.ReadOnly);
    }

    [Fact]
    public async Task AnOrdinaryOpenList_OffersTheDelete()
    {
        var routing = Routing(new Recorder());
        routing.Apply(Snapshot(Entry(readOnly: false)));

        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);

        Assert.False(routing.ShowListReadOnly);
        Assert.True(routing.ShowDeleteCard);
        Assert.False(routing.RoutingEditor!.IsReadOnly);
        Assert.False(routing.RoutingSettings!.ReadOnly);
    }

    [Fact]
    public async Task ANewVersionOfAnOpenReadOnlyList_IsReadAgain()
    {
        var agent = new Recorder();
        var routing = Routing(agent);
        routing.Apply(Snapshot(Entry(readOnly: true)));
        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);

        routing.Apply(Snapshot(Entry(readOnly: true) with { RuleCount = 3, ProxyRuleCount = 3 }));
        await Settled(routing);

        Assert.Equal(2, agent.Count(IpcContract.OpGetRoutingList));
        Assert.Equal(2, agent.Count(IpcContract.OpGetRoutingSettings));
    }

    [Fact]
    public async Task TwoVersionsInARow_LeaveEveryRuleOnce()
    {
        var agent = new Recorder(slow: true);
        var routing = Routing(agent);
        routing.Apply(Snapshot(Entry(readOnly: true)));
        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);

        routing.Apply(Snapshot(Entry(readOnly: true) with { RuleCount = 2 }));
        routing.Apply(Snapshot(Entry(readOnly: true) with { RuleCount = 3 }));
        await Settled(routing);
        await Task.Delay(300);

        Assert.Equal(["geosite:youtube", "domain:example.com"], routing.RoutingEditor!.ProxyRules);
        Assert.Equal(["geosite:youtube", "domain:example.com"], routing.RoutingEditor.RuleItems.Select(row => row.Token));
    }

    [Fact]
    public async Task ANewVersionWithAnotherRule_ShowsEveryRuleOnce()
    {
        var agent = new Recorder();
        var routing = Routing(agent);
        routing.Apply(Snapshot(Entry(readOnly: true)));
        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);
        agent.Rules = "proxy|geosite:youtube\nproxy|domain:example.com\nproxy|geosite:openai";

        routing.Apply(Snapshot(Entry(readOnly: true) with { RuleCount = 3 }));
        await Settled(routing);

        Assert.Equal(["geosite:youtube", "domain:example.com", "geosite:openai"], routing.RoutingEditor!.ProxyRules);
        Assert.Equal(["geosite:youtube", "domain:example.com", "geosite:openai"], routing.RoutingEditor.RuleItems.Select(row => row.Token));
    }

    [Fact]
    public async Task TheSameVersionOfAnOpenReadOnlyList_IsNotReadAgain()
    {
        var agent = new Recorder();
        var routing = Routing(agent);
        routing.Apply(Snapshot(Entry(readOnly: true)));
        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);

        routing.Apply(Snapshot(Entry(readOnly: true)));
        await Settled(routing);

        Assert.Equal(1, agent.Count(IpcContract.OpGetRoutingList));
    }

    [Fact]
    public async Task AListTheServerCloses_DropsWhatWasTyped()
    {
        var routing = Routing(new Recorder());
        routing.Apply(Snapshot(Entry(readOnly: false)));
        routing.OpenCardCommand.Execute(routing.RoutingLists[0]);
        await Settled(routing);
        routing.RoutingEditor!.Name = "mine";
        Assert.True(routing.IsEditDirty);

        routing.Apply(Snapshot(Entry(readOnly: true)));
        await Settled(routing);

        Assert.Equal("office", routing.RoutingEditor.Name);
        Assert.False(routing.IsEditDirty);
        Assert.True(routing.RoutingEditor.IsReadOnly);
        Assert.False(routing.ShowDeleteCard);
    }

    [Fact]
    public void AnEditorTurnedReadOnly_DropsTheClearAndTheTypedEntry()
    {
        var editor = new RoutingListEditorViewModel(new Recorder(), 7, "office");
        editor.ProxyRules.Add("geosite:youtube");
        editor.RequestClearRulesCommand.Execute(null);
        editor.RuleInput = "you";

        editor.IsReadOnly = true;

        Assert.False(editor.ClearPending);
        Assert.Empty(editor.RuleInput);
    }

    [Fact]
    public async Task TheSettingsOfAReadOnlyList_SendOnlyTheAgentWideOnes()
    {
        var agent = new Recorder();
        var settings = new RoutingSettingsViewModel(agent, 7) { ReadOnly = true };
        await settings.LoadAsync();
        settings.RouteTtl = "120";

        Assert.True(await settings.CommitAsync());

        Assert.Equal(0, agent.Count(IpcContract.OpSetRoutingSettings));
        Assert.Equal(1, agent.Count(IpcContract.OpSetSetting));
    }

    [Fact]
    public async Task TheSettingsOfAnOrdinaryList_SendItsTrafficBlock()
    {
        var agent = new Recorder();
        var settings = new RoutingSettingsViewModel(agent, 7);
        await settings.LoadAsync();
        settings.AllUdp = true;

        Assert.True(await settings.CommitAsync());

        Assert.Equal(1, agent.Count(IpcContract.OpSetRoutingSettings));
    }

    // The window asks no region of its own: the regions are picked and the tunnel is up.
    private static RoutingViewModel Routing(IAgentConnection agent) =>
        new MainWindowViewModel(agent, new UiPreferences { PresetRegions = "de" }).Routing;

    // A list of the server of office-awg1, the way the agent reports it.
    private static RoutingListEntry Entry(bool readOnly) =>
        new(7, "office", 1, 1, 1, ProxyRuleCount: 1, Source: "office-awg1", ReadOnly: readOnly);

    private static StatusSnapshot Snapshot(RoutingListEntry entry) =>
        new("1", null, [], RoutingLists: [entry], BoundStatus: ConnectionStatus.Connected);

    // Waits out the load of an opened list.
    private static async Task Settled(RoutingViewModel routing)
    {
        for (var i = 0; i < 200 && routing.SectionLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    // Answers every command with nothing and keeps what was sent; a slow one answers the list after a pause.
    private sealed class Recorder(bool slow = false) : IAgentConnection
    {
        private readonly ConcurrentQueue<IpcCommand> _sent = new();

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

        public int Count(string op) => _sent.Count(command => command.Op == op);

        public string Rules { get; set; } = "proxy|geosite:youtube\nproxy|domain:example.com";

        public void Start()
        {
        }

        public async Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            _sent.Enqueue(command);
            var answer = command.Op switch
            {
                IpcContract.OpGetRoutingList => Rules,
                IpcContract.OpGetRoutingSettings => "{}",
                _ => string.Empty,
            };
            if (slow && command.Op == IpcContract.OpGetRoutingList)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            return new IpcAck(true, answer);
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
