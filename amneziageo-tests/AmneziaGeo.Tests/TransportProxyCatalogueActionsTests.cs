using System.Collections.Concurrent;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TransportProxyCatalogueActionsTests
{
    private static readonly ProxyReference First = new("a5b67339-b508-4c20-a3c0-6457ea303f59", "first");
    private static readonly ProxyReference Second = First with { Id = "second" };

    [Fact]
    public async Task CheckAllUsesTheCurrentDraftDestinationWithoutSelectingOrEnabling()
    {
        var denied = Summary(First with { Id = "denied" }) with { Targets = new(true, [new("other.example", 51820)]) };
        var foreign = Summary(First with { Id = "foreign" }) with { Local = true, OwnerConfig = "other" };
        var agent = new Agent { Summaries = [Summary(First), Summary(Second), denied, foreign] };
        var context = Settings(agent);
        context.SelectFromPicker(Summary(Second), true);
        context.SeedProxyTarget("[Peer]\nEndpoint = draft.example:51823");
        var catalog = Catalogue(agent, context);

        await catalog.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);

        var calls = agent.Commands.Where(one => one.Op == IpcContract.OpProbeTransportProxy).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, one => Assert.Equal(["vpn", "draft.example", "51823"], one.Args.Skip(1)));
        Assert.Equal(Second, context.SelectedProxyChoice?.Reference);
        Assert.False(context.SelectedProxyEnabled);
        Assert.True(context.IsDirty);
        Assert.DoesNotContain(agent.Commands, one => one.Op is IpcContract.OpSelectTransportProxy or IpcContract.OpSetConnection);
    }

    [Fact]
    public async Task PartialChecksKeepTheirResultsAcrossSnapshotsUntilTheDefinitionChanges()
    {
        var summaries = new[] { Summary(First), Summary(Second) };
        var agent = new Agent
        {
            Summaries = summaries,
            Probe = command => Task.FromResult(Reference(command) == First
                ? Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available))
                : new IpcAck(false, "proxy-auth")),
        };
        var catalog = Catalogue(agent, Settings(agent));

        await catalog.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);
        catalog.Apply(Snapshot(summaries));

        Assert.True(catalog.Items.Single(one => one.Reference == First).Available);
        Assert.Equal("proxy-auth", catalog.Items.Single(one => one.Reference == Second).Summary.Fault);
        Assert.All(catalog.Items, one => Assert.NotNull(one.Summary.Checked));
        Assert.All(summaries, one => Assert.Equal(TransportProxyAvailability.Unknown, one.Availability));

        catalog.Apply(Snapshot([summaries[0] with { DefinitionStamp = "changed" }, summaries[1]]));

        Assert.Equal(TransportProxyAvailability.Unknown, catalog.Items.Single(one => one.Reference == First).Summary.Availability);
        Assert.Equal("proxy-auth", catalog.Items.Single(one => one.Reference == Second).Summary.Fault);
    }

    [Fact]
    public async Task CheckAllBoundsParallelismAndIgnoresRepeatedStarts()
    {
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var agent = new Agent
        {
            Summaries = [Summary(First), Summary(Second), Summary(First with { Id = "third" }), Summary(First with { Id = "fourth" })],
            Probe = async _ =>
            {
                var count = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), count));
                try
                {
                    return await pending.Task.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
        };
        var catalog = Catalogue(agent, Settings(agent));
        var check = catalog.CheckAllCommand.ExecuteAsync(null);
        Assert.True(catalog.IsCheckingAll);
        Assert.False(catalog.CanCheckAll);
        Assert.False(catalog.CanRefresh);
        Assert.Equal(2, agent.Commands.Count(one => one.Op == IpcContract.OpProbeTransportProxy));

        await catalog.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.Equal(2, agent.Commands.Count(one => one.Op == IpcContract.OpProbeTransportProxy));
        pending.SetResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));
        await check.ConfigureAwait(false);

        Assert.InRange(peak, 1, 2);
        Assert.Equal(4, agent.Commands.Count(one => one.Op == IpcContract.OpProbeTransportProxy));
        Assert.False(catalog.IsCheckingAll);
        Assert.True(catalog.CanCheckAll);
    }

    [Fact]
    public async Task AChangedDestinationDiscardsPendingAndPreviousChecks()
    {
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent { Summaries = [Summary(First)], Probe = _ => pending.Task };
        var context = Settings(agent);
        var catalog = Catalogue(agent, context);
        var check = catalog.CheckAllCommand.ExecuteAsync(null);

        context.SeedEndpoint("changed.example:51821");
        await check.ConfigureAwait(false);
        pending.SetResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));

        Assert.False(catalog.IsCheckingAll);
        Assert.Equal(TransportProxyAvailability.Unknown, Assert.Single(catalog.Items).Summary.Availability);
        agent.Probe = _ => Task.FromResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));
        await catalog.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.True(Assert.Single(catalog.Items).Available);
        context.SeedEndpoint("another.example:51822");
        Assert.Equal(TransportProxyAvailability.Unknown, Assert.Single(catalog.Items).Summary.Availability);
    }

    [Fact]
    public async Task ASnapshotChangesTheOpenCheckDestinationWithoutDiscardingConfigurationEdits()
    {
        var agent = new Agent { Summaries = [Summary(First), Summary(Second)] };
        var host = new MainWindowViewModel(agent, new UiPreferences(persist: false));
        var entry = new ConfigEntry("vpn", "vpn.example:51820", false, ConnectionStatus.Disconnected, [],
            TransportProxy: First, TransportProxyChosen: true, TransportProxyEnabled: false);
        host.Config.Apply(new StatusSnapshot("1", null, [entry], [], TransportProxies: agent.Summaries));
        host.Config.OpenConfig = "vpn";
        var context = Assert.IsType<ConfigTransportViewModel>(host.Config.ConfigTransport);
        context.SelectFromPicker(Summary(Second), true);
        context.SelectedProxyEnabled = true;
        context.MtuMode = (int)MtuMode.Custom;
        context.Mtu = "1380";
        host.Proxies.OpenForConfiguration(context);
        await host.Proxies.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.All(host.Proxies.Items, one => Assert.True(one.Available));
        var moved = new StatusSnapshot("1", null, [entry with { Endpoint = "changed.example:51821" }], [], TransportProxies: agent.Summaries);

        host.Config.Apply(moved);
        host.Proxies.Apply(moved);

        Assert.Same(context, host.Config.ConfigTransport);
        Assert.Equal(new ProxyDestination("changed.example", 51821), context.ProxyDestination);
        Assert.Equal(Second, context.SelectedProxyChoice?.Reference);
        Assert.True(context.SelectedProxyEnabled);
        Assert.Equal("1380", context.Mtu);
        Assert.True(context.IsDirty);
        Assert.All(host.Proxies.Items, one => Assert.Equal(TransportProxyAvailability.Unknown, one.Summary.Availability));
        agent.Commands.Clear();
        await host.Proxies.CheckAllCommand.ExecuteAsync(null).ConfigureAwait(false);

        var probes = agent.Commands.Where(one => one.Op == IpcContract.OpProbeTransportProxy).ToArray();
        Assert.Equal(2, probes.Length);
        Assert.All(probes, one => Assert.Equal(["vpn", "changed.example", "51821"], one.Args.Skip(1)));
        Assert.DoesNotContain(agent.Commands, one => one.Op is IpcContract.OpSelectTransportProxy or IpcContract.OpSetWebSocket or IpcContract.OpEditConfig);
        context.Revert();
        Assert.Equal(First, context.SelectedProxyChoice?.Reference);
        Assert.False(context.SelectedProxyEnabled);
        Assert.Equal("1420", context.Mtu);
        Assert.False(context.IsDirty);
        Assert.Equal(new ProxyDestination("changed.example", 51821), context.ProxyDestination);
    }

    [Fact]
    public void AStoredEndpointRefreshPreservesTheExplicitDraftDestination()
    {
        var agent = new Agent { Summaries = [Summary(First)] };
        var context = Settings(agent);
        context.SeedProxyTarget("[Peer]\nEndpoint = draft.example:51823");
        context.SelectFromPicker(Summary(First), true);
        context.SelectedProxyEnabled = true;

        context.RefreshProxyEndpoint("changed.example:51821");

        Assert.Equal(new ProxyDestination("draft.example", 51823), context.ProxyDestination);
        Assert.Equal(First, context.SelectedProxyChoice?.Reference);
        Assert.True(context.SelectedProxyEnabled);
        Assert.True(context.IsDirty);
    }

    [Fact]
    public async Task LeavingTheCatalogueCancelsItsChecksAndRejectsLateResultsInAnotherContext()
    {
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent { Summaries = [Summary(First)], Probe = _ => pending.Task };
        var catalog = Catalogue(agent, Settings(agent));
        var check = catalog.CheckAllCommand.ExecuteAsync(null);

        catalog.LeaveSection();
        await check.ConfigureAwait(false);
        catalog.OpenForConfiguration(Settings(agent, "other", "other.example:51821"));
        pending.SetResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));

        Assert.Equal("other", catalog.ConfigurationName);
        Assert.False(catalog.IsCheckingAll);
        Assert.Equal(TransportProxyAvailability.Unknown, Assert.Single(catalog.Items).Summary.Availability);
    }

    [Fact]
    public async Task RefreshAwaitsHelloAndPreservesTheDraftSelectionAndLocalRecords()
    {
        var local = Summary(First with { Id = "local" }) with { Local = true, OwnerConfig = "vpn" };
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent { Summaries = [Summary(First), Summary(Second), local], RefreshReply = pending.Task };
        var context = Settings(agent);
        context.SelectFromPicker(Summary(Second), true);
        var catalog = Catalogue(agent, context);
        var refresh = catalog.RefreshCommand.ExecuteAsync(null);
        Assert.True(catalog.IsRefreshing);
        Assert.False(catalog.CanRefresh);
        await catalog.RefreshCommand.ExecuteAsync(null).ConfigureAwait(false);
        var request = Assert.Single(agent.Commands.Where(one => one.Op == IpcContract.OpAskServers));
        Assert.Equal(["vpn"], request.Args);

        pending.SetResult(Reply<IReadOnlyList<TransportProxySummary>>([Summary(First), Summary(Second), local, Summary(First with { Id = "new" })]));
        await refresh.ConfigureAwait(false);

        Assert.Equal(4, catalog.Items.Count);
        Assert.Contains(catalog.Items, one => one.Reference == local.Reference);
        Assert.Equal(Second, context.SelectedProxyChoice?.Reference);
        Assert.False(context.SelectedProxyEnabled);
        Assert.True(context.IsDirty);
        Assert.DoesNotContain(agent.Commands, one => one.Op is IpcContract.OpRefreshSubscription or IpcContract.OpSelectTransportProxy);
        Assert.False(catalog.IsRefreshing);
        Assert.True(catalog.CanRefresh);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheCatalogueAndTheSelection()
    {
        var agent = new Agent { Summaries = [Summary(First)], RefreshReply = Task.FromResult(new IpcAck(false, "proxy-refresh-failed")) };
        var context = Settings(agent);
        context.SelectFromPicker(Summary(First), true);
        context.SelectedProxyEnabled = true;
        var catalog = Catalogue(agent, context);

        await catalog.RefreshCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.Single(catalog.Items);
        Assert.Equal(First, context.SelectedProxyChoice?.Reference);
        Assert.True(context.SelectedProxyEnabled);
        Assert.NotEmpty(catalog.CatalogueError);
        Assert.DoesNotContain(agent.Commands, one => one.Op is IpcContract.OpRefreshSubscription or IpcContract.OpSelectTransportProxy);
    }

    [Fact]
    public async Task ARefreshReplyCannotPopulateANewConfigurationContext()
    {
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent { Summaries = [Summary(First)], RefreshReply = pending.Task };
        var catalog = Catalogue(agent, Settings(agent));
        var refresh = catalog.RefreshCommand.ExecuteAsync(null);
        catalog.OpenForConfiguration(Settings(agent, "other"));
        await refresh.ConfigureAwait(false);
        pending.SetResult(Reply<IReadOnlyList<TransportProxySummary>>([Summary(Second)]));

        Assert.Equal("other", catalog.ConfigurationName);
        Assert.Equal(First, Assert.Single(catalog.Items).Reference);
        Assert.False(catalog.IsRefreshing);
        Assert.Empty(catalog.CatalogueError);
    }

    [Fact]
    public async Task AnEditorCheckUpdatesItsSavedCardWithoutChoosingIt()
    {
        var agent = new Agent { Summaries = [Summary(First)] };
        var context = Settings(agent);
        var catalog = Catalogue(agent, context);
        await catalog.OpenCardCommand.ExecuteAsync(Assert.Single(catalog.Items)).ConfigureAwait(false);

        await catalog.ProbeCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.True(Assert.Single(catalog.Items).Available);
        Assert.Null(context.SelectedProxyChoice?.Reference);
        Assert.False(context.SelectedProxyEnabled);
        catalog.TryNavigateBack();
        catalog.Apply(Snapshot(agent.Summaries));
        Assert.True(Assert.Single(catalog.Items).Available);
    }

    private static ConfigTransportViewModel Settings(Agent agent, string name = "vpn", string endpoint = "vpn.example:51820")
    {
        var context = new ConfigTransportViewModel(agent, name, false, 1420, false, endpoint: endpoint);
        context.ApplyProxyCatalog(agent.Summaries, null, true, false);
        return context;
    }

    private static TransportProxiesViewModel Catalogue(Agent agent, ConfigTransportViewModel context)
    {
        var host = new MainWindowViewModel(agent, new UiPreferences(persist: false));
        host.Proxies.OpenForConfiguration(context);
        return host.Proxies;
    }

    private static TransportProxySummary Summary(ProxyReference reference) => new(reference, "front", "websocket", "proxy.example", 443,
        reference.Id, false, ["source.example"], DefinitionStamp: reference.Id + "-initial");

    private static StatusSnapshot Snapshot(IReadOnlyList<TransportProxySummary> summaries) => new("1", null,
        [new ConfigEntry("vpn", "vpn.example:51820", false, ConnectionStatus.Disconnected, [])], [], TransportProxies: summaries);

    private static ProxyReference Reference(IpcCommand command) => JsonSerializer.Deserialize<ProxyReference>(command.Args[0], IpcJson.Options)!;
    private static IpcAck Reply<T>(T value) => new(true, JsonSerializer.Serialize(value, IpcJson.Options));

    private sealed class Agent : IAgentConnection
    {
        public ConcurrentQueue<IpcCommand> Commands { get; } = new();
        public IReadOnlyList<TransportProxySummary> Summaries { get; init; } = [];
        public Task<IpcAck>? RefreshReply { get; init; }
        public Func<IpcCommand, Task<IpcAck>> Probe { get; set; } = _ => Task.FromResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Commands.Enqueue(command);
            return command.Op switch
            {
                IpcContract.OpListTransportProxies => Task.FromResult(Reply(Summaries)),
                IpcContract.OpAskServers => RefreshReply ?? Task.FromResult(Reply(Summaries)),
                IpcContract.OpProbeTransportProxy => Probe(command),
                _ => Task.FromResult(new IpcAck(true, "")),
            };
        }
    }
}
