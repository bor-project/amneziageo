using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What a server of ours offers one configuration: where it answered and the arguments of every feature by name.
/// </summary>
/// <param name="Config">The configuration the offer belongs to.</param>
/// <param name="Origin">The scheme, host and port the server answered at; empty when no server of ours answered.</param>
/// <param name="Inside">Whether the server answered inside the tunnel.</param>
/// <param name="Version">The version of the server.</param>
/// <param name="Client">The name the server holds the client under.</param>
/// <param name="Features">The arguments of every feature, by feature name.</param>
public sealed record ServerOffer(
    string Config,
    string Origin,
    bool Inside,
    string Version,
    string Client,
    IReadOnlyDictionary<string, JsonElement> Features)
{
    /// <summary>
    /// No server of ours.
    /// </summary>
    public static ServerOffer None { get; } = new(string.Empty, string.Empty, false, string.Empty, string.Empty, new Dictionary<string, JsonElement>());

    /// <summary>
    /// Whether a server of ours answered.
    /// </summary>
    [JsonIgnore]
    public bool Ours => Origin.Length > 0;

    /// <summary>
    /// Returns the host and port the server answered at.
    /// </summary>
    public string Authority() =>
        Uri.TryCreate(Origin, UriKind.Absolute, out var parsed) ? parsed.Authority : Origin;

    /// <summary>
    /// Returns the arguments of a feature, or null when it is not offered.
    /// </summary>
    public JsonElement? Arguments(string name) =>
        Features.TryGetValue(name, out var arguments) && arguments.ValueKind == JsonValueKind.Object ? arguments : null;

    /// <summary>
    /// Renders it as the ack payload.
    /// </summary>
    public string ToPayload() => JsonSerializer.Serialize(this, IpcJson.Options);

    /// <summary>
    /// Reads it back from the ack payload.
    /// </summary>
    public static ServerOffer Parse(string payload)
    {
        try
        {
            var offer = JsonSerializer.Deserialize<ServerOffer>(payload, IpcJson.Options);

            return offer is { Features: not null } ? offer : None;
        }
        catch (JsonException)
        {
            return None;
        }
    }
}

/// <summary>
/// Arguments of the speed feature.
/// </summary>
/// <param name="Down">The address a run pulls bytes from.</param>
/// <param name="Up">The address a run sends bytes to.</param>
/// <param name="Limit">The most bytes one leg carries.</param>
/// <param name="Expires">When the pass in the addresses stops answering.</param>
public sealed record SpeedArgs(string Down, string Up, long Limit, DateTimeOffset Expires)
{
    /// <summary>
    /// The key of the feature in the dictionary.
    /// </summary>
    public const string Name = "speed";

    /// <summary>
    /// Returns the arguments of the feature in an offer, or null when they are absent or broken.
    /// </summary>
    public static SpeedArgs? Of(ServerOffer? offer)
    {
        if (offer?.Arguments(Name) is not { } arguments)
        {
            return null;
        }

        var down = ServerHello.Text(arguments, "down");
        var up = ServerHello.Text(arguments, "up");
        if (down.Length == 0 || up.Length == 0)
        {
            return null;
        }

        var limit = arguments.TryGetProperty("limit", out var bytes) && bytes.TryGetInt64(out var most) ? most : 0;

        return new SpeedArgs(down, up, limit, Until(arguments));
    }

    // Reads when the pass stops answering, a minute from now when the server names no time.
    private static DateTimeOffset Until(JsonElement arguments)
    {
        return arguments.TryGetProperty("expires", out var when)
            && when.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(when.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp)
                ? stamp
                : DateTimeOffset.UtcNow.AddMinutes(1);
    }
}

/// <summary>
/// What one address answered to the hello.
/// </summary>
/// <param name="Offer">The offer of a server of ours, null when none answered.</param>
/// <param name="Heard">Whether the address answered anything at all.</param>
public sealed record HelloReply(ServerOffer? Offer, bool Heard);

/// <summary>
/// Asks a server what it is and what it offers the client holding a configuration.
/// </summary>
public static class ServerHello
{
    /// <summary>
    /// The word a server of ours answers under.
    /// </summary>
    public const string ServerName = "amneziageo";

    /// <summary>
    /// The path the point of the server sits at.
    /// </summary>
    public const string Path = "/api/hello";

    /// <summary>
    /// The header the countersign of the server travels in.
    /// </summary>
    public const string ProofHeader = "Amneziageo-Proof";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Asks one address what it offers this configuration.
    /// </summary>
    public static async Task<HelloReply> AskAsync(string origin, string privateKey, string serverKey, bool inside, CancellationToken ct)
    {
        if (!Curve25519.IsKey(privateKey) || !Curve25519.IsKey(serverKey))
        {
            return new HelloReply(null, false);
        }

        var heard = false;
        var handler = new SocketsHttpHandler { ConnectTimeout = _timeout, UseProxy = false };

        try
        {
            using var client = new HttpClient(handler) { Timeout = _timeout };
            using var greeting = await client.GetAsync(origin + Path, ct).ConfigureAwait(false);
            heard = true;
            var challenge = await ChallengeAsync(greeting, ct).ConfigureAwait(false);
            if (challenge.Length == 0)
            {
                return new HelloReply(null, true);
            }

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PeerProof.NonceBytes));
            var answer = new
            {
                key = Curve25519.PublicOf(privateKey),
                challenge,
                nonce,
                proof = PeerProof.Answer(privateKey, serverKey, challenge),
            };

            using var body = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(origin + Path, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new HelloReply(null, true);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var countersign = response.Headers.TryGetValues(ProofHeader, out var values) ? values.FirstOrDefault() : null;
            if (!PeerProof.Countersigns(privateKey, serverKey, nonce, bytes, countersign))
            {
                return new HelloReply(null, true);
            }

            return new HelloReply(Offer(origin, bytes, inside), true);
        }
        catch (Exception ex) when (Refused(ex))
        {
            return new HelloReply(null, heard);
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>
    /// Returns a string property of a JSON object, empty when it is absent.
    /// </summary>
    public static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // Reads the challenge from a greeting of a server of ours.
    private static async Task<string> ChallengeAsync(HttpResponseMessage greeting, CancellationToken ct)
    {
        if (!greeting.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        using var json = JsonDocument.Parse(await greeting.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        return json.RootElement.ValueKind == JsonValueKind.Object && Ours(json.RootElement)
            ? Text(json.RootElement, "challenge")
            : string.Empty;
    }

    // Reads the dictionary of features from a countersigned answer.
    private static ServerOffer? Offer(string origin, byte[] body, bool inside)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Ours(root))
        {
            return null;
        }

        var features = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("features", out var offered) && offered.ValueKind == JsonValueKind.Object)
        {
            foreach (var feature in offered.EnumerateObject())
            {
                if (feature.Value.ValueKind == JsonValueKind.Object)
                {
                    features[feature.Name] = feature.Value.Clone();
                }
            }
        }

        return new ServerOffer(string.Empty, origin, inside, Text(root, "version"), Text(root, "client"), features);
    }

    // Tells whether an answer comes from a server of ours.
    private static bool Ours(JsonElement root) =>
        string.Equals(Text(root, "server"), ServerName, StringComparison.Ordinal);

    // Tells whether a failure means the address offers nothing.
    private static bool Refused(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or OperationCanceledException
            or JsonException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or UriFormatException;
}
