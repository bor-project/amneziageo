using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Where a server of ours measures the speed for one client, and for how long the pass in the addresses answers.
/// The addresses carry the host the server was asked at, so one asked inside the tunnel measures inside it.
/// </summary>
public sealed record SpeedOffer(string Origin, string Down, string Up, long Limit, DateTimeOffset Expires, bool Inside);

/// <summary>
/// What the window is told about where a probe measures its speed: the server of the configuration when it
/// offers that, and nothing when the service in the settings decides.
/// </summary>
public sealed record SpeedService(bool Own, string Server, string Against)
{
    /// <summary>
    /// Nothing offered, so the service in the settings stands.
    /// </summary>
    public static SpeedService None { get; } = new(false, string.Empty, string.Empty);

    /// <summary>
    /// Renders it as the ack payload.
    /// </summary>
    public string ToPayload() => JsonSerializer.Serialize(this, IpcJson.Options);

    /// <summary>
    /// Reads it back from the ack payload.
    /// </summary>
    public static SpeedService Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<SpeedService>(payload, IpcJson.Options) ?? None;
        }
        catch (JsonException)
        {
            return None;
        }
    }
}

/// <summary>
/// Asks a server what it is and what it offers the client holding a configuration. Nothing here needs an account
/// of the panel: the answer to its challenge is counted from the keys the configuration already carries.
/// </summary>
public static class ServerHello
{
    /// <summary>
    /// The word a server of ours answers under.
    /// </summary>
    public const string ServerName = "amneziageo";

    /// <summary>
    /// The port the panel answers on where nothing names another.
    /// </summary>
    public const int PanelPort = 8443;

    /// <summary>
    /// The offer that measures the speed against the server itself.
    /// </summary>
    public const string SpeedFeature = "speed";

    /// <summary>
    /// The path the point of the server sits at.
    /// </summary>
    public const string Path = "/api/hello";

    // What one leg of the exchange is given: a server that is not there is left behind rather than waited for.
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Asks one address what it offers this configuration; anything but a server of ours answers nothing.
    /// </summary>
    public static async Task<SpeedOffer?> AskAsync(string origin, string privateKey, string serverKey, bool inside, CancellationToken ct)
    {
        if (!Curve25519.IsKey(privateKey) || !Curve25519.IsKey(serverKey))
        {
            return null;
        }

        var handler = new SocketsHttpHandler { ConnectTimeout = _timeout };
        // Takes the certificate of the panel as it stands: it answers on an address of its own, most often inside
        // the tunnel, where no name a certificate is issued for exists.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        try
        {
            using var client = new HttpClient(handler) { Timeout = _timeout };
            var challenge = await ChallengeAsync(client, origin, ct).ConfigureAwait(false);
            if (challenge.Length == 0)
            {
                return null;
            }

            var answer = new
            {
                key = Curve25519.PublicOf(privateKey),
                challenge,
                proof = PeerProof.Answer(privateKey, serverKey, challenge),
            };

            using var body = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(origin + Path, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return Speed(origin, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), inside);
        }
        catch (Exception ex) when (Refused(ex))
        {
            return null;
        }
        finally
        {
            handler.Dispose();
        }
    }

    // The challenge the server hands out, and the word that says it is one of ours.
    private static async Task<string> ChallengeAsync(HttpClient client, string origin, CancellationToken ct)
    {
        using var response = await client.GetAsync(origin + Path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Ours(json.RootElement) ? Text(json.RootElement, "challenge") : string.Empty;
    }

    // What the answer offers for the speed; a server that does not measure for this client offers none.
    private static SpeedOffer? Speed(string origin, string body, bool inside)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!Ours(root) || !root.TryGetProperty(SpeedFeature, out var speed) || speed.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var down = Text(speed, "down");
        var up = Text(speed, "up");
        if (down.Length == 0 || up.Length == 0)
        {
            return null;
        }

        var limit = speed.TryGetProperty("limit", out var bytes) && bytes.TryGetInt64(out var most) ? most : 0;

        return new SpeedOffer(origin, down, up, limit, Expires(speed), inside);
    }

    // When the pass stops answering; one the server does not date is taken as good for a minute.
    private static DateTimeOffset Expires(JsonElement speed)
    {
        return speed.TryGetProperty("expires", out var when)
            && when.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(when.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp)
                ? stamp
                : DateTimeOffset.UtcNow.AddMinutes(1);
    }

    // Whether the answer comes from a server of ours.
    private static bool Ours(JsonElement root) =>
        string.Equals(Text(root, "server"), ServerName, StringComparison.Ordinal);

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // A server that refuses, answers nothing or answers something else leaves the offer unknown.
    private static bool Refused(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or OperationCanceledException
            or JsonException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or UriFormatException;
}
