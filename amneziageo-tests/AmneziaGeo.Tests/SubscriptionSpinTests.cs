using System.Diagnostics;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The refresh of a subscription from its card or from the page of its configuration keeps turning for two seconds
/// at least, however soon the agent answers.
/// </summary>
public sealed class SubscriptionSpinTests
{
    private static readonly TimeSpan Spin = TimeSpan.FromSeconds(1.9);

    [Fact]
    public async Task TheCard_TurnsForTwoSecondsAtLeast()
    {
        var agent = new Quick();
        var catalogue = await CatalogueAsync(agent);
        var row = Assert.Single(catalogue.Configs);
        var watch = Stopwatch.StartNew();

        var run = catalogue.RefreshConfigSubscriptionCommand.ExecuteAsync(row);
        var turning = row.SubscriptionRefreshing;
        await run;

        Assert.True(turning);
        Assert.True(watch.Elapsed >= Spin);
        Assert.False(row.SubscriptionRefreshing);
        Assert.Equal([IpcContract.OpRefreshSubscription], agent.Refreshes);
    }

    [Fact]
    public async Task ThePage_TurnsForTwoSecondsAtLeast()
    {
        var agent = new Quick();
        var catalogue = await CatalogueAsync(agent);
        var item = Assert.Single(catalogue.Subscriptions);
        var watch = Stopwatch.StartNew();

        var run = item.RefreshCommand.ExecuteAsync(null);
        var turning = item.Busy;
        await run;

        Assert.True(turning);
        Assert.True(watch.Elapsed >= Spin);
        Assert.False(item.Busy);
        Assert.Equal([IpcContract.OpRefreshSubscription], agent.Refreshes);
    }

    private static async Task<ConfigViewModel> CatalogueAsync(Quick agent)
    {
        var host = new MainWindowViewModel(agent, new UiPreferences());
        var catalogue = new ConfigViewModel(host, agent);
        catalogue.Apply([new ConfigEntry("ours", "vpn.example:51820", false, ConnectionStatus.Disconnected, [], Subscription: "vpn.example")]);
        await catalogue.LoadSubscriptionsAsync();

        return catalogue;
    }

    // Answers at once: the list of subscriptions holds one, every other command is done.
    private sealed class Quick : IAgentConnection
    {
        public List<string> Refreshes { get; } = [];

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
            if (command.Op == IpcContract.OpRefreshSubscription)
            {
                Refreshes.Add(command.Op);
            }

            return Task.FromResult(command.Op == IpcContract.OpListSubscriptions
                ? new IpcAck(true, """[{"name":"vpn.example","url":"https://vpn.example:51820/sub/abc","checkedAt":1790088166}]""")
                : new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
