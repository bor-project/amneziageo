using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TransportProxyStateStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-proxy-state-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
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
    public async Task AdditiveInitializationPreservesConfigurationsKeysAndTransport()
    {
        var text = """
                   [Interface]
                   PrivateKey = legacy-private-key
                   Address = 10.8.0.2/32

                   [Peer]
                   PublicKey = legacy-server-key
                   PresharedKey = legacy-preshared-key
                   Endpoint = vpn.example.net:51820
                   AllowedIPs = 0.0.0.0/0
                   """;
        var transport = new ConfigTransport("retained", true, 1392, true, MtuMode.Custom,
            UseRouter: false, AllowInbound: true, InboundNetwork: true, UseRouting: false,
            WebSocketHost: "wss://user:password@relay.example.net:8443/path", WebSocketPort: 8443,
            AllowSignal: false, HoldTraffic: false);
        await _store.SaveConfigAsync("retained", text).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(transport).ConfigureAwait(false);
        await _store.SetSettingAsync("retained-setting", "retained-value").ConfigureAwait(false);
        var version = await UserVersionAsync().ConfigureAwait(false);
        await ExecuteSqlAsync("DROP TABLE transport_proxy_state;").ConfigureAwait(false);

        await _store.InitializeAsync().ConfigureAwait(false);

        Assert.Equal(1L, version);
        Assert.Equal(version, await UserVersionAsync().ConfigureAwait(false));
        Assert.Equal(text, await _store.GetConfigTextAsync("retained").ConfigureAwait(false));
        Assert.Equal(transport, await _store.GetConfigTransportAsync("retained").ConfigureAwait(false));
        Assert.Equal("retained-value", await _store.GetSettingAsync("retained-setting").ConfigureAwait(false));
        Assert.Equal(0, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task APositiveExpectedRevisionDoesNotCreateAMissingCatalog()
    {
        Assert.False(await _store.TrySaveTransportProxyStateAsync(State("missing", 8), 7).ConfigureAwait(false));

        var held = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Equal(0, held.Revision);
        Assert.Empty(held.Selections);
    }

    [Fact]
    public async Task AStaleWriterDoesNotReplaceTheCurrentAggregate()
    {
        var another = new SqliteStateStore(_path);
        var first = State("first");
        Assert.True(await _store.TrySaveTransportProxyStateAsync(first, 0).ConfigureAwait(false));
        Assert.False(await another.TrySaveTransportProxyStateAsync(State("stale"), 0).ConfigureAwait(false));
        Assert.Equal(first.ToPayload(), (await another.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());

        var next = first with { Revision = 2, Selections = [new TransportProxySelection("second", null)] };
        Assert.True(await another.TrySaveTransportProxyStateAsync(next, 1).ConfigureAwait(false));
        Assert.False(await _store.TrySaveTransportProxyStateAsync(first with { Revision = 2 }, 1).ConfigureAwait(false));
        Assert.Equal(next.ToPayload(), (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task IndependentConnectionsHaveOneWinnerAtTheSameRevision()
    {
        var another = new SqliteStateStore(_path);
        var states = new[] { State("one"), State("two") };
        var writers = new[] { _store, another };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = writers.Select((writer, index) => Task.Run(async () =>
        {
            await ready.Task.ConfigureAwait(false);
            return await writer.TrySaveTransportProxyStateAsync(states[index], 0).ConfigureAwait(false);
        })).ToArray();
        ready.SetResult(true);

        var outcomes = await Task.WhenAll(attempts).ConfigureAwait(false);

        Assert.Single(outcomes.Where(outcome => outcome));
        var expected = outcomes[0] ? states[0] : states[1];
        Assert.Equal(expected.ToPayload(), (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        Assert.Equal(expected.ToPayload(), (await another.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task RestartAndLegacyTransportEditsPreserveTheAggregateAndExplicitNone()
    {
        var source = Guid.NewGuid().ToString("D");
        var reference = new ProxyReference(source, "local-one");
        var proxy = new OfferedProxy("local-one", "local", "websocket", "relay.example.net", 8443, "relay",
            new OfferedProxyTls("relay.example.net", ""), new OfferedProxyAuth("bearer", "test-credential"),
            new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        var record = new CatalogTransportProxy(reference, proxy, Local: true);
        var state = new TransportProxyState
        {
            Revision = 1,
            LocalSource = source,
            Locals = [record],
            Publications = [new TransportProxyPublication("publisher", "mark", "issuer", source, [record], "server")],
            Trust = [new TransportProxyTrust(source, "signed", "stored-public-key")],
            Selections = [new TransportProxySelection("carried", reference), new TransportProxySelection("direct", null)],
            Retired = [new RetiredTransportProxy(new ProxyReference(source, "gone"), "withdrawn", "websocket", "old.example.net", 443, "old", false)],
        };
        Assert.True(await _store.TrySaveTransportProxyStateAsync(state, 0).ConfigureAwait(false));
        await _store.SetConfigTransportAsync(new ConfigTransport("carried", true, 1380, true, MtuMode.Custom)).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("direct", false)).ConfigureAwait(false);

        await _store.InitializeAsync().ConfigureAwait(false);
        _store.ClearPool();
        var restarted = new SqliteStateStore(_path);
        await restarted.InitializeAsync().ConfigureAwait(false);
        var held = await restarted.GetTransportProxyStateAsync().ConfigureAwait(false);

        Assert.Equal(state.ToPayload(), held.ToPayload());
        Assert.Null(Assert.Single(held.Selections.Where(selection => selection.Config == "direct")).Reference);
        Assert.Equal(1L, await UserVersionAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task MalformedPayloadIsReportedAndRetained()
    {
        await ExecuteSqlAsync("INSERT INTO transport_proxy_state (id, revision, payload) VALUES (1, 1, $payload);", "{").ConfigureAwait(false);

        await Assert.ThrowsAsync<JsonException>(() => _store.GetTransportProxyStateAsync()).ConfigureAwait(false);
        await _store.InitializeAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<JsonException>(() => _store.GetTransportProxyStateAsync()).ConfigureAwait(false);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\":9,\"revision\":1}")]
    [InlineData("{\"revision\":2}")]
    public async Task AnInvalidPayloadOrRevisionIsNotReplacedWithAnEmptyCatalog(string payload)
    {
        await ExecuteSqlAsync("INSERT INTO transport_proxy_state (id, revision, payload) VALUES (1, 1, $payload);", payload).ConfigureAwait(false);

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetTransportProxyStateAsync()).ConfigureAwait(false);
    }

    [Theory]
    [InlineData(-1L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(0L, 2L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task InvalidRevisionAdvancesDoNotWrite(long expected, long proposed)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.TrySaveTransportProxyStateAsync(State("invalid", proposed), expected)).ConfigureAwait(false);

        Assert.Equal(0, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    private static TransportProxyState State(string config, long revision = 1) => new()
    {
        Revision = revision,
        LocalSource = Guid.NewGuid().ToString("D"),
        Selections = [new TransportProxySelection(config, null)],
    };

    private async Task ExecuteSqlAsync(string sql, string? payload = null)
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                if (payload is not null)
                {
                    command.Parameters.AddWithValue("$payload", payload);
                }

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<long> UserVersionAsync()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "PRAGMA user_version;";
                return Assert.IsType<long>(await command.ExecuteScalarAsync().ConfigureAwait(false));
            }
        }
    }
}
