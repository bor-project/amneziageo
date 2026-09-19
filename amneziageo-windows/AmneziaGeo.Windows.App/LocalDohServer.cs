using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Serves the name proxy over HTTPS on this machine, so lookups still reach it where plain 53 is taken by
/// another program.
/// </summary>
internal sealed class LocalDohServer : IDisposable
{
    private const int Port = 443;
    private const string PathPart = "/dns-query";
    private const string Subject = "CN=AmneziaGeo Local Resolver";
    private const string AppId = "{b0f0f0a1-5a2d-4d0e-9a1d-0f4a2b6c8d31}";

    private readonly IPAddress _address;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _stopping;
    private bool _bound;

    /// <summary>
    /// ctor
    /// </summary>
    public LocalDohServer(IPAddress address, ILogger logger)
    {
        _address = address;
        _logger = logger;
    }

    /// <summary>
    /// The template the system is told to ask this resolver by.
    /// </summary>
    public string Template => $"https://{_address}{PathPart}";

    /// <summary>
    /// The address this resolver answers on.
    /// </summary>
    public IPAddress Address => _address;

    /// <summary>
    /// Opens the endpoint and answers queries with the given resolver; tells whether it listens.
    /// </summary>
    public bool Start(Func<byte[], CancellationToken, Task<byte[]?>> answer)
    {
        try
        {
            var certificate = Certificate();
            if (!Bind(certificate.Thumbprint))
            {
                return false;
            }

            _listener.Prefixes.Add($"https://{_address}:{Port}/");
            _listener.Start();
            _stopping = new CancellationTokenSource();
            _ = Task.Run(() => ServeAsync(answer, _stopping.Token));
            _logger.LogInformation("names are also served over HTTPS on {Template}", Template);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "names could not be served over HTTPS on {Address}", _address);
            Release();
            return false;
        }
    }

    private async Task ServeAsync(Func<byte[], CancellationToken, Task<byte[]?>> answer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var context = default(HttpListenerContext);
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "the HTTPS resolver stopped taking lookups");
                }

                return;
            }

            _ = AnswerAsync(context, answer, ct);
        }
    }

    private async Task AnswerAsync(HttpListenerContext context, Func<byte[], CancellationToken, Task<byte[]?>> answer, CancellationToken ct)
    {
        try
        {
            var query = await QueryAsync(context.Request, ct).ConfigureAwait(false);
            if (query is null)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var reply = await answer(query, ct).ConfigureAwait(false);
            if (reply is null)
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = LocalDohWire.MessageType;
            context.Response.ContentLength64 = reply.Length;
            await context.Response.OutputStream.WriteAsync(reply, ct).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "an HTTPS lookup was not answered");
            try
            {
                context.Response.Abort();
            }
            catch (Exception inner)
            {
                _logger.LogTrace(inner, "the HTTPS response could not be dropped");
            }
        }
    }

    private static async Task<byte[]?> QueryAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return LocalDohWire.FromBase64Url(request.QueryString["dns"]);
        }

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (buffer.Length <= LocalDohWire.MaxQueryBytes)
        {
            var read = await request.InputStream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var query = buffer.ToArray();
        return query.Length is > 0 and <= LocalDohWire.MaxQueryBytes ? query : null;
    }

    // Keeps one self-signed certificate for this endpoint in the machine store and trusts it there.
    private X509Certificate2 Certificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var held in store.Certificates)
        {
            if (string.Equals(held.Subject, Subject, StringComparison.OrdinalIgnoreCase)
                && held.HasPrivateKey
                && held.NotAfter > DateTime.Now.AddDays(30)
                && held.MatchesHostname(_address.ToString()))
            {
                return held;
            }
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(_address);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var password = Guid.NewGuid().ToString("N");
        var kept = X509CertificateLoader.LoadPkcs12(
            made.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(kept);
        Trust(kept);
        return kept;
    }

    // Puts the certificate where a chain check on this machine finds it.
    private void Trust(X509Certificate2 certificate)
    {
        try
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadWrite);
            using var shown = X509CertificateLoader.LoadCertificate(certificate.RawData);
            root.Add(shown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the certificate of the HTTPS resolver is not trusted on this machine, so the system may refuse it");
        }
    }

    // Hands the port to this certificate, unless another program already holds it.
    private bool Bind(string thumbprint)
    {
        var held = Netsh($"http show sslcert ipport={_address}:{Port}");
        if (held.Contains(_address.ToString(), StringComparison.Ordinal)
            && !held.Contains(AppId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("{Address}:{Port} is already held by another program, so names are not served over HTTPS", _address, Port);
            return false;
        }

        Netsh($"http delete sslcert ipport={_address}:{Port}");
        var added = Netsh($"http add sslcert ipport={_address}:{Port} certhash={thumbprint} appid={AppId} certstorename=MY");
        _bound = true;
        if (added.Contains("SSL Certificate successfully added", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.LogWarning("{Address}:{Port} did not take the certificate: {Answer}", _address, Port, added.Trim());
        return false;
    }

    private void Release()
    {
        try
        {
            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "the HTTPS resolver was already stopping");
        }

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "the HTTPS resolver was already closed");
        }

        if (_bound)
        {
            Netsh($"http delete sslcert ipport={_address}:{Port}");
            _bound = false;
        }
    }

    private string Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(6000);
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "netsh {Arguments} did not run", arguments);
            return string.Empty;
        }
    }

    /// <summary>
    /// Closes the endpoint and hands the port back.
    /// </summary>
    public void Dispose()
    {
        Release();
    }
}
