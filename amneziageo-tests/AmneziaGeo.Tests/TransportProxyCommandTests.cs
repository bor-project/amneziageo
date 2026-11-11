using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyCommandTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ag-proxy-commands-" + Guid.NewGuid().ToString("N") + ".db");
    private SqliteStateStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
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
    public async Task PublicCatalogOmitsCredentialsAndLocalGetReturnsTheRequestedDefinition()
    {
        var proxy = new OfferedProxy("draft", "Local", "websocket", "proxy.example.net", 443, "relay",
            new OfferedProxyTls("proxy.example.net", ""), new OfferedProxyAuth("bearer", "private-credential"), DateTimeOffset.MinValue);
        var saved = await Execute(IpcContract.OpSaveTransportProxy, "", JsonSerializer.Serialize(proxy, IpcJson.Options)).ConfigureAwait(false);
        Assert.True(saved.Ok);
        var listed = await Execute(IpcContract.OpListTransportProxies).ConfigureAwait(false);
        Assert.True(listed.Ok);
        Assert.DoesNotContain("private-credential", listed.Message);
        var local = await Execute(IpcContract.OpGetTransportProxy, saved.Message).ConfigureAwait(false);
        Assert.True(local.Ok);
        Assert.Contains("private-credential", local.Message);
        Assert.True((await Execute(IpcContract.OpRemoveTransportProxy, saved.Message).ConfigureAwait(false)).Ok);
        Assert.False((await Execute(IpcContract.OpGetTransportProxy, saved.Message).ConfigureAwait(false)).Ok);
    }

    [Fact]
    public async Task BadReferencesAndDefinitionsReturnErrorsWithoutChangingTheCatalog()
    {
        Assert.False((await Execute(IpcContract.OpGetTransportProxy, "null").ConfigureAwait(false)).Ok);
        Assert.False((await Execute(IpcContract.OpSaveTransportProxy, "", "{}").ConfigureAwait(false)).Ok);
        Assert.False((await Execute(IpcContract.OpSelectTransportProxy, "unknown", "null").ConfigureAwait(false)).Ok);
        Assert.Equal(0, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
        Assert.Null(await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand("unrelated", [])).ConfigureAwait(false));
    }

    private async Task<IpcAck> Execute(string op, params string[] args) =>
        (await TransportProxyCommands.TryExecuteAsync(_store, new IpcCommand(op, args)).ConfigureAwait(false))!;
}
