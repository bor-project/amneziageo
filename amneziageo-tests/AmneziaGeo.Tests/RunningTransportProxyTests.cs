using System.Security.Cryptography;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class RunningTransportProxyTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ageo-running-proxy-" + Guid.NewGuid().ToString("N") + ".db");
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;
    private string _config = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        _catalog = new TransportProxyCatalog(_store);
        var key = Convert.ToBase64String(SHA256.HashData("running-target"u8));
        _config = $"[Interface]\nPrivateKey = {key}\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = actual.example:443\nAllowedIPs = 0.0.0.0/0";
        await _store.SaveConfigAsync("a", _config).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WindowsDescriptorCleanupRemovesOnlyTheCurrentProcessRecord()
    {
        var transport = new RunningTransportProxy(null, "actual.example", 443);
        var key = "running-transport:a:" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var other = "running-transport:a:" + (Environment.ProcessId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _store.SetSettingAsync(other, "other process").ConfigureAwait(false);
        await RunningTransportState.WriteAsync(_store, "a", transport, NullLogger.Instance).ConfigureAwait(false);
        Assert.NotNull(await _store.GetSettingAsync(key).ConfigureAwait(false));

        await RunningTransportState.ClearAsync(_store, "a", NullLogger.Instance).ConfigureAwait(false);

        Assert.Null(await _store.GetSettingAsync(key).ConfigureAwait(false));
        Assert.Equal("other process", await _store.GetSettingAsync(other).ConfigureAwait(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavingAPolicyTracksTheRunningProxyAfterTheSavedSelectionChanges(bool editRunning)
    {
        var first = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        var second = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        await _catalog.SelectAsync("a", first).ConfigureAwait(false);
        var actual = RunningTransportProxy.From(await _catalog.ResolveAsync("a").ConfigureAwait(false), _config);
        await _catalog.SelectAsync("a", second).ConfigureAwait(false);
        var edited = editRunning ? first : second;
        var definition = Proxy() with { Targets = new(true, [new("other.example", 443)]) };
        var changed = new List<string>();

        var ack = await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(IpcContract.OpSaveTransportProxy,
            [edited.Id, JsonSerializer.Serialize(definition, IpcJson.Options)]), running: ["a"], changed: changed.Add,
            actual: new Dictionary<string, RunningTransportProxy> { ["a"] = actual }).ConfigureAwait(false);

        Assert.True(ack!.Ok);
        Assert.Equal(editRunning ? ["a"] : Array.Empty<string>(), changed);
        Assert.Equal(second, (await _catalog.SelectionAsync("a").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task RemovingTheRunningProxyTracksItsReferenceAfterTheSavedSelectionChanges()
    {
        var first = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        var second = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        await _catalog.SelectAsync("a", first).ConfigureAwait(false);
        var actual = RunningTransportProxy.From(await _catalog.ResolveAsync("a").ConfigureAwait(false), _config);
        await _catalog.SelectAsync("a", second).ConfigureAwait(false);
        var changed = new List<string>();

        var ack = await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(IpcContract.OpRemoveTransportProxy,
            [JsonSerializer.Serialize(first, IpcJson.Options)]), running: ["a"], changed: changed.Add,
            actual: new Dictionary<string, RunningTransportProxy> { ["a"] = actual }).ConfigureAwait(false);

        Assert.True(ack!.Ok);
        Assert.Equal(["a"], changed);
    }

    [Fact]
    public async Task ARunningDirectSessionDoesNotTakeTheSavedProxyForItsCarrier()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        var actual = RunningTransportProxy.From(null, _config);
        var changed = new List<string>();

        var ack = await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(IpcContract.OpSaveTransportProxy,
            [reference.Id, JsonSerializer.Serialize(Proxy() with { Auth = new("bearer", "changed") }, IpcJson.Options)]),
            running: ["a"], changed: changed.Add, actual: new Dictionary<string, RunningTransportProxy> { ["a"] = actual }).ConfigureAwait(false);

        Assert.True(ack!.Ok);
        Assert.Empty(changed);
        Assert.Null(await _catalog.ResolveRunningAsync("a", actual).ConfigureAwait(false));
    }

    [Fact]
    public async Task TheRunningEndpointSurvivesAStoredConfigurationEdit()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        var actual = RunningTransportProxy.From(await _catalog.ResolveAsync("a").ConfigureAwait(false), _config);
        await _store.SaveConfigAsync("a", _config.Replace("actual.example:443", "draft.example:51820", StringComparison.Ordinal)).ConfigureAwait(false);
        var before = await TransportProxyChanges.CaptureAsync(_store, ["a"], actual: new Dictionary<string, RunningTransportProxy> { ["a"] = actual }).ConfigureAwait(false);

        await _catalog.SaveLocalAsync(reference.Id, Proxy() with { Targets = new(true, [new("draft.example", 51820)]) }).ConfigureAwait(false);

        Assert.NotNull(await _catalog.ResolveAsync("a").ConfigureAwait(false));
        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => _catalog.ResolveRunningAsync("a", actual)).ConfigureAwait(false);
        Assert.Equal("proxy-target-denied", error.Code);
        Assert.Equal(["a"], await before.ChangedAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task SelectingAnotherProxyStillChangesTheNextConnection()
    {
        var first = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        var second = await _catalog.SaveLocalAsync(null, Proxy()).ConfigureAwait(false);
        await _catalog.SelectAsync("a", first).ConfigureAwait(false);
        var actual = RunningTransportProxy.From(await _catalog.ResolveAsync("a").ConfigureAwait(false), _config);
        var changed = new List<string>();

        var ack = await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(IpcContract.OpSelectTransportProxy,
            ["a", JsonSerializer.Serialize(second, IpcJson.Options)]), running: ["a"], changed: changed.Add,
            actual: new Dictionary<string, RunningTransportProxy> { ["a"] = actual }).ConfigureAwait(false);

        Assert.True(ack!.Ok);
        Assert.Equal(["a"], changed);
    }

    [Fact]
    public void TheSnapshotDistinguishesAnOldAgentFromAnExplicitDirectTransport()
    {
        var old = JsonSerializer.Deserialize<ConfigEntry>("""{"name":"a","endpoint":"actual.example:443","geoSplit":false,"status":"connected","rules":[]}""", IpcJson.Options);
        Assert.NotNull(old);
        Assert.Null(old.RunningTransport);
        var entry = old with { RunningTransport = new RunningTransportProxy(null, "actual.example", 443, "public-server-key") };

        var text = JsonSerializer.Serialize(entry, IpcJson.Options);
        var restored = JsonSerializer.Deserialize<ConfigEntry>(text, IpcJson.Options);

        Assert.NotNull(restored!.RunningTransport);
        Assert.Null(restored.RunningTransport.Reference);
        Assert.Equal("actual.example", restored.RunningTransport.TargetHost);
    }

    [Fact]
    public void RunningMetadataContainsNoTransportCredentialOrPrivateKey()
    {
        var proxy = Proxy() with { Auth = new("bearer", "bearer-secret") };
        var resolved = new ResolvedTransportProxy(new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "one"), proxy),
            "a", "actual.example", 443);
        var metadata = RunningTransportProxy.From(resolved,
            "[Interface]\nPrivateKey = private-key\n[Peer]\nPublicKey = public-server-key\nEndpoint = actual.example:443");

        var text = JsonSerializer.Serialize(metadata, IpcJson.Options);

        Assert.Contains("public-server-key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", text, StringComparison.Ordinal);
    }

    private static OfferedProxy Proxy() => new("one", "Proxy", "websocket", "front.example", 443, "relay",
        new("front.example", ""), new("bearer", "test"), DateTimeOffset.UtcNow);
}
