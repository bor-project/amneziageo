using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TransportProxyUiTests
{
    private static readonly ProxyReference First = new("a5b67339-b508-4c20-a3c0-6457ea303f59", "proxy1");
    private static readonly ProxyReference Second = new("b517f53a-2a57-47e6-b00e-19706ee38f9a", "proxy1");

    [Theory]
    [InlineData("xhttp-runtime-missing", "Proxies_XhttpUnavailable")]
    [InlineData("xhttp-runtime-version", "Proxies_XhttpUnavailable")]
    [InlineData("xhttp-runtime-start", "Proxies_XhttpFailed")]
    [InlineData("xhttp-runtime-exited", "Proxies_XhttpFailed")]
    [InlineData("xhttp-runtime-timeout", "Proxies_XhttpFailed")]
    [InlineData("xhttp-version-timeout", "Proxies_XhttpFailed")]
    public void XhttpFailuresHaveLocalizedMessages(string code, string key)
    {
        Assert.Equal(Loc.Instance.Get(key), TransportProxyWords.Fault(code));
        Assert.DoesNotContain(code, TransportProxyWords.Fault(code), StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredProxiesDoNotSelectThemselves()
    {
        var editor = Settings(new Agent());
        editor.ApplyProxyCatalog([Summary(First), Summary(Second)], null, true);

        Assert.Null(editor.SelectedProxyChoice?.Reference);
        Assert.Equal(3, editor.ProxyChoices.Count);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void TheSourceDistinguishesEqualIds()
    {
        var editor = Settings(new Agent());
        editor.ApplyProxyCatalog([Summary(First), Summary(Second)], Second, true);

        Assert.Equal(Second, editor.SelectedProxyChoice?.Reference);
        Assert.Equal(3, editor.ProxyChoices.Count);
    }

    [Fact]
    public void AWithdrawnSelectionRemainsVisible()
    {
        var editor = Settings(new Agent());
        editor.ApplyProxyCatalog([Summary(First)], First, true);
        editor.ApplyProxyCatalog([], First, true);

        Assert.Equal(First, editor.SelectedProxyChoice?.Reference);
        Assert.Contains(Loc.Instance.Get("Proxies_Missing"), editor.SelectedProxyChoice!.Label, StringComparison.Ordinal);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void AnIncomingSnapshotPreservesAnUnsavedChoice()
    {
        var editor = Settings(new Agent());
        editor.ApplyProxyCatalog([Summary(First), Summary(Second)], First, true);
        editor.SelectedProxyChoice = editor.ProxyChoices.Single(one => one.Reference == Second);
        editor.ApplyProxyCatalog([Summary(First)], First, true);

        Assert.Equal(Second, editor.SelectedProxyChoice?.Reference);
        Assert.True(editor.IsDirty);
        editor.Revert();
        Assert.Equal(First, editor.SelectedProxyChoice?.Reference);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task ExplicitNoneTravelsInItsOwnCommand()
    {
        var agent = new Agent();
        var editor = Settings(agent);
        editor.ApplyProxyCatalog([Summary(First)], First, true);
        editor.SelectedProxyChoice = editor.ProxyChoices.Single(one => one.Reference is null);

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        var command = Assert.Single(agent.Commands.Where(one => one.Op == IpcContract.OpSelectTransportProxy));
        Assert.Equal(["vpn", "null"], command.Args);
        Assert.DoesNotContain(agent.Commands, one => one.Args.Contains("explicit-legacy"));
    }

    [Fact]
    public async Task MtuChangesDoNotReselectAMissingProxy()
    {
        var agent = new Agent();
        var editor = Settings(agent);
        editor.ApplyProxyCatalog([], First, true);
        editor.MtuMode = (int)MtuMode.Custom;
        editor.Mtu = "1380";

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.DoesNotContain(agent.Commands, one => one.Op == IpcContract.OpSelectTransportProxy);
        Assert.Equal(First, editor.SelectedProxyChoice?.Reference);
    }

    [Fact]
    public void ConfigSnapshotsSeedTheOpenSettingsWithoutRecreatingThem()
    {
        var agent = new Agent();
        var config = new MainWindowViewModel(agent, new UiPreferences()).Config;
        var entry = new ConfigEntry("vpn", "vpn.example:51820", false, ConnectionStatus.Disconnected, [], TransportProxy: Second, TransportProxyChosen: true);
        config.Apply(new StatusSnapshot("1", null, [entry], [], TransportProxies: [Summary(First), Summary(Second)]));
        config.OpenConfig = "vpn";
        var editor = config.ConfigTransport;
        Assert.NotNull(editor);
        Assert.Equal(Second, editor.SelectedProxyChoice?.Reference);

        config.Apply(new StatusSnapshot("1", null, [entry], [], TransportProxies: []));

        Assert.Same(editor, config.ConfigTransport);
        Assert.Equal(Second, editor.SelectedProxyChoice?.Reference);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task NewWebSocketUsesBearerAndTheHostAsDefaultSni()
    {
        var agent = new Agent();
        var editor = NewEditor(agent);

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.NotNull(agent.Saved);
        Assert.Equal("", agent.SaveId);
        Assert.Equal("bearer", agent.Saved.Auth.Scheme);
        Assert.Equal("cdn.example", agent.Saved.Tls.ServerName);
        Assert.Null(agent.Saved.Xhttp);
        Assert.Equal(First, editor.Reference);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task XhttpCarriesTheFixedGetHeaderProfile()
    {
        var agent = new Agent();
        var editor = NewEditor(agent);
        editor.TransportIndex = 1;
        editor.Credential = "63c77f4f-8501-4308-a32d-2a36ea0cb6ed";

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.NotNull(agent.Saved);
        Assert.Equal("vless", agent.Saved.Auth.Scheme);
        Assert.Equal(new OfferedXHttp("packet-up", "GET", "header"), agent.Saved.Xhttp);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("pin")]
    [InlineData("sni")]
    [InlineData("credential")]
    public async Task InvalidLocalFieldsPreventIpcWrites(string field)
    {
        var agent = new Agent();
        var editor = NewEditor(agent);
        switch (field)
        {
            case "name": editor.Name = new string('a', 65); break;
            case "host": editor.Host = "https://wrong.example"; break;
            case "port": editor.Port = "65536"; break;
            case "path": editor.Path = "bad/path"; break;
            case "pin": editor.Pin = "1234"; break;
            case "sni": editor.ServerName = "https://wrong.example"; break;
            case "credential": editor.Credential = "a b"; break;
        }

        Assert.False(await editor.CommitAsync().ConfigureAwait(false));
        Assert.Empty(agent.Commands);
        Assert.NotEmpty(editor.StatusMessage);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task RejectedSavesKeepTheDraft()
    {
        var agent = new Agent { RejectSave = true };
        var editor = NewEditor(agent);

        Assert.False(await editor.CommitAsync().ConfigureAwait(false));
        Assert.True(editor.IsDirty);
        Assert.Null(editor.Reference);
        Assert.NotEmpty(editor.StatusMessage);
    }

    [Fact]
    public async Task ServerDetailsContainNoCredentialAndCannotBeSaved()
    {
        var agent = new Agent();
        var editor = new TransportProxyEditorViewModel(agent, summary: Summary(First));

        Assert.False(editor.Editable);
        Assert.False(editor.ShowCredential);
        Assert.Empty(editor.Credential);
        Assert.False(await editor.CommitAsync().ConfigureAwait(false));
        Assert.Empty(agent.Commands);
    }

    [Fact]
    public async Task LegacyEditsKeepTheirAuthSchemeAndTransport()
    {
        var agent = new Agent();
        var local = new CatalogTransportProxy(First, Definition() with { Auth = new OfferedProxyAuth("legacy", "user:password"), Path = "" }, true, true);
        var editor = new TransportProxyEditorViewModel(agent, local);
        Assert.False(editor.ModernEditable);
        editor.Path = "new-path";

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.NotNull(agent.Saved);
        Assert.Equal("legacy", agent.Saved.Auth.Scheme);
        Assert.Equal("websocket", agent.Saved.Transport);
        Assert.Equal("user:password", agent.Saved.Auth.Credential);
        Assert.Equal("new-path", agent.Saved.Path);
    }

    [Fact]
    public async Task LocalCardsLoadCredentialsOnlyWhenOpened()
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true), Summaries = [Summary(First) with { Local = true }] };
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot(agent.Summaries));

        Assert.DoesNotContain(agent.Commands, one => one.Op == IpcContract.OpGetTransportProxy);
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);

        Assert.NotNull(catalog.Editor);
        Assert.Equal("secret-token", catalog.Editor.Credential);
        Assert.Single(agent.Commands.Where(one => one.Op == IpcContract.OpGetTransportProxy));
    }

    [Fact]
    public async Task HeartbeatsKeepTheOpenEditorAndItsUnsavedFields()
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true) };
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot([Summary(First) with { Local = true }]));
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);
        var editor = catalog.Editor;
        Assert.NotNull(editor);
        editor.Name = "unfinished";
        var changes = new List<string?>();
        catalog.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        catalog.Apply(Snapshot([Summary(First) with { Name = "incoming", Local = true, Availability = TransportProxyAvailability.Available }]));

        Assert.Same(editor, catalog.Editor);
        Assert.Equal("unfinished", editor.Name);
        Assert.True(editor.IsDirty);
        Assert.Equal(Loc.Instance.Get("Proxies_Available"), editor.Availability);
        Assert.DoesNotContain(nameof(TransportProxiesViewModel.ShowEditor), changes);
    }

    [Fact]
    public async Task ServerDetailsFollowHelloWithoutBeingReopened()
    {
        var agent = new Agent();
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot([Summary(First)]));
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);
        var editor = catalog.Editor;

        catalog.Apply(Snapshot([Summary(First) with { Name = "new front", Host = "new.example", Sources = ["other vpn"], Availability = TransportProxyAvailability.Available }]));

        Assert.Same(editor, catalog.Editor);
        Assert.Equal("new front", editor!.Name);
        Assert.Equal("new.example", editor.Host);
        Assert.Equal("other vpn", editor.Source);
        Assert.Equal(Loc.Instance.Get("Proxies_Available"), editor.Availability);
        Assert.False(editor.Editable);
        Assert.False(editor.ShowCredential);
        Assert.Empty(editor.Credential);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AWithdrawnOpenDetailRetainsItsAddressAndShowsMissing()
    {
        var agent = new Agent();
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot([Summary(First)]));
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);
        var editor = catalog.Editor;

        catalog.Apply(Snapshot([]));

        Assert.Same(editor, catalog.Editor);
        Assert.Equal("cdn.example", editor!.Host);
        Assert.Equal(Loc.Instance.Get("Proxies_Missing"), editor.Availability);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AvailabilityProbeCompletesWithoutReplacingTheEditor()
    {
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent { ProbeReply = pending.Task };
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot([Summary(First)]));
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);
        var editor = catalog.Editor;
        var probe = catalog.ProbeCommand.ExecuteAsync(null);
        Assert.True(catalog.IsProbing);
        Assert.Same(editor, catalog.Editor);

        agent.Summaries = [Summary(First) with { Availability = TransportProxyAvailability.Available }];
        pending.SetResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Available)));
        await probe.ConfigureAwait(false);

        Assert.False(catalog.IsProbing);
        Assert.Same(editor, catalog.Editor);
        Assert.Equal(Loc.Instance.Get("Proxies_Available"), editor!.Availability);
    }

    [Fact]
    public async Task DeleteReturnsToTheCatalogWithoutSelectingAnotherProxy()
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true), Summaries = [Summary(First) with { Local = true }] };
        var catalog = new MainWindowViewModel(agent, new UiPreferences()).Proxies;
        catalog.Apply(Snapshot(agent.Summaries));
        await catalog.OpenCardCommand.ExecuteAsync(catalog.Items[0]).ConfigureAwait(false);

        await catalog.DeleteCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.True(catalog.IsCatalogue);
        Assert.Empty(catalog.Items);
        Assert.DoesNotContain(agent.Commands, one => one.Op == IpcContract.OpSelectTransportProxy);
    }

    [Fact]
    public async Task AnUnrelatedProxySavePreservesADeferredReconnectOffer()
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true), Summaries = [Summary(First) with { Local = true }] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "proxies" };
        shell.Proxies.Apply(Snapshot(agent.Summaries));
        shell.Home.RestartPending = true;
        Assert.True(shell.ShowReconnectBar);
        shell.DismissReconnectCommand.Execute(null);
        await shell.Proxies.OpenCardCommand.ExecuteAsync(shell.Proxies.Items[0]).ConfigureAwait(false);
        shell.Proxies.Editor!.Name = "Unused renamed proxy";

        await shell.Proxies.SaveSectionCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.NotNull(agent.Saved);
        Assert.True(shell.Home.RestartPending);
        Assert.False(shell.ShowReconnectBar);
    }

    [Fact]
    public async Task AConfigSaveWithoutConnectionChangesPreservesADeferredReconnectOffer()
    {
        var shell = new MainWindowViewModel(new Agent(), new UiPreferences()) { Nav = "settings", SettingsSection = "config" };
        shell.Home.RestartPending = true;
        Assert.True(shell.ShowReconnectBar);
        shell.DismissReconnectCommand.Execute(null);

        await shell.Config.SaveSectionCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.True(shell.Home.RestartPending);
        Assert.False(shell.ShowReconnectBar);
        shell.Home.RestartPending = false;
        shell.Home.RestartPending = true;
        Assert.True(shell.ShowReconnectBar);
    }

    [Fact]
    public async Task ChangingAUsedProxyRearmsADeferredReconnectOffer()
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true), Summaries = [Summary(First) with { Local = true }] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "proxies" };
        shell.Config.Apply(new StatusSnapshot("1", "vpn",
            [new ConfigEntry("vpn", "vpn.example:51820", false, ConnectionStatus.Connected, [], TransportProxy: First, TransportProxyChosen: true)], [],
            Active: true, RestartRequired: true, TransportProxies: agent.Summaries));
        shell.Proxies.Apply(Snapshot(agent.Summaries));
        shell.Home.RestartPending = true;
        shell.DismissReconnectCommand.Execute(null);
        await shell.Proxies.OpenCardCommand.ExecuteAsync(shell.Proxies.Items[0]).ConfigureAwait(false);
        shell.Proxies.Editor!.Credential = "changed-token";

        await shell.Proxies.SaveSectionCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.NotNull(agent.Saved);
        Assert.True(shell.ShowReconnectBar);
    }

    [Fact]
    public void ProxyEditorDistinguishesConnectionParametersFromItsLabel()
    {
        var editor = new TransportProxyEditorViewModel(new Agent(), new CatalogTransportProxy(First, Definition(), true));

        editor.Name = "Renamed";
        Assert.True(editor.IsDirty);
        Assert.False(editor.ConnectionChanged);
        editor.Credential = "changed-token";
        Assert.True(editor.ConnectionChanged);
    }

    private static ConfigTransportViewModel Settings(Agent agent) => new(agent, "vpn", false, 1420, false);

    [Fact]
    public async Task ALocalRestrictionRequiresValidTargetsAndTravelsInTheSavedDefinition()
    {
        var agent = new Agent();
        var editor = NewEditor(agent);
        editor.Restricted = true;
        Assert.False(editor.CanCommit());
        Assert.Equal(Loc.Instance.Get("Proxies_InvalidTargets"), editor.StatusMessage);
        editor.TargetsText = "VPN.EXAMPLE.:443\n[2001:db8::1]:51820";

        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.True(agent.Saved!.Targets!.Restricted);
        Assert.Equal([new ProxyDestination("2001:db8::1", 51820), new("vpn.example", 443)], agent.Saved.Targets.Destinations);
    }

    [Fact]
    public void LocalRestrictionEditsRevertWithoutChangingTheConnectionDefinition()
    {
        var definition = Definition() with { Targets = new(true, [new("vpn.example", 443)]) };
        var editor = new TransportProxyEditorViewModel(new Agent(), new CatalogTransportProxy(First, definition, true));
        editor.TargetsText = "other.example:443";
        Assert.True(editor.IsDirty);
        Assert.False(editor.ConnectionChanged);

        editor.Revert();
        Assert.Equal("vpn.example:443", editor.TargetsText);
        Assert.True(editor.Restricted);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ServerRestrictionsRemainReadOnlyAndFollowHello()
    {
        var summary = Summary(First) with { Targets = new(true, [new("vpn.example", 443)]) };
        var editor = new TransportProxyEditorViewModel(new Agent(), summary: summary);
        Assert.True(editor.ShowTargets);
        Assert.False(editor.ModernEditable);
        Assert.True(editor.Restricted);
        Assert.Equal("vpn.example:443", editor.TargetsText);

        editor.ApplySummary(summary with { Targets = new(true, [new("other.example", 51820)]) });
        Assert.Equal("other.example:51820", editor.TargetsText);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void IncompatibleChoicesAreFilteredWhileASavedChoiceRemainsVisible()
    {
        var editor = Settings(new Agent());
        var restricted = Summary(First) with { Targets = new(true, [new("other.example", 443)]), CompatibleConfigs = [] };
        editor.ApplyProxyCatalog([restricted, Summary(Second)], null, true);
        Assert.DoesNotContain(editor.ProxyChoices, one => one.Reference == First);
        Assert.Contains(editor.ProxyChoices, one => one.Reference == Second);

        editor.ApplyProxyCatalog([restricted, Summary(Second)], First, true);
        Assert.Equal(First, editor.SelectedProxyChoice!.Reference);
        Assert.Contains(Loc.Instance.Get("Proxies_TargetDenied"), editor.SelectedProxyChoice.Label, StringComparison.Ordinal);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ImportedConfigurationsUseTheDraftDestinationAndLegacyIssuer()
    {
        var editor = Settings(new Agent());
        editor.SeedProxyTarget("[Peer]\nPublicKey = server-key\nEndpoint = vpn.example:443");
        var legacy = Summary(First) with { Legacy = true, LegacyIssuer = "other-server", LegacyPort = 443, CompatibleConfigs = [] };
        var modern = Summary(Second) with { Targets = new(true, [new("vpn.example", 443)]), CompatibleConfigs = [] };
        editor.ApplyProxyCatalog([legacy, modern], null, true);

        Assert.DoesNotContain(editor.ProxyChoices, one => one.Reference == First);
        Assert.Contains(editor.ProxyChoices, one => one.Reference == Second);
    }

    [Theory]
    [InlineData("other.example:443", true)]
    [InlineData("vpn.example:51820\nother.example:443", false)]
    public async Task RestrictionEditsRearmOnlyWhenTheRunningDestinationChangesPermission(string targets, bool rearm)
    {
        var agent = new Agent { Local = new CatalogTransportProxy(First, Definition(), true), Summaries = [Summary(First) with { Local = true }] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "proxies" };
        shell.Config.Apply(new StatusSnapshot("1", "vpn",
            [new ConfigEntry("vpn", "vpn.example:51820", false, ConnectionStatus.Connected, [], TransportProxy: First, TransportProxyChosen: true)], [],
            Active: true, RestartRequired: true, TransportProxies: agent.Summaries));
        shell.Proxies.Apply(Snapshot(agent.Summaries));
        shell.Home.RestartPending = true;
        shell.DismissReconnectCommand.Execute(null);
        await shell.Proxies.OpenCardCommand.ExecuteAsync(shell.Proxies.Items[0]).ConfigureAwait(false);
        shell.Proxies.Editor!.Restricted = true;
        shell.Proxies.Editor.TargetsText = targets;

        await shell.Proxies.SaveSectionCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.NotNull(agent.Saved);
        Assert.Equal(rearm, shell.ShowReconnectBar);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task PolicyEditsUseTheRunningCarrierAfterAnotherProxyWasSavedAndThePromptDismissed(bool editRunning, bool actualUdp, bool rearm)
    {
        var edited = editRunning ? First : Second;
        var agent = new Agent { Local = new CatalogTransportProxy(edited, Definition() with { Id = edited.Id }, true),
            Summaries = [Summary(edited) with { Local = true }] };
        var shell = new MainWindowViewModel(agent, new UiPreferences()) { Nav = "settings", SettingsSection = "proxies" };
        shell.Config.Apply(new StatusSnapshot("1", "vpn",
            [new ConfigEntry("vpn", "draft.example:443", false, ConnectionStatus.Connected, [], TransportProxy: Second, TransportProxyChosen: true,
                RunningTransport: new RunningTransportProxy(actualUdp ? null : First, "actual.example", 51820))], [],
            Active: true, RestartRequired: true, TransportProxies: agent.Summaries));
        shell.Proxies.Apply(Snapshot(agent.Summaries));
        shell.Home.RestartPending = true;
        shell.DismissReconnectCommand.Execute(null);
        await shell.Proxies.OpenCardCommand.ExecuteAsync(shell.Proxies.Items[0]).ConfigureAwait(false);
        shell.Proxies.Editor!.Restricted = true;
        shell.Proxies.Editor.TargetsText = "draft.example:443";

        await shell.Proxies.SaveSectionCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.NotNull(agent.Saved);
        Assert.Equal(rearm, shell.ShowReconnectBar);
    }

    private static TransportProxySummary Summary(ProxyReference reference) => new(reference, "front", "websocket", "cdn.example", 443, "vpn", false, [reference == First ? "office" : "home"]);
    private static OfferedProxy Definition() => new(First.Id, "front", "websocket", "cdn.example", 443, "vpn", new OfferedProxyTls("cdn.example", ""), new OfferedProxyAuth("bearer", "secret-token"), DateTimeOffset.MinValue);
    private static StatusSnapshot Snapshot(IReadOnlyList<TransportProxySummary> summaries) => new("1", null, [], [], TransportProxies: summaries);
    private static IpcAck Reply<T>(T value) => new(true, JsonSerializer.Serialize(value, IpcJson.Options));

    private static TransportProxyEditorViewModel NewEditor(Agent agent) => new(agent)
    {
        Name = "front", Host = "cdn.example", Path = "vpn", Credential = "secret-token",
    };

    private sealed class Agent : IAgentConnection
    {
        public List<IpcCommand> Commands { get; } = [];
        public CatalogTransportProxy? Local { get; init; }
        public IReadOnlyList<TransportProxySummary> Summaries { get; set; } = [];
        public OfferedProxy? Saved { get; private set; }
        public string SaveId { get; private set; } = "";
        public bool RejectSave { get; init; }
        public Task<IpcAck>? ProbeReply { get; init; }
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Commands.Add(command);
            switch (command.Op)
            {
                case IpcContract.OpListTransportProxies:
                    return Task.FromResult(Reply(Summaries));
                case IpcContract.OpGetTransportProxy:
                    return Task.FromResult(Reply(Local));
                case IpcContract.OpSaveTransportProxy:
                    SaveId = command.Args[0];
                    Saved = JsonSerializer.Deserialize<OfferedProxy>(command.Args[1], IpcJson.Options);
                    return Task.FromResult(RejectSave ? new IpcAck(false, "bad-proxy") : Reply(First));
                case IpcContract.OpRemoveTransportProxy:
                    Summaries = [];
                    break;
                case IpcContract.OpProbeTransportProxy:
                    return ProbeReply ?? Task.FromResult(Reply(new TransportProxyProbeResult(TransportProxyAvailability.Unknown)));
            }

            return Task.FromResult(new IpcAck(true, ""));
        }
    }
}
