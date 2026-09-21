using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What the services of a server answered the hello.
/// </summary>
/// <param name="Offer">The offer of a server of ours, null when none answered.</param>
/// <param name="Heard">Whether the services answered anything at all.</param>
public sealed record HelloReply(ServerOffer? Offer, bool Heard);

/// <summary>
/// Asks the services of a server what it offers the client holding a config.
/// </summary>
public static class ServerHello
{
    /// <summary>
    /// The path the hello sits at.
    /// </summary>
    public const string Path = "/api/hello";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Asks the services of a config what they offer it; a clock the server holds too far off is taken from its
    /// answer and the hello is asked once more.
    /// </summary>
    public static async Task<HelloReply> AskAsync(ServiceTarget point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (!Curve25519.IsKey(point.PrivateKey) || !Curve25519.IsKey(point.ServerKey))
        {
            return new HelloReply(null, false);
        }

        using var handler = new SocketsHttpHandler { ConnectTimeout = _timeout, UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = Presented;
        using var client = new HttpClient(handler) { Timeout = _timeout };
        var heard = false;
        try
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = ServiceToken.Make(point.PrivateKey, point.ServerKey, time);
                using var body = new StringContent(ServiceToken.Body(token), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(Origin(point) + Path, body, ct).ConfigureAwait(false);
                heard = true;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new HelloReply(Opened(point, token, bytes), true);
                }

                if (Clock(response.StatusCode, bytes) is not { } server)
                {
                    break;
                }

                time = server;
            }

            return new HelloReply(null, true);
        }
        catch (Exception ex) when (Refused(ex))
        {
            return new HelloReply(null, heard);
        }
    }

    /// <summary>
    /// Returns the address of the services of a point.
    /// </summary>
    public static string Origin(ServiceTarget point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var host = point.Host.Contains(':', StringComparison.Ordinal) ? $"[{point.Host}]" : point.Host;

        return string.Create(CultureInfo.InvariantCulture, $"https://{host}:{point.Port}");
    }

    // Takes any certificate the services present: the token proves the client and the answer is sealed for it.
    private static bool Presented(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null;

    // Opens a sealed answer and reads the offer in it.
    private static ServerOffer? Opened(ServiceTarget point, ServiceProof token, byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("iv", out var iv)
            || !root.TryGetProperty("data", out var data)
            || iv.ValueKind != JsonValueKind.String
            || data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var body = ServiceToken.Open(point.PrivateKey, point.ServerKey, token.Nonce, iv.GetString()!, data.GetString()!);

        return body is null ? null : ServerOffer.Read(body);
    }

    // Reads the clock of a server that refused a token for its time.
    private static long? Clock(HttpStatusCode status, byte[] bytes)
    {
        if (status != HttpStatusCode.Forbidden)
        {
            return null;
        }

        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var error)
            && string.Equals(error.GetString(), "stale-time", StringComparison.Ordinal)
            && root.TryGetProperty("time", out var time)
            && time.TryGetInt64(out var seconds)
                ? seconds
                : null;
    }

    // Tells whether a failure means the services answered nothing of ours.
    private static bool Refused(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or OperationCanceledException
            or JsonException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or UriFormatException;
}

/// <summary>
/// The answer to the question what the server of the selected config offers: the config and its offer.
/// </summary>
public static class OfferReply
{
    /// <summary>
    /// Renders the answer as the ack message.
    /// </summary>
    public static string Render(string config, ServerOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("config", config);
            writer.WritePropertyName("offer");
            if (offer.Ours)
            {
                using var json = JsonDocument.Parse(offer.ToPayload());
                json.RootElement.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads the answer back from the ack message.
    /// </summary>
    public static (string Config, ServerOffer Offer) Parse(string? message)
    {
        try
        {
            using var json = JsonDocument.Parse(message ?? string.Empty);
            var root = json.RootElement;
            var config = root.TryGetProperty("config", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
            var offer = root.TryGetProperty("offer", out var body) && body.ValueKind == JsonValueKind.Object
                ? ServerOffer.Parse(body.GetRawText())
                : ServerOffer.None;

            return (config, offer);
        }
        catch (JsonException)
        {
            return (string.Empty, ServerOffer.None);
        }
    }
}
