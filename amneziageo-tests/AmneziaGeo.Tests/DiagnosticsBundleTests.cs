using System.IO.Compression;
using AmneziaGeo.Dal;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The archive handed to support: what it must never carry out of the machine, and what it must carry in.
/// A key or a password leaking here leaves the user's control the moment the file is sent.
/// </summary>
public sealed class DiagnosticsBundleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-diag-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _logPath;
    private SqliteStateStore _store = null!;
    private SqliteLogStore _logs = null!;

    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsBundleTests()
    {
        _dbPath = Path.Combine(_root, "state.db");
        _logPath = Path.Combine(_root, "log.db");
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        _logs = new SqliteLogStore(_logPath);
        await _logs.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _logs.Dispose();
        _store.ClearPool();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void PrivateAndPresharedKeys_AreMasked()
    {
        var text = DiagnosticsBundle.Redact("PrivateKey = aBcD1234\nPresharedKey = zZzZ9999\nPublicKey = keepMe");

        Assert.DoesNotContain("aBcD1234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zZzZ9999", text, StringComparison.Ordinal);
        Assert.Contains("keepMe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsInsideAUrl_AreMasked()
    {
        var text = DiagnosticsBundle.Redact("wss://bob:hunter2@vpn.example.com/secretpath");

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secretpath", text, StringComparison.Ordinal);
        Assert.Contains("vpn.example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheArchive_CarriesTheSummaryAndBothLogs()
    {
        await _store.SaveConfigAsync("srv", "[Interface]\nPrivateKey = topSecretKey\n");
        _logs.AppendAgent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5, "agent", "PrivateKey = anotherSecret");
        await _logs.FlushAsync();

        var bundle = new DiagnosticsBundle(_store, _logs);
        var path = await bundle.WriteAsync(
            Path.Combine(_root, "out"),
            "header\n",
            row => row.Message,
            new BundleSources(Runtime: _ => Task.FromResult("PrivateKey = topSecretKey"), Cache: _ => Task.FromResult("1.2.3.4 proxy")));

        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("summary.txt"));
        Assert.NotNull(zip.GetEntry("config.txt"));
        Assert.NotNull(zip.GetEntry("cache.txt"));
        Assert.NotNull(zip.GetEntry("checks.log"));
        Assert.NotNull(zip.GetEntry("ageo.log"));
        Assert.NotNull(zip.GetEntry("routes.log"));

        using var reader = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains("srv", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunsOfTheChecks_TravelInTheArchive()
    {
        _logs.AppendCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "channel check for \"srv\"\n  verdict   nothing to blame");
        await _logs.FlushAsync();

        var bundle = new DiagnosticsBundle(_store, _logs);
        var path = await bundle.WriteAsync(Path.Combine(_root, "out"), "header\n", row => row.Message);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("checks.log")!.Open());
        var text = await reader.ReadToEndAsync();

        Assert.Contains("nothing to blame", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConfigurationInTheArchive_CarriesNoKeys()
    {
        var bundle = new DiagnosticsBundle(_store, _logs);
        var path = await bundle.WriteAsync(
            Path.Combine(_root, "out"),
            "header\n",
            row => row.Message,
            new BundleSources(Runtime: _ => Task.FromResult("PrivateKey = topSecretKey")));

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("config.txt")!.Open());
        var text = await reader.ReadToEndAsync();

        Assert.DoesNotContain("topSecretKey", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeysInTheLogRows_AreMaskedInsideTheArchive()
    {
        _logs.AppendAgent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5, "agent", "PrivateKey = topSecretKey");
        await _logs.FlushAsync();

        var bundle = new DiagnosticsBundle(_store, _logs);
        var path = await bundle.WriteAsync(Path.Combine(_root, "out"), "header\n", row => row.Message);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("ageo.log")!.Open());
        var text = await reader.ReadToEndAsync();

        Assert.DoesNotContain("topSecretKey", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }
}
