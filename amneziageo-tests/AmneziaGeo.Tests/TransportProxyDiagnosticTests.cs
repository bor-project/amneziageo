using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using Xunit;

namespace AmneziaGeo.Tests;

[Collection("Proxy diagnostic localization")]
public sealed class TransportProxyDiagnosticTests
{
    [Fact]
    public void OlderResultsKeepTheirCodeAndRetryPolicy()
    {
        var result = JsonSerializer.Deserialize<TransportProxyProbeResult>("{\"availability\":2,\"fault\":\"proxy-bad-answer\"}", IpcJson.Options)!;
        Assert.Null(result.Diagnostic);
        Assert.Equal("proxy-bad-answer", result.Fault);
        Assert.Equal(ConnectFailureReason.TransportRejected, TransportProxyProbes.FailureReason(result.Fault));
        Assert.Equal("proxy-bad-answer", new TransportProxyFault(result.Fault).Message);
    }

    [Fact]
    public void StructuredResultsRetainTheReasonInJsonAndExceptions()
    {
        var diagnostic = new TransportProxyDiagnostic("challenge", "http-status", 404);
        var result = new TransportProxyProbeResult(TransportProxyAvailability.Unavailable, "proxy-bad-answer", Diagnostic: diagnostic);
        var restored = JsonSerializer.Deserialize<TransportProxyProbeResult>(JsonSerializer.Serialize(result, IpcJson.Options), IpcJson.Options)!;
        Assert.Equal(result, restored);
        var failure = new TransportProxyFault(result.Fault, diagnostic);
        Assert.Equal("proxy-bad-answer", failure.Code);
        Assert.Equal((failure.Code, diagnostic), TransportProxyDiagnostic.Decode(failure.Message));
        Assert.Equal(ConnectFailureReason.TransportRejected, TransportProxyProbes.FailureReason(failure.Code));
    }

    [Theory]
    [InlineData("challenge", "Authorization: Bearer secret", "")]
    [InlineData("https://host/?token=secret", "http-status", "")]
    [InlineData("challenge", "missing-header", "secret")]
    public void UnrecognizedDiagnosticsCannotExposeTheirContents(string stage, string reason, string field)
    {
        var diagnostic = new TransportProxyDiagnostic(stage, reason, 404, field);
        Assert.False(diagnostic.Valid);
        Assert.Equal("proxy-bad-answer", diagnostic.Encode("proxy-bad-answer"));
        Assert.Null(TransportProxyDiagnostic.Decode("proxy-bad-answer|" + stage + "|" + reason + "|404|" + field).Diagnostic);
    }

    [Theory]
    [InlineData("certificate-name")]
    [InlineData("certificate-pin")]
    [InlineData("certificate-date")]
    [InlineData("certificate-chain")]
    public async Task TlsRefusalsIdentifyTheCertificateCheck(string reason)
    {
        using var server = new TlsPanel(leafEnds: reason == "certificate-date" ? 0 : 10);
        var proxy = ProxyTransportModesTests.Http("GET", true) with
        {
            Port = server.Port,
            Tls = new(reason == "certificate-name" ? "wrong.example" : "localhost",
                reason == "certificate-pin" ? new string('0', 64) : reason == "certificate-chain" ? "" : server.Pin),
        };
        var record = new CatalogTransportProxy(new(Guid.NewGuid().ToString("D"), "proxy"), proxy);
        var result = await TransportProxyProbes.CheckTargetAsync(new(record, "vpn", "vpn.example", 51820), CancellationToken.None,
            connect: async (_, ct) =>
            {
                var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, server.Port, ct).ConfigureAwait(false);
                return client.GetStream();
            }).ConfigureAwait(false);
        Assert.Equal("proxy-tls", result.Fault);
        Assert.Equal(new TransportProxyDiagnostic("tls", reason), result.Diagnostic);
        Assert.Equal(0, server.Served);
    }

    [Fact]
    public void LocalizedFailureIdentifiesHttpRatherThanBlamingTls()
    {
        var previous = Loc.Instance.Culture.Name;
        try
        {
            Loc.Instance.SetCulture("ru");
            var message = TransportProxyWords.Fault("proxy-bad-answer", new("challenge", "http-status", 404));
            Assert.Contains("404", message, StringComparison.Ordinal);
            Assert.Contains("авторизации", message, StringComparison.Ordinal);
            Assert.DoesNotContain("сертификат", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(message, TransportProxyWords.Fault(new TransportProxyDiagnostic("challenge", "http-status", 404).Encode("proxy-bad-answer")));
            Assert.DoesNotContain("TLS", Loc.Instance.Get("MainVm_NoticeConnectFailed_TransportRejected", message), StringComparison.Ordinal);
        }
        finally
        {
            Loc.Instance.SetCulture(previous);
        }
    }
}

[CollectionDefinition("Proxy diagnostic localization", DisableParallelization = true)]
public sealed class ProxyDiagnosticLocalizationCollection;
