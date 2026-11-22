using System.Text.Json;
using AmneziaGeo.Cli;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;
using Commands = AmneziaGeo.Cli.TransportProxyCommands;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

[Collection("Output")]
public sealed class TransportProxyCliOwnershipTests : IAsyncLifetime
{
    private const string Owner = "Office VPN";
    private const string Other = "Other VPN";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ag-cli-owner-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly BufferConsoleSink _console = new();
    private SqliteStateStore _store = null!;
    private Link _link = null!;

    public async ValueTask InitializeAsync()
    {
        Output.Sink = _console;
        Output.Json = false;
        Output.Quiet = false;
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        foreach (var name in new[] { Owner, Other })
        {
            var key = Convert.ToBase64String(new byte[32]);
            await _store.SaveConfigAsync(name, $"[Interface]\nPrivateKey = {key}\nAddress = 10.254.254.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = vpn.example.net:443\nAllowedIPs = 0.0.0.0/0").ConfigureAwait(false);
        }
        _link = new Link(_store);
    }

    public ValueTask DisposeAsync()
    {
        Output.Sink = new SystemConsoleSink();
        Output.Json = false;
        TextInput.StandardInput = null;
        _store.ClearPool();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreationBindsTheExactConfigurationAndMakesTheProxySelectableInItsUi()
    {
        Assert.Equal(Exit.Ok, await SaveAsync(["--config", Owner, "--stdin"]).ConfigureAwait(false));
        var record = Assert.Single((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
        Assert.Equal(Owner, record.OwnerConfig);
        Assert.Equal(["", JsonSerializer.Serialize(Definition(), IpcJson.Options), Owner], Assert.Single(_link.Calls).Args);

        var summary = Assert.Single(await TransportProxyCatalog.For(_store).ListAsync().ConfigureAwait(false));
        Assert.Equal([Owner], summary.CompatibleConfigs);
        using var uiLink = new NullAgentConnection();
        var ownerUi = new ConfigTransportViewModel(uiLink, Owner, false, 1420, false, endpoint: "vpn.example.net:443");
        var otherUi = new ConfigTransportViewModel(uiLink, Other, false, 1420, false, endpoint: "vpn.example.net:443");
        Assert.True(ownerUi.AllowsProxy(summary));
        Assert.False(otherUi.AllowsProxy(summary));
        ownerUi.ApplyProxyCatalog([summary], record.Reference, true);
        Assert.Empty(ownerUi.SelectedProxyFault);
        Assert.True(ownerUi.CanEnableSelectedProxy);

        var reference = record.Reference.Source + "/" + record.Reference.Id;
        Assert.Equal(Exit.Ok, await Commands.SelectAsync(_link, [Owner, reference]).ConfigureAwait(false));
        Assert.Equal(Exit.Failed, await Commands.SelectAsync(_link, [Other, reference]).ConfigureAwait(false));
        Assert.NotNull(await TransportProxyCatalog.For(_store).ResolveAsync(Owner).ConfigureAwait(false));
        Assert.DoesNotContain(Definition().Auth.Credential, _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingWithoutConfigKeepsTheOwnerReferenceAndDisabledSelectionAfterRestart()
    {
        Assert.Equal(Exit.Ok, await SaveAsync(["--config", Owner, "--stdin"]).ConfigureAwait(false));
        var catalog = TransportProxyCatalog.For(_store);
        var record = Assert.Single((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
        await catalog.SelectAsync(Owner, record.Reference, enabled: false).ConfigureAwait(false);
        Assert.Equal(Exit.Ok, await SaveAsync([record.Reference.Id, "--stdin"], Definition() with { Name = "Updated" }).ConfigureAwait(false));
        Assert.Equal(2, _link.Calls[^1].Args.Count);

        var restarted = new SqliteStateStore(_path);
        var kept = Assert.Single((await restarted.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
        Assert.Equal(record.Reference, kept.Reference);
        Assert.Equal(Owner, kept.OwnerConfig);
        Assert.Equal(record.Proxy.Auth, kept.Proxy.Auth);
        Assert.Equal(JsonSerializer.Serialize(record.Proxy.Targets), JsonSerializer.Serialize(kept.Proxy.Targets));
        Assert.Equal("Updated", kept.Proxy.Name);
        var selection = await TransportProxyCatalog.For(restarted).SelectionAsync(Owner).ConfigureAwait(false);
        Assert.Equal(record.Reference, selection.Reference);
        Assert.False(selection.Enabled);
        restarted.ClearPool();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("office vpn")]
    public async Task UnknownConfigurationDoesNotCreateAnOrphan(string owner)
    {
        Assert.Equal(Exit.Failed, await SaveAsync(["--config", owner, "--stdin"]).ConfigureAwait(false));
        Assert.Empty((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
        Assert.Contains("unknown-config", _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingCannotReassignAnOwnedProxy()
    {
        Assert.Equal(Exit.Ok, await SaveAsync(["--config", Owner, "--stdin"]).ConfigureAwait(false));
        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        var record = Assert.Single(state.Locals);
        Assert.Equal(Exit.Failed, await SaveAsync([record.Reference.Id, "--config", Other, "--stdin"]).ConfigureAwait(false));
        Assert.Equal(state.ToPayload(), (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        Assert.Equal(Exit.Ok, await SaveAsync([record.Reference.Id, "--config", Owner, "--stdin"]).ConfigureAwait(false));
        Assert.Equal(Owner, Assert.Single((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals).OwnerConfig);
    }

    [Fact]
    public async Task AnOldUnownedRecordRequiresAnExplicitOwnerToBecomeSelectableInTheUi()
    {
        var saved = await _link.SendAsync(IpcContract.OpSaveTransportProxy, "", JsonSerializer.Serialize(Definition(), IpcJson.Options)).ConfigureAwait(false);
        Assert.True(saved.Ok);
        var reference = JsonSerializer.Deserialize<ProxyReference>(saved.Message, IpcJson.Options)!;
        Assert.Equal(Exit.Ok, await SaveAsync([reference.Id, "--stdin"]).ConfigureAwait(false));
        var catalog = TransportProxyCatalog.For(_store);
        var summary = Assert.Single(await catalog.ListAsync().ConfigureAwait(false));
        using var uiLink = new NullAgentConnection();
        var ui = new ConfigTransportViewModel(uiLink, Owner, false, 1420, false, endpoint: "vpn.example.net:443");
        Assert.Null(summary.OwnerConfig);
        Assert.False(ui.AllowsProxy(summary));

        Assert.Equal(Exit.Ok, await SaveAsync([reference.Id, "--config", Owner, "--stdin"]).ConfigureAwait(false));
        var repaired = Assert.Single(await catalog.ListAsync().ConfigureAwait(false));
        Assert.Equal(reference, repaired.Reference);
        Assert.Equal(Owner, repaired.OwnerConfig);
        Assert.True(ui.AllowsProxy(repaired));
        Assert.Equal([Owner], repaired.CompatibleConfigs);
    }

    [Fact]
    public async Task TheFileInputCarriesTheSameOwnerAsStdin()
    {
        var file = Path.Combine(Path.GetTempPath(), "ag-cli-proxy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Definition(), IpcJson.Options)).ConfigureAwait(false);
            Assert.Equal(Exit.Ok, await Commands.RunAsync(_link, ["save", "--file", file, "--config=" + Owner]).ConfigureAwait(false));
            Assert.Equal(Owner, Assert.Single((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals).OwnerConfig);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task<int> SaveAsync(string[] args, OfferedProxy? definition = null)
    {
        TextInput.StandardInput = new StringReader(JsonSerializer.Serialize(definition ?? Definition(), IpcJson.Options));
        return await Commands.RunAsync(_link, ["save", .. args]).ConfigureAwait(false);
    }

    private static OfferedProxy Definition() => new("draft", "Local", "websocket", "relay.example.net", 443, "relay",
        new OfferedProxyTls("relay.example.net", ""), new OfferedProxyAuth("bearer", "private-test-token", "Authorization"),
        DateTimeOffset.MinValue, Targets: new(true, [new("vpn.example.net", 443)]));

    private sealed class Link(SqliteStateStore store) : IAgentLink
    {
        public List<IpcCommand> Calls { get; } = [];
        public StatusSnapshot Snapshot { get; } = new("test", null, []);

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public async Task<IpcAck> SendAsync(string op, params string[] args)
        {
            var command = new IpcCommand(op, args);
            Calls.Add(command);
            return (await AmneziaGeo.Ipc.TransportProxyCommands.TryExecuteAsync(store, command).ConfigureAwait(false))!;
        }
    }
}
