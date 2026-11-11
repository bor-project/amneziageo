using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TransportProxyPresentationTests
{
    private static readonly ProxyReference First = new("a5b67339-b508-4c20-a3c0-6457ea303f59", "first");
    private static readonly ProxyReference Second = new("a5b67339-b508-4c20-a3c0-6457ea303f59", "second");

    [Fact]
    public void GetIconsFollowSavedAndRunningMethodsIndependently()
    {
        var editor = new ConfigTransportViewModel(new Agent(), "vpn", false, 1420, false);
        var notices = new List<string?>();
        editor.PropertyChanged += (_, change) => notices.Add(change.PropertyName);
        var get = Summary(First) with { Transport = "http", Http = new OfferedProxyHttp(2, "GET", "header") };
        editor.ApplyProxyCatalog([get], First, true);
        Assert.True(editor.SelectedProxyIsHttpGet);
        Assert.True(editor.SelectedProxyChoice!.IsHttpGet);
        Assert.Contains(nameof(editor.SelectedProxyIsHttpGet), notices);
        editor.ApplyRunningProxy(new(First, "udp.example", 51820, Front:
            new("front", "http", "cdn.example", 80, "relay", false, "server", get.Http, UseCertificate: false)), true);
        Assert.True(editor.RunningProxyIsHttpGet);
        Assert.Contains(nameof(editor.RunningProxyIsHttpGet), notices);

        notices.Clear();
        editor.ApplyProxyCatalog([get with { Http = new OfferedProxyHttp(2, "POST", "body") }], First, true);
        Assert.False(editor.SelectedProxyIsHttpGet);
        Assert.False(editor.SelectedProxyChoice!.IsHttpGet);
        Assert.True(editor.RunningProxyIsHttpGet);
        Assert.Contains(nameof(editor.SelectedProxyIsHttpGet), notices);
        editor.ApplyRunningProxy(new(null, "udp.example", 51820), true);
        Assert.False(editor.RunningProxyIsHttpGet);
        Assert.Contains(nameof(editor.RunningProxyIsHttpGet), notices);
    }

    [Fact]
    public void EqualNamesRetainDifferentSavedAndRunningFronts()
    {
        var current = Summary(First) with { Host = "new.example", Transport = "websocket" };
        var actual = new RunningTransportProxy(First, "udp.example", 51820, Front:
            new("Same", "xhttp", "old.example", 80, "old", false, "Moscow"));
        var row = new ConfigItemViewModel();
        row.ApplyTransportProxy(Entry(Second, actual), [current, Summary(Second) with { Sources = ["Riga"] }]);

        Assert.Contains("WebSocket", row.SelectedProxyText, StringComparison.Ordinal);
        Assert.Contains("Riga", row.SelectedProxyText, StringComparison.Ordinal);
        Assert.Contains("XHTTP", row.ActualProxyText, StringComparison.Ordinal);
        Assert.Contains("old.example:80/old", row.ActualProxyText, StringComparison.Ordinal);
        Assert.Contains("udp.example:51820", row.ActualProxyText, StringComparison.Ordinal);
        Assert.DoesNotContain("new.example", row.ActualProxyText, StringComparison.Ordinal);
    }

    [Fact]
    public void OldAgentUnknownTransportRemainsDistinctFromExplicitUdp()
    {
        var unknown = TransportProxyPresentation.Running([Summary(First)], null);
        var direct = TransportProxyPresentation.Running([Summary(First)], new(null, "::1", 51820));
        var oldProxy = TransportProxyPresentation.Running([Summary(First)], new(First, "udp.example", 51820));

        Assert.Equal(Loc.Instance.Get("Proxies_ActualUnknown"), unknown);
        Assert.Equal("UDP · [::1]:51820", direct);
        Assert.Contains("udp.example:51820", oldProxy, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", oldProxy, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyVerifiedCatalogAliasesIdentifyTheRunningProxy()
    {
        var unsigned = new ProxyReference("u:old", First.Id);
        var signed = Summary(First) with { Aliases = [unsigned] };
        var actual = new RunningTransportProxy(unsigned, "udp.example", 51820);
        var row = new TransportProxyItemViewModel(signed);
        row.ApplyUsage([Entry(Second, actual)]);

        Assert.True(row.HasUsed);
        Assert.False(row.HasChosen);
        Assert.Contains("Same", TransportProxyPresentation.Running([signed], actual), StringComparison.Ordinal);
        row.Apply(signed with { Aliases = null });
        Assert.False(row.HasUsed);
    }

    [Fact]
    public void AvailableProxyCanBeIncompatibleWithoutChangingItsAvailability()
    {
        var denied = Summary(First) with { Availability = TransportProxyAvailability.Available,
            Targets = new(true, [new("other.example", 443)]), CompatibleConfigs = [] };
        var editor = new ConfigTransportViewModel(new Agent(), "vpn", false, 1420, false, endpoint: "udp.example:51820");
        editor.ApplyProxyCatalog([denied, Summary(Second)], First, true);

        Assert.True(editor.SelectedProxyAvailable);
        Assert.Equal(Loc.Instance.Get("Proxies_Available"), editor.SelectedProxyAvailability);
        Assert.Equal(Loc.Instance.Get("Proxies_TargetDenied"), editor.SelectedProxyFault);
        Assert.False(editor.SelectedProxyChoice!.IsSelectable);
        Assert.False(editor.IsDirty);
        editor.SelectedProxyChoice = editor.ProxyChoices.Single(one => one.Reference == Second);
        editor.SelectedProxyChoice = editor.ProxyChoices.Single(one => one.Reference == First);
        Assert.Equal(Second, editor.SelectedProxyChoice.Reference);
    }

    [Fact]
    public void AWithdrawnSelectionStaysVisibleAndCannotBeSelectedAgain()
    {
        var editor = new ConfigTransportViewModel(new Agent(), "vpn", false, 1420, false);
        editor.ApplyProxyCatalog([], First, true);
        Assert.False(editor.SelectedProxyChoice!.IsSelectable);
        Assert.False(editor.CanViewSelectedProxy);
        editor.SelectedProxyChoice = editor.ProxyChoices[0];
        editor.SelectedProxyChoice = editor.ProxyChoices.Single(one => one.Reference == First);
        Assert.Null(editor.SelectedProxyChoice.Reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVerifiedRunningAliasDoesNotSilentlyReplaceAMissingSavedChoice(bool reversed)
    {
        var unsigned = new ProxyReference("u:old", First.Id);
        var retired = Summary(unsigned) with { Missing = true };
        var signed = Summary(First) with { Aliases = [unsigned] };
        var catalog = reversed ? new[] { signed, retired } : [retired, signed];
        var agent = new Agent();
        var editor = new ConfigTransportViewModel(agent, "vpn", false, 1420, false);
        editor.ApplyProxyCatalog(catalog, unsigned, true);
        editor.ApplyRunningProxy(new(unsigned, "udp.example", 51820), true);

        Assert.Equal(unsigned, editor.SelectedProxyChoice!.Reference);
        Assert.False(editor.SelectedProxyChoice.IsSelectable);
        Assert.False(editor.CanViewSelectedProxy);
        Assert.True(editor.CanViewRunningProxy);
        Assert.Equal(Loc.Instance.Get("Proxies_Missing"), editor.SelectedProxyFault);
        Assert.False(editor.IsDirty);
        Assert.DoesNotContain(agent.Commands, one => one.Op == IpcContract.OpSelectTransportProxy);
        Assert.Same(signed, TransportProxyPresentation.FindRunning(catalog, unsigned));
        Assert.Same(retired, TransportProxyPresentation.FindExact(catalog, unsigned));
        var oldRow = new TransportProxyItemViewModel(retired);
        var currentRow = new TransportProxyItemViewModel(signed);
        oldRow.ApplyUsage([Entry(unsigned, new(unsigned, "udp.example", 51820))], catalog);
        currentRow.ApplyUsage([Entry(unsigned, new(unsigned, "udp.example", 51820))], catalog);
        Assert.True(oldRow.HasChosen);
        Assert.False(oldRow.HasUsed);
        Assert.False(currentRow.HasChosen);
        Assert.True(currentRow.HasUsed);
    }

    [Fact]
    public void ConfigViewAndBackKeepTheSameDirtyConfigEditor()
    {
        var agent = new Agent { Summaries = [Summary(First)] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "config" };
        var snapshot = new StatusSnapshot("1", "vpn", [Entry(First, new(First, "udp.example", 51820))], [], TransportProxies: agent.Summaries);
        shell.Config.Apply(snapshot);
        shell.Proxies.Apply(snapshot);
        shell.Config.OpenConfigFor("vpn");
        var editor = shell.Config.ConfigTransport!;
        editor.MtuMode = (int)MtuMode.Custom;
        editor.Mtu = "1380";

        editor.ViewSelectedProxyCommand.Execute(null);
        Assert.Equal("proxies", shell.SettingsSection);
        Assert.NotNull(shell.Proxies.Editor);
        Assert.True(shell.Proxies.TryNavigateBack());

        Assert.Equal("config", shell.SettingsSection);
        Assert.Same(editor, shell.Config.ConfigTransport);
        Assert.True(editor.IsDirty);
        Assert.Equal("1380", editor.Mtu);
        Assert.DoesNotContain(agent.Commands, one => one.Op == IpcContract.OpSelectTransportProxy);
    }

    [Fact]
    public async Task LoadingFailureAndRetryDoNotFlashTheEmptyStateOrDropKnownRows()
    {
        var reply = new TaskCompletionSource<IpcAck>();
        var agent = new Agent { ListReply = reply.Task };
        var shell = new MainWindowViewModel(agent, new UiPreferences());
        shell.Proxies.EnterSection();
        Assert.True(shell.Proxies.IsCatalogueLoading);
        Assert.False(shell.Proxies.IsEmpty);
        reply.SetResult(new(false, "proxy-unavailable"));
        await shell.Proxies.RetryCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.True(shell.Proxies.ShowCatalogueError);
        Assert.False(shell.Proxies.IsEmpty);

        agent.ListReply = null;
        agent.Summaries = [Summary(First)];
        await shell.Proxies.RetryCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.False(shell.Proxies.ShowCatalogueError);
        Assert.False(shell.Proxies.IsCatalogueLoading);
        Assert.Single(shell.Proxies.Items);
    }

    [Fact]
    public void ViewingAProxyKeepsTheImportDraftAndItsUnsavedSelection()
    {
        var agent = new Agent { Summaries = [Summary(First)] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "config" };
        var snapshot = new StatusSnapshot("1", null, [], [], TransportProxies: agent.Summaries);
        shell.Config.Apply(snapshot);
        shell.Proxies.Apply(snapshot);
        shell.Config.BeginManualImportCommand.Execute(null);
        shell.Config.SectionConfigName = "Uncommitted name";
        var editor = shell.Config.SectionTransport!;
        editor.ApplyProxyCatalog(agent.Summaries, First, true);

        editor.ViewSelectedProxyCommand.Execute(null);
        Assert.Equal("proxies", shell.SettingsSection);
        Assert.True(shell.Proxies.TryNavigateBack());

        Assert.True(shell.Config.IsCreatingSectionConfig);
        Assert.Equal("Uncommitted name", shell.Config.SectionConfigName);
        Assert.Same(editor, shell.Config.SectionTransport);
        Assert.Equal(First, editor.SelectedProxyChoice!.Reference);
    }

    [Fact]
    public void PublicRunningFrontContainsNoAuthenticationOrTlsFields()
    {
        var definition = new OfferedProxy("one", "Same", "websocket", "cdn.example", 443, "front",
            new("secret-sni", "secret-pin"), new("bearer", "secret-credential"), DateTimeOffset.MinValue);
        var actual = RunningTransportProxy.From(new(new(First, definition), "vpn", "udp.example", 51820, "Moscow"), "");
        var wire = JsonSerializer.Serialize(actual, IpcJson.Options);
        Assert.Equal("Moscow", actual.Front!.Source);
        Assert.DoesNotContain("secret-", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tls", wire, StringComparison.OrdinalIgnoreCase);
        var old = JsonSerializer.Deserialize<RunningTransportProxy>("{\"reference\":null,\"targetHost\":\"old.example\",\"targetPort\":443}", IpcJson.Options);
        Assert.Null(old!.Front);
    }

    private static TransportProxySummary Summary(ProxyReference reference) => new(reference, "Same", "websocket", "cdn.example", 443, "front", false, ["Moscow"]);
    private static ConfigEntry Entry(ProxyReference reference, RunningTransportProxy? actual) => new("vpn", "udp.example:51820", false,
        ConnectionStatus.Connected, [], TransportProxy: reference, TransportProxyChosen: true, RunningTransport: actual);

    private sealed class Agent : IAgentConnection
    {
        public IReadOnlyList<TransportProxySummary> Summaries { get; set; } = [];
        public Task<IpcAck>? ListReply { get; set; }
        public List<IpcCommand> Commands { get; } = [];
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);
        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Commands.Add(command);
            return command.Op == IpcContract.OpListTransportProxies
                ? ListReply ?? Task.FromResult(new IpcAck(true, JsonSerializer.Serialize(Summaries, IpcJson.Options)))
                : Task.FromResult(new IpcAck(true, ""));
        }
    }
}
