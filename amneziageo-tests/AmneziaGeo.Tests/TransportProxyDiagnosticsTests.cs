using System.IO.Compression;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Checks transport proxy support archives.
/// </summary>
public sealed class TransportProxyDiagnosticsTests : IAsyncLifetime
{
    private const string Secret = "private-proxy-token";
    private const string User = "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ageo-proxy-diag-" + Guid.NewGuid().ToString("N"));
    private SqliteStateStore _store = null!;
    private SqliteLogStore _logs = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteStateStore(Path.Combine(_root, "state.db"));
        await _store.InitializeAsync();
        _logs = new SqliteLogStore(Path.Combine(_root, "logs.db"));
        await _logs.InitializeAsync();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _logs.Dispose();
        _logs.ClearPool();
        _store.ClearPool();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("Authorization: Bearer " + Secret)]
    [InlineData("Sec-WebSocket-Protocol: authorization.bearer." + Secret)]
    [InlineData("credential=" + Secret)]
    [InlineData("token: " + Secret)]
    [InlineData("{\"auth\":{\"scheme\":\"bearer\",\"credential\":\"" + Secret + "\"}}")]
    [InlineData("{\"auth\":{\"\\u0063redential\":\"" + Secret + "\"}}")]
    public void TransportTokens_AreMasked(string text)
    {
        var redacted = DiagnosticsBundle.Redact(text);

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vless://" + User + "@cdn.example.com:443?t=packet-up")]
    [InlineData("{\"auth\":{\"scheme\":\"vless\",\"credential\":\"" + User + "\"},\"host\":\"cdn.example.com\"}")]
    [InlineData("{\"users\":[{\"id\":\"" + User + "\",\"encryption\":\"none\"}],\"address\":\"cdn.example.com\"}")]
    public void VlessCredentials_AreMaskedWhileTheHostRemains(string text)
    {
        var redacted = DiagnosticsBundle.Redact(text);

        Assert.DoesNotContain(User, redacted, StringComparison.Ordinal);
        Assert.Contains("cdn.example.com", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicSourceAndTlsIdentity_RemainVisible()
    {
        var text = """{"source":"bcb4b9ad-00bc-4c2f-b32b-4655a1655a02","tls":{"serverName":"cdn.example.com","pin":"abcd"},"publicKey":"public-material"}""";

        Assert.Equal(text, DiagnosticsBundle.Redact(text));
    }

    [Fact]
    public async Task Summary_ReportsTheExplicitProxyAndNoneWithoutTheOldSwitch()
    {
        await _store.SaveConfigAsync("proxied", Config());
        await _store.SaveConfigAsync("direct", Config());
        await _store.SetConfigTransportAsync(new ConfigTransport("proxied", false));
        await _store.SetConfigTransportAsync(new ConfigTransport("direct", true));
        var catalog = TransportProxyCatalog.For(_store);
        var reference = await catalog.SaveLocalAsync(null, new OfferedProxy("draft", "CDN", "xhttp", "cdn.example.com", 443, "tunnel",
            new OfferedProxyTls("cdn.example.com", ""), new OfferedProxyAuth("vless", User), DateTimeOffset.MinValue,
            new OfferedXHttp("packet-up", "GET", "header")));
        await catalog.SelectAsync("proxied", reference);
        await catalog.SelectAsync("direct", null);
        await catalog.RemoveLocalAsync(reference);

        var path = await new DiagnosticsBundle(_store, _logs).WriteAsync(Path.Combine(_root, "out"), "", row => row.Message);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains("transport:  proxy " + reference.Source + "/" + reference.Id, summary, StringComparison.Ordinal);
        Assert.Contains("transport:  none (plain UDP)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("websocket:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(User, summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheArchive_MasksTransportCredentialsInRuntimeAndLogs()
    {
        var payload = JsonSerializer.Serialize(new { auth = new { scheme = "vless", credential = User }, host = "cdn.example.com" });
        _logs.AppendAgent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5, "transport", "Authorization: Bearer " + Secret);
        await _logs.FlushAsync();
        var path = await new DiagnosticsBundle(_store, _logs).WriteAsync(Path.Combine(_root, "out"), "", row => row.Message,
            new BundleSources(Runtime: _ => Task.FromResult(payload)));

        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain(User, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":9,\"revision\":1,\"credential\":\"" + Secret + "\"}")]
    public async Task AnUnreadableCatalog_ProducesADiagnosticWithoutChangingItsPayload(string payload)
    {
        await _store.SaveConfigAsync("proxied", Config());
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state.db") }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO transport_proxy_state (id, revision, payload) VALUES (1, 1, $payload);";
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }

        var path = await new DiagnosticsBundle(_store, _logs).WriteAsync(Path.Combine(_root, "out"), "", row => row.Message);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains("transport:  catalog-unreadable", summary, StringComparison.Ordinal);
        Assert.Contains("proxied", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("plain UDP", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, summary, StringComparison.Ordinal);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT payload FROM transport_proxy_state WHERE id = 1;";
        Assert.Equal(payload, await check.ExecuteScalarAsync());
    }

    private static string Config() => "[Interface]\nPrivateKey = client-private\n[Peer]\nPublicKey = server-public\nEndpoint = vpn.example:51820\n";
}
