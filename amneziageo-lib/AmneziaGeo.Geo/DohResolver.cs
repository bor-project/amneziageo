using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Asks a DNS over HTTPS resolver, dialling it by address so no name has to be resolved first.
/// </summary>
public sealed class DohResolver : IDisposable
{
    /// <summary>
    /// The resolver asked where none was named.
    /// </summary>
    public const string DefaultUrl = "https://cloudflare-dns.com/dns-query";

    private const string WireType = "application/dns-message";

    private readonly HttpClient _client;
    private readonly Uri _url;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public DohResolver(string url, IPAddress address, TimeSpan timeout, Action<Socket>? bind = null)
    {
        _url = new Uri(url);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    bind?.Invoke(socket);
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception)
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        _client = new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// The address the resolver named by default is dialled at.
    /// </summary>
    public static IPAddress DefaultAddress { get; } = IPAddress.Parse("1.1.1.1");

    /// <summary>
    /// The name the resolver is asked under.
    /// </summary>
    public string Endpoint => _url.Host;

    /// <summary>
    /// Sends one query as it stands and returns the answer as it came.
    /// </summary>
    public async Task<byte[]> AskAsync(byte[] query, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new MediaTypeHeaderValue(WireType);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(WireType));
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the connections the resolver holds.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }
}
