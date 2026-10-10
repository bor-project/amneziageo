using System.Globalization;
using System.Net;
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
/// The subscription a server of ours names for a config.
/// </summary>
/// <param name="Url">The address the subscription is read at.</param>
/// <param name="Revision">The mark of what the subscription hands out now.</param>
/// <param name="Pin">The SHA-256 of the certificate the address answers under, empty when it names none.</param>
public sealed record OfferedSubscription(string Url, string Revision, string Pin);

/// <summary>
/// A geo source a server of ours hands its clients.
/// </summary>
/// <param name="Name">The name the server knows the source by, empty when it names none.</param>
/// <param name="Kind">geoip or geosite.</param>
/// <param name="Url">The address the file of the source is fetched from.</param>
public sealed record OfferedSource(string Name, string Kind, string Url);

/// <summary>
/// A routing list a server of ours hands its clients.
/// </summary>
/// <param name="Name">The name the list takes.</param>
/// <param name="Rules">The rules of the list, each led by what it does with the traffic.</param>
/// <param name="AllUdp">Whether every UDP packet goes through the tunnel.</param>
/// <param name="Full">Whether everything goes through the tunnel but what goes directly.</param>
/// <param name="Id">The identifier the server knows the list by, empty when it names none.</param>
/// <param name="Updated">When the server last changed the list, null when it does not say.</param>
/// <param name="Default">Whether the list is put in use when it is added and none is in use.</param>
/// <param name="Source">The name of the configuration the list came with, empty when the server names none.</param>
public sealed record OfferedPreset(
    string Name,
    IReadOnlyList<string> Rules,
    bool AllUdp,
    bool Full,
    string Id = "",
    DateTimeOffset? Updated = null,
    bool Default = false,
    string Source = "")
{
    /// <summary>
    /// Renders the list the way a server hands it out.
    /// </summary>
    public string ToPayload()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            writer.WriteStartArray("rules");
            foreach (var rule in Rules)
            {
                writer.WriteStringValue(rule);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("allUdp", AllUdp);
            writer.WriteBoolean("full", Full);
            writer.WriteString("id", Id);
            if (Updated is { } when)
            {
                writer.WriteString("updated", when);
            }

            writer.WriteBoolean("default", Default);
            writer.WriteString("source", Source);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a list back from what <see cref="ToPayload"/> rendered; null when the text holds none.
    /// </summary>
    public static OfferedPreset? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);

            return ServerOffer.Preset(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

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
    /// The feature that measures the speed of the way to the server.
    /// </summary>
    public const string SpeedFeature = "speed";

    /// <summary>
    /// The feature that names the subscription of the client.
    /// </summary>
    public const string SubscriptionFeature = "subscription";

    /// <summary>
    /// The feature that names the geo sources the client holds.
    /// </summary>
    public const string SourcesFeature = "sources";

    /// <summary>
    /// The feature that names the routing lists the client holds.
    /// </summary>
    public const string PresetsFeature = "presets";

    /// <summary>
    /// The feature that names where the device takes the signal to disconnect.
    /// </summary>
    public const string DisconnectFeature = "disconnect";

    /// <summary>
    /// The most addresses the signal to disconnect is taken from.
    /// </summary>
    public const int MaxSignalSources = 8;

    /// <summary>
    /// The most geo sources one answer is taken for.
    /// </summary>
    public const int MaxSources = 64;

    /// <summary>
    /// The most routing lists one answer is taken for.
    /// </summary>
    public const int MaxPresets = 32;

    /// <summary>
    /// The most rules one offered routing list is taken with.
    /// </summary>
    public const int MaxPresetRules = 1024;

    /// <summary>
    /// The longest name an offered routing list is taken under.
    /// </summary>
    public const int MaxPresetName = 64;

    /// <summary>
    /// The longest identifier an offered routing list is taken under.
    /// </summary>
    public const int MaxPresetId = 64;

    /// <summary>
    /// The longest name of a configuration an offered routing list is taken with.
    /// </summary>
    public const int MaxPresetSource = 128;

    private static readonly string[] PresetRoles = ["proxy", "direct", "block"];
    private static readonly string[] PresetKinds = ["geosite", "geoip", "domain", "cidr"];

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
    /// The path the server takes the tunnel inside a websocket under; empty when it names none.
    /// </summary>
    public string WebSocketPath =>
        Arguments(WebSocketFeature) is { } arguments
        && arguments.TryGetProperty("path", out var path)
        && path.ValueKind == JsonValueKind.String
        && path.GetString() is { Length: > 0 and <= 64 } text
        && text.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
            ? text
            : string.Empty;

    /// <summary>
    /// The TCP port the device takes the signal to disconnect on, at its address inside the tunnel; zero when the
    /// server sends none.
    /// </summary>
    public int SignalPort =>
        Arguments(DisconnectFeature) is { } arguments
        && arguments.TryGetProperty("port", out var port)
        && port.ValueKind == JsonValueKind.Number
        && port.TryGetInt32(out var number)
        && number is >= 1 and <= 65535
            ? number
            : 0;

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
    /// Returns the addresses the signal to disconnect comes from; none when the server names none.
    /// </summary>
    public IReadOnlyList<string> SignalSources()
    {
        if (Arguments(DisconnectFeature) is not { } arguments
            || !arguments.TryGetProperty("from", out var from)
            || from.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sources = new List<string>();
        foreach (var item in from.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && IPAddress.TryParse(item.GetString()!.Trim(), out var address)
                && !sources.Contains(address.ToString())
                && sources.Count < MaxSignalSources)
            {
                sources.Add(address.ToString());
            }
        }

        return sources;
    }

    /// <summary>
    /// Returns the subscription the server names for the config; null when it names none.
    /// </summary>
    public OfferedSubscription? Subscription()
    {
        if (Arguments(SubscriptionFeature) is not { } arguments)
        {
            return null;
        }

        var url = Text(arguments, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return new OfferedSubscription(url, Text(arguments, "revision"), Text(arguments, "pin"));
    }

    /// <summary>
    /// Returns the geo sources the server names, the ones of a kind or at an address the client cannot take left out.
    /// </summary>
    public IReadOnlyList<OfferedSource> Sources()
    {
        if (Arguments(SourcesFeature) is not { } arguments
            || !arguments.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sources = new List<OfferedSource>();
        foreach (var item in items.EnumerateArray().Take(MaxSources))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = Text(item, "kind").ToLowerInvariant();
            var url = Text(item, "url").Trim();
            if (kind is "geoip" or "geosite"
                && Uri.TryCreate(url, UriKind.Absolute, out var address)
                && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                sources.Add(new OfferedSource(Text(item, "name").Trim(), kind, url));
            }
        }

        return sources;
    }

    /// <summary>
    /// Returns the routing lists the server names, each with the rules the client routes by: what goes through the
    /// tunnel, past it or nowhere, by geo key, network or domain. Lists without a name are left out.
    /// </summary>
    public IReadOnlyList<OfferedPreset> Presets()
    {
        if (Arguments(PresetsFeature) is not { } arguments
            || !arguments.TryGetProperty("lists", out var lists)
            || lists.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var presets = new List<OfferedPreset>();
        foreach (var list in lists.EnumerateArray().Take(MaxPresets))
        {
            if (Preset(list) is not { } preset
                || presets.Exists(one => string.Equals(one.Name, preset.Name, StringComparison.Ordinal))
                || (preset.Id.Length > 0 && presets.Exists(one => string.Equals(one.Id, preset.Id, StringComparison.Ordinal))))
            {
                continue;
            }

            presets.Add(preset);
        }

        return presets;
    }

    /// <summary>
    /// Reads one routing list a server hands out; null when it carries no name the client takes.
    /// </summary>
    public static OfferedPreset? Preset(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Object
            || Text(list, "name").Trim() is not { Length: > 0 and <= MaxPresetName } name)
        {
            return null;
        }

        var rules = list.TryGetProperty("rules", out var given) && given.ValueKind == JsonValueKind.Array
            ? given.EnumerateArray()
                .Where(rule => rule.ValueKind == JsonValueKind.String)
                .Select(rule => rule.GetString()!.Trim())
                .Where(IsPresetRule)
                .Take(MaxPresetRules)
                .ToList()
            : [];
        var id = PresetId(Text(list, "id"));
        var source = Text(list, "source").Trim();

        return new OfferedPreset(
            name,
            rules,
            Flag(list, "allUdp"),
            Flag(list, "full"),
            id,
            id.Length > 0 ? When(list, "updated") : null,
            Flag(list, "default"),
            source.Length > MaxPresetSource ? source[..MaxPresetSource] : source);
    }

    /// <summary>
    /// Tells whether another offer settles the tunnel the same way: from a server of ours alike, with the same
    /// websocket.
    /// </summary>
    public bool Settles(ServerOffer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Ours == other.Ours
            && WebSocketPort == other.WebSocketPort
            && string.Equals(WebSocketPath, other.WebSocketPath, StringComparison.Ordinal);
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

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? When(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var when)
            ? when.ToUniversalTime()
            : null;

    // An identifier is letters, digits and dashes; anything else reads as a list the server names no identifier of.
    private static string PresetId(string text)
    {
        var id = text.Trim().ToLowerInvariant();

        return id.Length is > 0 and <= MaxPresetId && id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter == '-')
            ? id
            : string.Empty;
    }

    // A rule of an offered list says what it does and names a geo key, a network or a domain; a server never
    // routes the applications of the device.
    private static bool IsPresetRule(string rule)
    {
        var bar = rule.IndexOf('|', StringComparison.Ordinal);
        var colon = rule.IndexOf(':', StringComparison.Ordinal);

        return bar > 0
            && colon > bar + 1
            && colon < rule.Length - 1
            && PresetRoles.Contains(rule[..bar], StringComparer.OrdinalIgnoreCase)
            && PresetKinds.Contains(rule[(bar + 1)..colon], StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// An offer kept for a config and when its server was asked.
/// </summary>
/// <param name="Offer">What the server offered, none when it is not a server of ours or did not answer.</param>
/// <param name="Asked">When the server was asked.</param>
/// <param name="Heard">Whether the services ever answered anything at all.</param>
/// <param name="Waits">How many connects waited in vain for services that never answered.</param>
public sealed record KeptOffer(ServerOffer Offer, DateTimeOffset Asked, bool Heard = true, int Waits = 0);

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
            var heard = !root.TryGetProperty("heard", out var answered) || answered.ValueKind != JsonValueKind.False;
            var waits = root.TryGetProperty("waits", out var waited) && waited.ValueKind == JsonValueKind.Number && waited.TryGetInt32(out var count)
                ? Math.Max(count, 0)
                : 0;

            return new KeptOffer(offer, asked, heard, waits);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps what the services of a config offered.
    /// </summary>
    public static Task WriteAsync(IStateStore store, string config, ServiceTarget point, ServerOffer offer, DateTimeOffset asked, CancellationToken ct = default) =>
        WriteAsync(store, config, point, offer, asked, true, 0, ct);

    /// <summary>
    /// Keeps what the services of a config offered, or that they never answered and how many connects waited for them.
    /// </summary>
    public static Task WriteAsync(IStateStore store, string config, ServiceTarget point, ServerOffer offer, DateTimeOffset asked, bool heard, int waits, CancellationToken ct = default)
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
            if (!heard)
            {
                writer.WriteBoolean("heard", false);
                writer.WriteNumber("waits", waits);
            }

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
