using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// One place a speed run measures at the server of a config: where bytes are pulled from and where they are sent to.
/// </summary>
/// <param name="Down">The address a run pulls bytes from.</param>
/// <param name="Up">The address a run sends bytes to.</param>
public sealed record SpeedLeg(string Down, string Up);

/// <summary>
/// What a server of ours offers one config: its version, the name it holds the client under and the arguments of
/// every feature by name.
/// </summary>
public sealed class ServerOffer
{
    /// <summary>
    /// The word a server of ours answers under.
    /// </summary>
    public const string ServerName = "amneziageo";

    /// <summary>
    /// The feature that carries the tunnel inside a websocket.
    /// </summary>
    public const string WebSocketFeature = "websocket";

    /// <summary>
    /// The feature that says whether the client may route on its own.
    /// </summary>
    public const string RoutingFeature = "routing";

    /// <summary>
    /// The feature that measures the speed of the way to the server.
    /// </summary>
    public const string SpeedFeature = "speed";

    private readonly Dictionary<string, JsonElement> _features;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerOffer(string version, string client, IReadOnlyDictionary<string, JsonElement> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        Version = version;
        Client = client;
        _features = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, arguments) in features)
        {
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                _features[name] = arguments.Clone();
            }
        }
    }

    /// <summary>
    /// No server of ours.
    /// </summary>
    public static ServerOffer None { get; } = new(string.Empty, string.Empty, new Dictionary<string, JsonElement>());

    /// <summary>
    /// The version of the server.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// The name the server holds the client under.
    /// </summary>
    public string Client { get; }

    /// <summary>
    /// The arguments of every feature, by name.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Features => _features;

    /// <summary>
    /// Whether a server of ours answered.
    /// </summary>
    public bool Ours => Version.Length > 0;

    /// <summary>
    /// The TCP port the server takes the tunnel inside a websocket on; zero when it offers none.
    /// </summary>
    public int WebSocketPort =>
        Arguments(WebSocketFeature) is { } arguments
        && arguments.TryGetProperty("port", out var port)
        && port.ValueKind == JsonValueKind.Number
        && port.TryGetInt32(out var number)
        && number is >= 1 and <= 65535
            ? number
            : 0;

    /// <summary>
    /// Whether the server bans routing on the device.
    /// </summary>
    public bool RoutingLocked =>
        Arguments(RoutingFeature) is { } arguments
        && arguments.TryGetProperty("allowed", out var allowed)
        && allowed.ValueKind == JsonValueKind.False;

    /// <summary>
    /// When the pass of the speed feature stops answering; the start of time when the server does not measure.
    /// </summary>
    public DateTimeOffset SpeedExpires =>
        Arguments(SpeedFeature) is { } arguments
        && arguments.TryGetProperty("expires", out var when)
        && when.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(when.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp
            : DateTimeOffset.MinValue;

    /// <summary>
    /// Returns the arguments of a feature, or null when it is not offered.
    /// </summary>
    public JsonElement? Arguments(string name) =>
        _features.TryGetValue(name, out var arguments) ? arguments : null;

    /// <summary>
    /// Returns where a speed run measures inside the tunnel or beside it; null when the server does not measure.
    /// </summary>
    public SpeedLeg? Speed(bool inside)
    {
        if (Arguments(SpeedFeature) is not { } arguments
            || !arguments.TryGetProperty(inside ? "inside" : "outside", out var leg)
            || leg.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var down = Text(leg, "down");
        var up = Text(leg, "up");

        return down.Length > 0 && up.Length > 0 ? new SpeedLeg(down, up) : null;
    }

    /// <summary>
    /// Tells whether another offer settles the tunnel the same way: the same websocket and the same routing.
    /// </summary>
    public bool Settles(ServerOffer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return WebSocketPort == other.WebSocketPort && RoutingLocked == other.RoutingLocked;
    }

    /// <summary>
    /// Renders it as JSON.
    /// </summary>
    public string ToPayload()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("server", ServerName);
            writer.WriteString("version", Version);
            writer.WriteString("client", Client);
            writer.WriteStartObject("features");
            foreach (var (name, arguments) in _features)
            {
                writer.WritePropertyName(name);
                arguments.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads it back from JSON; no server of ours when the JSON is not an answer of one.
    /// </summary>
    public static ServerOffer Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return None;
        }

        return Read(Encoding.UTF8.GetBytes(payload)) ?? None;
    }

    /// <summary>
    /// Reads the answer of a server; null when it is not an answer of a server of ours.
    /// </summary>
    public static ServerOffer? Read(ReadOnlySpan<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "server"), ServerName, StringComparison.Ordinal))
            {
                return null;
            }

            var features = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("features", out var offered) && offered.ValueKind == JsonValueKind.Object)
            {
                foreach (var feature in offered.EnumerateObject())
                {
                    features[feature.Name] = feature.Value;
                }
            }

            var version = Text(root, "version");

            return new ServerOffer(version.Length > 0 ? version : "0", Text(root, "client"), features);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// An offer kept for a config and when its server was asked.
/// </summary>
/// <param name="Offer">What the server offered, none when it is not a server of ours or did not answer.</param>
/// <param name="Asked">When the server was asked.</param>
public sealed record KeptOffer(ServerOffer Offer, DateTimeOffset Asked);

/// <summary>
/// Keeps what the servers of the configs offer in the settings of the store, one entry per config.
/// </summary>
public static class ServerOfferStore
{
    /// <summary>
    /// The prefix of the setting an offer is kept under.
    /// </summary>
    public const string Prefix = "offer:";

    /// <summary>
    /// Returns the setting the offer of a config is kept under.
    /// </summary>
    public static string Key(string config) => Prefix + config;

    /// <summary>
    /// Returns what the server of a config offers, as kept for the services its text asks.
    /// </summary>
    public static async Task<ServerOffer> ReadAsync(IStateStore store, string config, string? text, CancellationToken ct = default) =>
        (await KeptAsync(store, config, text, ct).ConfigureAwait(false))?.Offer ?? ServerOffer.None;

    /// <summary>
    /// Returns what the server of a stored config offers.
    /// </summary>
    public static async Task<ServerOffer> ReadAsync(IStateStore store, string config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var text = await store.GetConfigTextAsync(config, ct).ConfigureAwait(false);

        return await ReadAsync(store, config, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the offer kept for the services a config text asks, null when none was kept for them.
    /// </summary>
    public static async Task<KeptOffer?> KeptAsync(IStateStore store, string config, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (ConfigServices.Target(text) is not { } point)
        {
            return null;
        }

        var raw = await store.GetSettingAsync(Key(config), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("mark", out var mark)
                || !string.Equals(mark.GetString(), point.Mark(), StringComparison.Ordinal))
            {
                return null;
            }

            var offer = root.TryGetProperty("offer", out var body) && body.ValueKind == JsonValueKind.Object
                ? Parse(body)
                : ServerOffer.None;
            var asked = root.TryGetProperty("asked", out var when) && when.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.MinValue;

            return new KeptOffer(offer, asked);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps what the services of a config offered.
    /// </summary>
    public static Task WriteAsync(IStateStore store, string config, ServiceTarget point, ServerOffer offer, DateTimeOffset asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(offer);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mark", point.Mark());
            writer.WriteNumber("asked", asked.ToUnixTimeSeconds());
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

        return store.SetSettingAsync(Key(config), Encoding.UTF8.GetString(buffer.ToArray()), ct);
    }

    /// <summary>
    /// Moves the offer of a config onto its new name.
    /// </summary>
    public static async Task MoveAsync(IStateStore store, string oldName, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var raw = await store.GetSettingAsync(Key(oldName), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        await store.SetSettingAsync(Key(newName), raw, ct).ConfigureAwait(false);
        await store.SetSettingAsync(Key(oldName), string.Empty, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets the offer of a config.
    /// </summary>
    public static Task ForgetAsync(IStateStore store, string config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        return store.SetSettingAsync(Key(config), string.Empty, ct);
    }

    private static ServerOffer Parse(JsonElement body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            body.WriteTo(writer);
        }

        return ServerOffer.Read(buffer.ToArray()) ?? ServerOffer.None;
    }
}
