using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AmneziaGeo.Tests;

/// <summary>
/// A panel on the loopback for the tests: answers every request over TLS with one body, under a certificate that
/// chains through an issuer to a root made for it.
/// </summary>
internal sealed class TlsPanel : IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly SslStreamCertificateContext _context;
    private readonly int _status;
    private readonly string _body;
    private volatile bool _disposed;
    private int _served;

    /// <summary>
    /// ctor
    /// </summary>
    public TlsPanel(string name = "localhost", string body = "", int status = 200)
    {
        _body = body;
        _status = status;
        var now = DateTimeOffset.UtcNow;

        using var rootKey = RSA.Create(2048);
        var rootAsk = new CertificateRequest("CN=AmneziaGeo Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootAsk.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootAsk.PublicKey, false));
        using var root = rootAsk.CreateSelfSigned(now.AddDays(-2), now.AddDays(30));

        using var issuerKey = RSA.Create(2048);
        var issuerAsk = new CertificateRequest("CN=AmneziaGeo Test Issuer", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        issuerAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        issuerAsk.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerAsk.PublicKey, false));
        issuerAsk.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        using var issuerBare = issuerAsk.Create(root, now.AddDays(-1), now.AddDays(20), Serial());
        using var issuer = issuerBare.CopyWithPrivateKey(issuerKey);

        using var leafKey = RSA.Create(2048);
        var leafAsk = new CertificateRequest($"CN={name}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        leafAsk.CertificateExtensions.Add(names.Build());
        leafAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafAsk.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuth)], false));
        leafAsk.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var leafBare = leafAsk.Create(issuer, now.AddDays(-1), now.AddDays(10), Serial());
        using var leafKeyed = leafBare.CopyWithPrivateKey(leafKey);
        var leaf = X509CertificateLoader.LoadPkcs12(leafKeyed.Export(X509ContentType.Pfx), null);

        Root = X509CertificateLoader.LoadCertificate(root.RawData);
        Pin = Convert.ToHexStringLower(SHA256.HashData(leaf.RawData));
        _context = SslStreamCertificateContext.Create(leaf, [X509CertificateLoader.LoadCertificate(issuerBare.RawData)], offline: true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true, Name = "tls panel" }.Start();
    }

    /// <summary>
    /// Port the panel listens on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The root the certificate of the panel chains to, without its key.
    /// </summary>
    public X509Certificate2 Root { get; }

    /// <summary>
    /// The SHA-256 of the certificate the panel answers under.
    /// </summary>
    public string Pin { get; }

    /// <summary>
    /// Requests the panel has answered so far.
    /// </summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>
    /// Returns the address of a path of the panel under the name localhost.
    /// </summary>
    public string Url(string path) => $"https://localhost:{Port}{path}";

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        Root.Dispose();
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;

        return serial;
    }

    private void Accept()
    {
        while (!_disposed)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                new Thread(() => Serve(client)) { IsBackground = true, Name = "tls panel request" }.Start();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    // One request: TLS, the header with its body, the answer.
    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                using var tls = new SslStream(client.GetStream());
                tls.AuthenticateAsServer(new SslServerAuthenticationOptions { ServerCertificateContext = _context });
                Read(tls);
                Interlocked.Increment(ref _served);
                var body = Encoding.UTF8.GetBytes(_body);
                tls.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {_status} Answer\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                tls.Write(body);
                tls.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or ObjectDisposedException)
        {
        }
    }

    // Reads a request to the end of its body.
    private static void Read(Stream stream)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (stream.Read(one, 0, 1) == 0)
            {
                return;
            }

            header.Append((char)one[0]);
        }

        var length = 0;
        foreach (var line in header.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var body = new byte[length];
        stream.ReadExactly(body);
    }
}
