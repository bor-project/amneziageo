using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window asks the agent every second what the running tunnel loses and how quickly it answers, for as long
/// as it is on the screen and a tunnel is up, and puts the answer on the row of that tunnel.
/// </summary>
public sealed class LinkWatchTests
{
    [Fact]
    public void WithATunnelUpAndTheWindowOnTheScreen_TheLinkIsWatched()
    {
        var host = Window(new Agent(), active: true);

        Assert.True(host.Home.LinkWatched);
    }

    [Fact]
    public void WithNoTunnel_NothingIsWatched()
    {
        var host = Window(new Agent(), active: false);

        Assert.False(host.Home.LinkWatched);
    }

    [Fact]
    public void AWindowThatLeftTheScreen_StopsWatchingUntilItIsBack()
    {
        var host = Window(new Agent(), active: true);

        host.Home.Seen(false);
        Assert.False(host.Home.LinkWatched);

        host.Home.Seen(true);
        Assert.True(host.Home.LinkWatched);
    }

    [Fact]
    public void AWindowTheSystemTookOffTheScreen_StopsWatchingToo()
    {
        var host = Window(new Agent(), active: true);

        host.WindowShown(false);

        Assert.False(host.Home.LinkWatched);
    }

    [Fact]
    public void ATunnelThatWentDown_IsWatchedNoLonger()
    {
        var host = Window(new Agent(), active: true);

        host.Home.Apply(Snapshot(active: false));

        Assert.False(host.Home.LinkWatched);
    }

    [Fact]
    public void TheWatchBegins_WithALookOfItsOwn()
    {
        var agent = new Agent();
        var host = new MainWindowViewModel(agent, new UiPreferences());
        var snapshot = Snapshot(active: true);
        host.Home.SetConnected();
        host.Config.Apply(snapshot);

        host.Home.Apply(snapshot);

        Assert.Contains(IpcContract.OpLinkNow, agent.Sent);
    }

    [Fact]
    public async Task ALook_PutsTheAnswerOnTheRowOfTheTunnel()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        agent.Rows = new LinkNow(7, 43, 1).ToRow("home");

        await host.Home.LookAsync();

        var row = host.Config.Configs.First(config => config.Name == "home");
        Assert.Equal(7, row.LinkLossPercent);
        Assert.Equal(43, row.LinkRttMs);
        Assert.Equal(1, row.LinkLossStreak);
        Assert.Equal([IpcContract.OpLinkNow], agent.Sent);
    }

    [Fact]
    public async Task ALook_LeavesTheOtherRowsAsTheyWere()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        agent.Rows = new LinkNow(7, 43, 1).ToRow("home");

        await host.Home.LookAsync();

        var row = host.Config.Configs.First(config => config.Name == "office");
        Assert.Equal(LinkHealth.LossUnknown, row.LinkLossPercent);
        Assert.Equal(-1, row.LinkRttMs);
    }

    [Fact]
    public async Task TheHomeScreen_ShowsWhatTheLookBrought()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        agent.Rows = new LinkNow(0, 43, 0).ToRow("home");
        var told = new List<string?>();
        host.Home.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        await host.Home.LookAsync();

        Assert.Contains(nameof(ConnectionViewModel.CardProbeText), told);
        Assert.Contains(nameof(ConnectionViewModel.CardLossText), told);
    }

    [Fact]
    public async Task ALookThatBroughtNothingNew_TellsTheScreenNothing()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        agent.Rows = new LinkNow(0, 43, 0).ToRow("home");
        await host.Home.LookAsync();
        var told = new List<string?>();
        host.Home.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        await host.Home.LookAsync();

        Assert.Empty(told);
    }

    [Fact]
    public async Task AnAgentThatDoesNotKnowTheQuestion_IsNotAskedAgain()
    {
        var agent = new Agent { Refuses = true };
        var host = Window(agent, active: true);

        await host.Home.LookAsync();

        Assert.False(host.Home.LinkWatched);
    }

    [Fact]
    public async Task WhileAnOutageIsOn_TheCardSaysTheServerDoesNotAnswer()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        var row = Measured(host);
        agent.Rows = new LinkNow(100, -1, 3).ToRow("home");

        await host.Home.LookAsync();

        Assert.Equal(Loc.Instance.Get("Main_ProbeNoAnswer"), row.ProbeText);
    }

    [Fact]
    public async Task OnceTheLinkAnswersAgain_TheCardShowsItsTime()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        var row = Measured(host);
        agent.Rows = new LinkNow(100, -1, 3).ToRow("home");
        await host.Home.LookAsync();
        agent.Rows = new LinkNow(75, 40, 3).ToRow("home");

        await host.Home.LookAsync();

        Assert.Equal(Loc.Instance.Get("Main_ProbeMilliseconds", 40), row.ProbeText);
    }

    [Fact]
    public async Task ATunnelNothingMeasures_KeepsTheTimeOfItsServer()
    {
        var agent = new Agent();
        var host = Window(agent, active: true);
        var row = Measured(host);
        agent.Rows = LinkNow.Unknown.ToRow("home");

        await host.Home.LookAsync();

        Assert.Equal(Loc.Instance.Get("Main_ProbeMilliseconds", 43), row.ProbeText);
    }

    // The row of the tunnel, its server measured from beside the tunnel earlier.
    private static ConfigItemViewModel Measured(MainWindowViewModel host)
    {
        var row = host.Config.Configs.First(config => config.Name == "home");
        row.ProbeState = ProbeOutcome.Alive;
        row.ProbeMilliseconds = 43;
        return row;
    }

    // The window of an agent with two configurations, the tunnel standing on "home" or on nothing.
    private static MainWindowViewModel Window(Agent agent, bool active)
    {
        var host = new MainWindowViewModel(agent, new UiPreferences());
        var snapshot = Snapshot(active);
        host.Home.SetConnected();
        host.Config.Apply(snapshot);
        host.Home.Apply(snapshot);
        agent.Sent.Clear();
        return host;
    }

    private static StatusSnapshot Snapshot(bool active)
    {
        var configs = new[] { "home", "office" }
            .Select(name => new ConfigEntry(
                name,
                name + ".example:51820",
                false,
                active && name == "home" ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                [],
                HandshakeAgeSeconds: active && name == "home" ? 0 : -1))
            .ToList();

        return new StatusSnapshot(
            "1",
            active ? "home" : null,
            configs,
            Active: active,
            BoundStatus: active ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
            SelectedTarget: "home");
    }

    // Answers the question about the link with the rows it was given, and every other command with an ack.
    private sealed class Agent : IAgentConnection
    {
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

        /// <summary>
        /// The operations sent.
        /// </summary>
        public List<string> Sent { get; } = [];

        /// <summary>
        /// The rows the question about the link is answered with.
        /// </summary>
        public string Rows { get; set; } = string.Empty;

        /// <summary>
        /// Whether the agent answers that it does not know the question.
        /// </summary>
        public bool Refuses { get; init; }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Sent.Add(command.Op);
            return Task.FromResult(command.Op == IpcContract.OpLinkNow
                ? new IpcAck(!Refuses, Refuses ? "unknown command" : Rows)
                : new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
