using System.Globalization;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Describes a rejected proxy operation.
/// </summary>
public sealed record TransportProxyDiagnostic(string Stage, string Reason, int Status = 0, string Field = "")
{
    [JsonIgnore]
    public bool Valid => Stage is "connect" or "tls" or "challenge" or "probe"
        && Reason is "http-status" or "version" or "missing-header" or "duplicate-header" or "signature"
            or "invalid-json" or "invalid-field" or "nonce" or "transport" or "not-running" or "target"
            or "restarted" or "body-limit" or "timeout" or "dns" or "connection-refused" or "network"
            or "certificate-missing" or "certificate-name" or "certificate-date" or "certificate-chain"
            or "certificate-usage" or "certificate-pin" or "handshake"
        && (Status == 0 || Status is >= 100 and <= 599)
        && Field is "" or "X-WG-Version" or "X-WG-Auth" or "X-WG-Epoch" or "X-WG-Time"
            or "X-WG-Seq" or "X-WG-Down-Dropped" or "X-WG-Auth-Error" or "epoch" or "time" or "response";

    /// <summary>
    /// Encodes a classified refusal.
    /// </summary>
    public string Encode(string code) => Valid && code.Length <= 64 && code.All(one => char.IsAsciiLetterOrDigit(one) || one == '-')
        ? string.Join('|', code, Stage, Reason, Status.ToString(CultureInfo.InvariantCulture), Field) : code;

    /// <summary>
    /// Decodes a classified refusal.
    /// </summary>
    public static (string Code, TransportProxyDiagnostic? Diagnostic) Decode(string value)
    {
        var parts = value.Split('|');
        if (parts.Length == 5 && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            var diagnostic = new TransportProxyDiagnostic(parts[1], parts[2], status, parts[4]);
            if (diagnostic.Valid && diagnostic.Encode(parts[0]) == value)
            {
                return (parts[0], diagnostic);
            }
        }

        return (parts[0], null);
    }
}
