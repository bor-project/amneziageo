using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class TunnelProxyFailureBoundaryTests
{
    [Theory]
    [InlineData("proxy-catalog-busy", ConnectFailureReason.UnderlayUnreachable)]
    [InlineData("proxy-platform-unsupported", ConnectFailureReason.TransportRejected)]
    public async Task ResolvingTheCatalogPreservesTheActualFailureCategory(string code, ConnectFailureReason reason)
    {
        var store = DispatchProxy.Create<IStateStore, FaultStore>();
        ((FaultStore)store).Code = code;
        var runner = Runner(store);
        var error = await Assert.ThrowsAsync<ConnectFailureException>(() => InvokeAsync(runner, "ResolveProxyAsync", ["qa"])).ConfigureAwait(false);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(code, error.Message);
        Assert.IsType<TransportProxyFault>(error.InnerException);
    }

    [Theory]
    [InlineData("schema", ConnectFailureReason.TransportRejected, "bad-proxy")]
    [InlineData("tls", ConnectFailureReason.TransportRejected, "proxy-tls")]
    [InlineData("reset", ConnectFailureReason.UnderlayUnreachable, "proxy-no-answer")]
    public async Task TheTargetCheckPreservesTheActualFailureCategory(string mode, ConnectFailureReason reason, string code)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=front.example", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("front.example");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pin = mode == "tls" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var proxy = ProxyTransportModesTests.Http("GET", true) with { Port = port, Tls = new("front.example", pin, mode != "schema") };
        var selected = new ResolvedTransportProxy(new CatalogTransportProxy(new(Guid.NewGuid().ToString("D"), proxy.Id), proxy), "qa", "udp.example", 51820);
        var serving = mode == "schema" ? Task.CompletedTask : ResetAsync(listener, certificate, deadline.Token);
        var runner = Runner(null!);
        var error = await Assert.ThrowsAsync<ConnectFailureException>(() => InvokeAsync(runner, "StartCarrierAsync",
            ["qa", selected, null, IPAddress.Loopback, false, "", "", deadline.Token])).ConfigureAwait(false);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(code, error.Message);
        await serving.ConfigureAwait(false);
        if (mode == "schema")
        {
            Assert.False(listener.Pending());
        }
    }

    private static async Task ResetAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken ct)
    {
        try
        {
            using var peer = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var tls = new SslStream(peer.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, ct).ConfigureAwait(false);
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await tls.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                {
                    return;
                }

                header.Append((char)one[0]);
            }
        }
        catch (Exception error) when (error is IOException or AuthenticationException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static TunnelRunner Runner(IStateStore store) => new(store, null!, null!, null!, null!, null!, null!, null!, null!, null!,
        NullLoggerFactory.Instance, NullLogger<TunnelRunner>.Instance);

    internal static Task InvokeAsync(TunnelRunner runner, string method, object?[] arguments) =>
        (Task)typeof(TunnelRunner).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runner, arguments)!;

    public class FaultStore : DispatchProxy
    {
        public string Code { get; set; } = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new TransportProxyFault(Code);
    }
}
