using System.Text.Json;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The warning of a configuration update on the home screen offers the update or skips it till the server of the
/// subscription names its next revision.
/// </summary>
public sealed class ConfigUpdateSkipTests
{
    private const string Subscription = "vpn.example";

    [Fact]
    public void ARevisionNobodySkipped_Waits()
    {
        Assert.True(ConfigUpdateSkips.Waits(string.Empty, Subscription, "r2"));
    }

    [Fact]
    public void TheSkippedRevision_DoesNotWait()
    {
        var skipped = ConfigUpdateSkips.Skip([(Subscription, "r2")]);

        Assert.False(ConfigUpdateSkips.Waits(skipped, Subscription, "r2"));
    }

    [Fact]
    public void TheNextRevision_WaitsAgain()
    {
        var skipped = ConfigUpdateSkips.Skip([(Subscription, "r2")]);

        Assert.True(ConfigUpdateSkips.Waits(skipped, Subscription, "r3"));
    }

    [Fact]
    public void ASkip_LeavesAnotherSubscriptionWaiting()
    {
        var skipped = ConfigUpdateSkips.Skip([(Subscription, "r2")]);

        Assert.True(ConfigUpdateSkips.Waits(skipped, "panel.example", "r2"));
    }

    [Fact]
    public void ASkip_TakesEverySubscriptionNamed()
    {
        var skipped = ConfigUpdateSkips.Skip([(Subscription, "r2"), ("panel.example", "p5"), (Subscription, "r2")]);

        Assert.False(ConfigUpdateSkips.Waits(skipped, Subscription, "r2"));
        Assert.False(ConfigUpdateSkips.Waits(skipped, "panel.example", "p5"));
    }

    [Fact]
    public void ARevisionTheListHasNotNamed_DoesNotWait()
    {
        Assert.False(ConfigUpdateSkips.Waits(string.Empty, Subscription, string.Empty));
    }

    [Fact]
    public void AStaleSubscription_ShowsTheWarning()
    {
        var (window, _) = Window(stale: true, "r2", string.Empty);

        Assert.True(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public void ASubscriptionInStep_ShowsNoWarning()
    {
        var (window, _) = Window(stale: false, "r2", string.Empty);

        Assert.False(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public void TheSkippedRevision_HidesTheWarning()
    {
        var (window, _) = Window(stale: true, "r2", ConfigUpdateSkips.Skip([(Subscription, "r2")]));

        Assert.False(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public async Task TheNextRevision_ShowsTheWarningAgain()
    {
        var (window, agent) = Window(stale: true, "r2", ConfigUpdateSkips.Skip([(Subscription, "r2")]));
        var raised = new List<string?>();
        window.Home.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        agent.Offered = "r3";
        await window.Config.LoadSubscriptionsAsync();

        Assert.Contains(nameof(ConnectionViewModel.ShowConfigUpdate), raised);
        Assert.True(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public async Task ASkippedSubscription_IsReadAgainWithoutSnapshots()
    {
        var clock = new Clock();
        var (window, agent) = Window(stale: true, "r2", ConfigUpdateSkips.Skip([(Subscription, "r2")]), clock);
        agent.Offered = "r3";

        clock.Now += TimeSpan.FromMinutes(5);
        await window.Config.RereadStaleAsync();

        Assert.True(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public async Task ASubscriptionReadLately_IsNotReadAgain()
    {
        var clock = new Clock();
        var (window, agent) = Window(stale: true, "r2", ConfigUpdateSkips.Skip([(Subscription, "r2")]), clock);
        var reads = agent.Reads;
        agent.Offered = "r3";

        clock.Now += TimeSpan.FromMinutes(4);
        await window.Config.RereadStaleAsync();

        Assert.Equal(reads, agent.Reads);
        Assert.False(window.Home.ShowConfigUpdate);
    }

    [Fact]
    public async Task ASubscriptionInStep_IsNotReadByTheClock()
    {
        var clock = new Clock();
        var (window, agent) = Window(stale: false, "r2", string.Empty, clock);
        var reads = agent.Reads;

        clock.Now += TimeSpan.FromMinutes(10);
        await window.Config.RereadStaleAsync();

        Assert.Equal(reads, agent.Reads);
    }

    // A window with one config of the subscription, the revision its server names and the revisions skipped before.
    private static (MainWindowViewModel Window, Lists Agent) Window(bool stale, string offered, string skipped, TimeProvider? clock = null)
    {
        var agent = new Lists { Offered = offered };
        var window = new MainWindowViewModel(agent, new UiPreferences { SkippedConfigUpdates = skipped });
        if (clock is not null)
        {
            window.Config.Clock = clock;
        }

        window.Config.Apply(
        [
            new ConfigEntry("office", "vpn.example:51820", false, ConnectionStatus.Disconnected, [],
                Subscription: Subscription, SubscriptionStale: stale),
        ]);
        return (window, agent);
    }

    // Lists the one subscription with the revision its server names; answers anything else with nothing.
    private sealed class Lists : IAgentConnection
    {
        public string Offered { get; set; } = string.Empty;

        public int Reads { get; private set; }

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

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op != IpcContract.OpListSubscriptions)
            {
                return Task.FromResult(new IpcAck(true, string.Empty));
            }

            Reads++;
            var entry = new SubscriptionEntry(Subscription, "https://vpn.example:51820/sub/x", string.Empty, 0, 0, 0, 0, 0, 0,
                string.Empty, 1, 0, Stale: true, Offered: Offered);

            return Task.FromResult(new IpcAck(true, JsonSerializer.Serialize(new[] { entry }, IpcJson.Options)));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }

    // A clock that moves only when the test moves it.
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
