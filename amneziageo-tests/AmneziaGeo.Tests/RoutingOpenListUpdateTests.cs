using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An open routing list whose server holds a newer version offers it at the title of the section: taking it sends the
/// command of the card and reads the rules and the settings of the list again. A name the list got elsewhere reaches
/// the open editor.
/// </summary>
public sealed class RoutingOpenListUpdateTests
{
    [Fact]
    public async Task AnOpenListRenamedElsewhere_ShowsTheNewName()
    {
        var routing = Routing(new Agent());
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0));

        routing.Apply(Snapshot(new RoutingListEntry(7, "office", 1, 0, 0)));

        Assert.Equal("office", routing.RoutingEditor!.Name);
        Assert.False(routing.RoutingEditor.IsDirty);
    }

    [Fact]
    public async Task ANameBeingEdited_IsKept()
    {
        var routing = Routing(new Agent());
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0));
        routing.RoutingEditor!.Name = "mine";

        routing.Apply(Snapshot(new RoutingListEntry(7, "office", 1, 0, 0)));

        Assert.Equal("mine", routing.RoutingEditor.Name);
        Assert.True(routing.RoutingEditor.IsDirty);
    }

    [Fact]
    public async Task AnOpenListWithANewerVersion_OffersItAtTheTitle()
    {
        var routing = Routing(new Agent());

        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0, Source: "vpn.example-awg1-office", HasUpdate: true));

        Assert.True(routing.ShowListUpdate);
    }

    [Fact]
    public async Task AnOpenListThatHoldsTheNewest_OffersNothingAtTheTitle()
    {
        var routing = Routing(new Agent());

        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0, Source: "vpn.example-awg1-office"));

        Assert.False(routing.ShowListUpdate);
    }

    [Fact]
    public void TheCatalogue_OffersNoUpdateAtTheTitle()
    {
        var routing = Routing(new Agent());

        routing.Apply(Snapshot(new RoutingListEntry(7, "home", 1, 0, 0, HasUpdate: true)));

        Assert.False(routing.ShowListUpdate);
    }

    [Fact]
    public async Task ANewerVersionThatArrives_ShowsAtTheTitleOfTheOpenList()
    {
        var routing = Routing(new Agent());
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0));
        var raised = new List<string?>();
        routing.PropertyChanged += (_, e) =>
        {
            lock (raised)
            {
                raised.Add(e.PropertyName);
            }
        };

        routing.Apply(Snapshot(new RoutingListEntry(7, "home", 1, 0, 0, HasUpdate: true)));

        Assert.True(routing.ShowListUpdate);
        Assert.Contains(nameof(RoutingViewModel.ShowListUpdate), raised);
    }

    [Fact]
    public async Task AListStillLoading_OffersNoUpdateAtTheTitle()
    {
        var gate = new TaskCompletionSource();
        var routing = Routing(new Agent(gate.Task));
        routing.Apply(Snapshot(new RoutingListEntry(7, "home", 1, 0, 0, HasUpdate: true)));

        routing.OpenCardCommand.Execute(routing.RoutingLists.Single());

        Assert.True(routing.SectionLoading);
        Assert.False(routing.ShowListUpdate);

        gate.SetResult();
        await Settled(routing);

        Assert.True(routing.ShowListUpdate);
    }

    [Fact]
    public async Task TakingTheUpdate_SendsTheCommandOfTheCard()
    {
        var agent = new Agent();
        var routing = Routing(agent);
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0, HasUpdate: true));

        await routing.UpdateOpenListCommand.ExecuteAsync(null);

        var sent = Assert.Single(agent.Sent(IpcContract.OpUpdateRoutingList));
        Assert.Equal(["7"], sent.Args);
    }

    [Fact]
    public async Task TakingTheUpdate_ReadsTheRulesAndTheSettingsOfTheListAgain()
    {
        var routing = Routing(new Agent());
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0, HasUpdate: true));
        routing.RoutingEditor!.ProxyRules.Add("geosite:mine");

        await routing.UpdateOpenListCommand.ExecuteAsync(null);

        Assert.Equal(["geosite:openai", "geosite:youtube"], routing.RoutingEditor.ProxyRules);
        Assert.True(routing.RoutingSettings!.AllUdp);
        Assert.False(routing.RoutingEditor.IsDirty);
        Assert.False(routing.SectionLoading);
    }

    [Fact]
    public async Task AListThatHoldsTheNewest_SendsNothing()
    {
        var agent = new Agent();
        var routing = Routing(agent);
        await OpenAsync(routing, new RoutingListEntry(7, "home", 1, 0, 0));

        await routing.UpdateOpenListCommand.ExecuteAsync(null);

        Assert.Empty(agent.Sent(IpcContract.OpUpdateRoutingList));
    }

    private static RoutingViewModel Routing(Agent agent) => new MainWindowViewModel(agent, new UiPreferences()).Routing;

    private static StatusSnapshot Snapshot(params RoutingListEntry[] lists) => new("1.0.0", null, [], lists);

    // Opens the list the agent reports and waits out its load.
    private static async Task OpenAsync(RoutingViewModel routing, RoutingListEntry list)
    {
        routing.Apply(Snapshot(list));
        routing.OpenCardCommand.Execute(routing.RoutingLists.Single());
        await Settled(routing);
    }

    private static async Task Settled(RoutingViewModel routing)
    {
        for (var i = 0; i < 200 && routing.SectionLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    // Holds a list of one rule whose server version carries two rules and all UDP, and remembers what was sent.
    private sealed class Agent(Task? gate = null) : IAgentConnection
    {
        private readonly List<IpcCommand> _sent = [];

        private bool _updated;

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

        public IReadOnlyList<IpcCommand> Sent(string op)
        {
            lock (_sent)
            {
                return [.. _sent.Where(command => command.Op == op)];
            }
        }

        public void Start()
        {
        }

        public async Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            lock (_sent)
            {
                _sent.Add(command);
            }

            if (gate is not null)
            {
                await gate;
            }

            return Answer(command.Op);
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }

        private IpcAck Answer(string op)
        {
            lock (_sent)
            {
                if (op == IpcContract.OpUpdateRoutingList)
                {
                    _updated = true;
                }

                return op switch
                {
                    IpcContract.OpGetRoutingList => new IpcAck(true, _updated ? "proxy|geosite:openai\nproxy|geosite:youtube" : "proxy|geosite:youtube"),
                    IpcContract.OpGetRoutingSettings => new IpcAck(true, _updated ? "{\"allUdp\":true}" : "{\"allUdp\":false}"),
                    _ => new IpcAck(true, string.Empty),
                };
            }
        }
    }
}
