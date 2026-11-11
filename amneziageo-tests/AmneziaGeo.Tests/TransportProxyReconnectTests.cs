using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyReconnectTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ag-proxy-reconnect-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly List<string> _changed = [];
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        _catalog = TransportProxyCatalog.For(_store);
        foreach (var name in new[] { "a", "b", "direct" })
        {
            var key = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
            await _store.SaveConfigAsync(name, $"[Interface]\nPrivateKey = {key}\nAddress = 10.8.0.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = vpn-{name}.example.net:51820\nAllowedIPs = 0.0.0.0/0\n").ConfigureAwait(false);
            await _catalog.EnsureSelectionAsync(name).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
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
    public async Task EditingAnUnusedProxyPreservesADirectConnection()
    {
        var reference = await SaveAsync("unused").ConfigureAwait(false);

        var ack = await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["direct"], reference.Id,
            Json(Proxy("unused") with { Host = "changed.example.net" })).ConfigureAwait(false);

        Assert.True(ack.Ok);
        Assert.Empty(_changed);
        Assert.Null((await _catalog.SelectionAsync("direct").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task SelectingAProxyForAnInactiveConfigurationPreservesTheRunningConnection()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);

        var ack = await ExecuteAsync(IpcContract.OpSelectTransportProxy, ["a"], "b", Json(reference)).ConfigureAwait(false);

        Assert.True(ack.Ok);
        Assert.Empty(_changed);
        Assert.Equal(reference, (await _catalog.SelectionAsync("b").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task ASelectionChangeMarksOnlyTheAffectedRunningConfiguration()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSelectTransportProxy, ["a", "b"], "a", Json(reference)).ConfigureAwait(false)).Ok);

        Assert.Equal(["a"], _changed);
    }

    [Fact]
    public async Task DisablingTheRunningProxyRequiresReconnect()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSelectTransportProxy, ["a", "b"], "a", "null").ConfigureAwait(false)).Ok);

        Assert.Equal(["a"], _changed);
        Assert.Null((await _catalog.SelectionAsync("a").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task SelectingTheSameProxyOrNoneAgainDoesNotRequireReconnect()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSelectTransportProxy, ["a", "direct"], "a", Json(reference)).ConfigureAwait(false)).Ok);
        Assert.True((await ExecuteAsync(IpcContract.OpSelectTransportProxy, ["a", "direct"], "direct", "null").ConfigureAwait(false)).Ok);

        Assert.Empty(_changed);
    }

    [Fact]
    public async Task AnEditMarksEveryConfigurationUsingThatProxyAndNoOthers()
    {
        var shared = await SaveAsync("shared").ConfigureAwait(false);
        var other = await SaveAsync("other").ConfigureAwait(false);
        await _catalog.SelectAsync("a", shared).ConfigureAwait(false);
        await _catalog.SelectAsync("b", shared).ConfigureAwait(false);
        await _catalog.SelectAsync("direct", other).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a", "b", "direct"], shared.Id,
            Json(Proxy("shared") with { Host = "changed.example.net" })).ConfigureAwait(false)).Ok);

        Assert.Equal(["a", "b"], _changed);
    }

    [Fact]
    public async Task RenamingTheSelectedProxyDoesNotChangeItsTransport()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a"], reference.Id,
            Json(Proxy("proxy") with { Name = "New label", Updated = DateTimeOffset.UtcNow })).ConfigureAwait(false)).Ok);

        Assert.Empty(_changed);
    }

    [Fact]
    public async Task HostAndPinCaseDoNotChangeTheConnection()
    {
        var proxy = Proxy("proxy") with { Tls = new OfferedProxyTls("proxy.example.net", new string('a', 64)) };
        var reference = await _catalog.SaveLocalAsync(null, proxy).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a"], reference.Id,
            Json(proxy with { Host = "PROXY.example.NET", Tls = proxy.Tls with { ServerName = "PROXY.example.NET", Pin = new string('A', 64) } })).ConfigureAwait(false)).Ok);

        Assert.Empty(_changed);
    }

    [Fact]
    public async Task ChangingTheSelectedXhttpProfileRequiresReconnect()
    {
        var proxy = Proxy("proxy") with
        {
            Transport = "xhttp",
            Auth = new OfferedProxyAuth("vless", Guid.NewGuid().ToString("D")),
            Xhttp = new OfferedXHttp("packet-up", "GET", "header"),
        };
        var reference = await _catalog.SaveLocalAsync(null, proxy).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a"], reference.Id,
            Json(proxy with { Xhttp = new OfferedXHttp("packet-up", "GET", "header", UplinkChunkSize: 1024) })).ConfigureAwait(false)).Ok);

        Assert.Equal(["a"], _changed);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("tls")]
    [InlineData("pin")]
    [InlineData("credential")]
    [InlineData("transport")]
    public async Task ChangingTheSelectedConnectionParametersRequiresReconnect(string field)
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        var proxy = Proxy("proxy");
        var updated = field switch
        {
            "host" => proxy with { Host = "new.example.net" },
            "port" => proxy with { Port = 8443 },
            "path" => proxy with { Path = "new-path" },
            "tls" => proxy with { Tls = proxy.Tls with { ServerName = "new.example.net" } },
            "pin" => proxy with { Tls = proxy.Tls with { Pin = new string('a', 64) } },
            "credential" => proxy with { Auth = proxy.Auth with { Credential = "new-token" } },
            "transport" => proxy with { Transport = "xhttp", Auth = new OfferedProxyAuth("vless", Guid.NewGuid().ToString("D")), Xhttp = new OfferedXHttp("packet-up", "GET", "header") },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.True((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a"], reference.Id, Json(updated)).ConfigureAwait(false)).Ok);

        Assert.Equal(["a"], _changed);
    }

    [Fact]
    public async Task RemovingTheSelectedProxyRequiresReconnectWithoutChoosingDirect()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.True((await ExecuteAsync(IpcContract.OpRemoveTransportProxy, ["a", "direct"], Json(reference)).ConfigureAwait(false)).Ok);

        Assert.Equal(["a"], _changed);
        Assert.Equal(reference, (await _catalog.SelectionAsync("a").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task ARejectedEditDoesNotRequireReconnect()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        Assert.False((await ExecuteAsync(IpcContract.OpSaveTransportProxy, ["a"], reference.Id,
            Json(Proxy("proxy") with { Port = 65536 })).ConfigureAwait(false)).Ok);

        Assert.Empty(_changed);
    }

    [Fact]
    public async Task ATransportProbeDoesNotRequireReconnect()
    {
        var reference = await SaveAsync("proxy").ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);

        var ack = await TransportProxyCommands.TryExecuteAsync(_store,
            new IpcCommand(IpcContract.OpProbeTransportProxy, [Json(reference)]),
            probe: (_, _) => Task.FromResult(new TransportProxyProbeResult(TransportProxyAvailability.Unavailable)),
            running: ["a"], changed: _changed.Add).ConfigureAwait(false);

        Assert.True(ack!.Ok);
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task AChangedLegacyFrontRequiresReconnect()
    {
        var original = new ConfigTransport("a", true, WebSocketHost: "wss://old.example.net:443/path");
        await _store.SetConfigTransportAsync(original).ConfigureAwait(false);
        await _catalog.LegacySettingsAsync("a", null, original, explicitChoice: true).ConfigureAwait(false);
        var changes = await TransportProxyChanges.CaptureAsync(_store, ["a", "direct"]).ConfigureAwait(false);
        var updated = original with { WebSocketHost = "wss://new.example.net:443/path" };
        await _store.SetConfigTransportAsync(updated).ConfigureAwait(false);

        await _catalog.LegacySettingsAsync("a", original, updated, explicitChoice: true).ConfigureAwait(false);

        Assert.Equal(["a"], await changes.ChangedAsync().ConfigureAwait(false));
    }

    private Task<ProxyReference> SaveAsync(string id) => _catalog.SaveLocalAsync(null, Proxy(id));

    private async Task<IpcAck> ExecuteAsync(string op, IReadOnlyList<string> running, params string[] args) =>
        (await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(op, args),
            running: running, changed: _changed.Add).ConfigureAwait(false))!;

    private static OfferedProxy Proxy(string id) => new(id, "Proxy " + id, "websocket", "proxy.example.net", 443, "relay",
        new OfferedProxyTls("proxy.example.net", ""), new OfferedProxyAuth("bearer", "test-token"), DateTimeOffset.MinValue);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, IpcJson.Options);
}
