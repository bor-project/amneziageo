using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Identifies a proxy in the common catalog.
/// </summary>
public sealed record ProxyReference(string Source, string Id);

/// <summary>
/// Describes the TLS identity of a proxy.
/// </summary>
public sealed record OfferedProxyTls(string ServerName, string Pin, bool Enabled = true);

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
    string XPaddingBytes = "100-100",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scheme = null, int Version = 0);

/// <summary>
/// Describes a public transport proxy.
/// </summary>
public sealed record OfferedProxy(
    string Id, string Name, string Transport, string Host, int Port, string Path,
    OfferedProxyTls Tls, OfferedProxyAuth Auth, DateTimeOffset Updated, OfferedXHttp? Xhttp = null, ProxyTargets? Targets = null,
    OfferedProxyHttp? Http = null, OfferedProxyQuic? Quic = null);

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
            if (catalog is null || catalog.Version is not (1 or 2) || !Guid.TryParse(catalog.Source, out var source) || source == Guid.Empty
                || catalog.Items is null || catalog.Items.Count > 256)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proxy in catalog.Items)
            {
                if (proxy is null || !Valid(proxy) || catalog.Version == 1 && proxy.Transport is not ("websocket" or "xhttp")
                    || catalog.Version == 2 && (proxy.Transport == "xhttp" || !ExplicitTls(body, seen.Count)) || !seen.Add(proxy.Id))
                {
                    return null;
                }
            }

            var normalized = catalog with { Source = source.ToString("D"), Items = [.. catalog.Items.Select(ProxyTransportModes.Normalize)] };
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

    private static bool ExplicitTls(JsonElement body, int index) =>
        body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && index < items.GetArrayLength()
        && items[index].TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.Object
        && tls.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False;

    public static bool Valid(OfferedProxy proxy) =>
        proxy.Id is { Length: > 0 and <= 64 }
        && proxy.Id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
        && proxy.Name is { Length: > 0 and <= 64 }
        && !string.IsNullOrWhiteSpace(proxy.Name)
        && proxy.Host is { Length: > 0 and <= 253 }
        && Uri.CheckHostName(proxy.Host) != UriHostNameType.Unknown
        && proxy.Port is >= 1 and <= 65535
        && proxy.Path is { Length: > 0 and <= 64 }
        && proxy.Path.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
        && ProxyTargetRules.Valid(proxy.Targets)
        && ProxyTransportModes.Valid(proxy);
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
