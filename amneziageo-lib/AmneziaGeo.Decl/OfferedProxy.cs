using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Identifies a proxy in the common catalog.
/// </summary>
public sealed record ProxyReference(string Source, string Id);

/// <summary>
/// Describes the TLS identity of a proxy.
/// </summary>
public sealed record OfferedProxyTls(string ServerName, string Pin);

/// <summary>
/// Carries a transport credential.
/// </summary>
public sealed record OfferedProxyAuth(string Scheme, string Credential);

/// <summary>
/// Describes the HTTP requests of an XHTTP proxy.
/// </summary>
public sealed record OfferedXHttp(
    string Mode,
    [property: System.Text.Json.Serialization.JsonPropertyName("uplinkHTTPMethod")] string UplinkHttpMethod,
    string UplinkDataPlacement,
    string HttpVersion = "2",
    int ScMaxEachPostBytes = 2048,
    int ScMinPostsIntervalMs = 1,
    int UplinkChunkSize = 2048,
    string XPaddingBytes = "100-100");

/// <summary>
/// Describes a public transport proxy.
/// </summary>
public sealed record OfferedProxy(
    string Id, string Name, string Transport, string Host, int Port, string Path,
    OfferedProxyTls Tls, OfferedProxyAuth Auth, DateTimeOffset Updated, OfferedXHttp? Xhttp = null);

/// <summary>
/// Describes a complete published proxy catalog.
/// </summary>
public sealed record OfferedProxyCatalog(int Version, string Source, IReadOnlyList<OfferedProxy> Items, OfferedProxySourceProof? Proof = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads a published catalog.
    /// </summary>
    public static OfferedProxyCatalog? Read(JsonElement body)
    {
        try
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var catalog = body.Deserialize<OfferedProxyCatalog>(Json);
            if (catalog is null || catalog.Version != 1 || !Guid.TryParse(catalog.Source, out var source) || source == Guid.Empty
                || catalog.Items is null || catalog.Items.Count > 256)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proxy in catalog.Items)
            {
                if (proxy is null || !Valid(proxy) || !seen.Add(proxy.Id))
                {
                    return null;
                }
            }

            var normalized = catalog with { Source = source.ToString("D") };
            if (body.TryGetProperty("proof", out var proof))
            {
                if (proof.ValueKind != JsonValueKind.Object || normalized.Proof is null
                    || !ProxySourceIdentity.Verify(normalized, normalized.Proof.Issuer, out var publicKey))
                {
                    return null;
                }

                normalized = normalized with { Proof = normalized.Proof with { PublicKey = publicKey } };
            }

            return normalized;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(OfferedProxy proxy) =>
        proxy.Id is { Length: > 0 and <= 64 }
        && proxy.Id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
        && proxy.Name is { Length: > 0 and <= 64 }
        && !string.IsNullOrWhiteSpace(proxy.Name)
        && proxy.Host is { Length: > 0 and <= 253 }
        && Uri.CheckHostName(proxy.Host) != UriHostNameType.Unknown
        && proxy.Port is >= 1 and <= 65535
        && proxy.Path is { Length: > 0 and <= 64 }
        && proxy.Path.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
        && proxy.Tls is { ServerName.Length: > 0, Pin: not null }
        && Uri.CheckHostName(proxy.Tls.ServerName) != UriHostNameType.Unknown
        && (proxy.Tls.Pin.Length == 0 || proxy.Tls.Pin.Length == 64 && proxy.Tls.Pin.All(char.IsAsciiHexDigit))
        && proxy.Auth is { Credential.Length: > 0 and <= 512 }
        && (proxy.Transport == "websocket" && proxy.Auth.Scheme == "bearer"
                && proxy.Auth.Credential.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '.' or '_' or '~' or '+' or '/' or '=')
            || proxy.Transport == "xhttp" && proxy.Auth.Scheme == "vless" && Guid.TryParse(proxy.Auth.Credential, out var user) && user != Guid.Empty
                && proxy.Xhttp is { Mode: "packet-up", UplinkHttpMethod: "GET", UplinkDataPlacement: "header", HttpVersion: "2",
                    ScMaxEachPostBytes: > 0 and <= 2048, ScMinPostsIntervalMs: >= 1 and <= 1000,
                    UplinkChunkSize: > 0 and <= 2048, XPaddingBytes: "100-100" });
}

/// <summary>
/// Reports the result of a transport probe.
/// </summary>
public enum TransportProxyAvailability
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2,
}
