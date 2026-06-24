using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A portable, self-contained snapshot of a single profile: its bound config (.conf text, including the
/// private keys), the config's transport and own geo split, and the profile's routing list. Serialized to
/// JSON for save-to-file / clipboard transfer between machines, and re-created on import under fresh names.
/// Materialized route/domain sets are intentionally omitted — only the rule tokens travel, so the target
/// machine re-materializes them against its own geo data.
/// </summary>
internal static class ProfilePortable
{
    /// <summary>
    /// Marker stored in every bundle so import can reject unrelated JSON.
    /// </summary>
    public const string FormatTag = "amneziageo-profile";

    /// <summary>
    /// The bundle schema version this build writes and can read.
    /// </summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The full exported profile. <see cref="ConfigText"/> is null when the profile has no config yet;
    /// <see cref="Transport"/>, <see cref="Geo"/>, <see cref="Routing"/>, and <see cref="Dns"/> are null
    /// when unset.
    /// </summary>
    public sealed record Bundle(
        string Format,
        int Version,
        string Profile,
        string Config,
        string? ConfigText,
        TransportBlock? Transport,
        GeoBlock? Geo,
        RoutingBlock? Routing,
        string? Dns = null,
        ExclusionsBlock? Exclusions = null);

    /// <summary>
    /// The config's WebSocket (UDP-over-TCP) transport. Host empty reuses the config's own Endpoint host.
    /// </summary>
    public sealed record TransportBlock(bool UseWebSocket, string Host, int Port);

    /// <summary>
    /// The config's own geo split: whether it is on and the rule tokens (geosite:openai, geoip:ru, …).
    /// </summary>
    public sealed record GeoBlock(bool Split, IReadOnlyList<string> Rules);

    /// <summary>
    /// The profile's routing list: whether the profile uses it, the list name, and its rule tokens.
    /// </summary>
    public sealed record RoutingBlock(bool Use, string ListName, IReadOnlyList<string> Rules);

    /// <summary>
    /// The config's bypass exclusions: the list (one entry per line) and whether to auto-exclude local
    /// subnets.
    /// </summary>
    public sealed record ExclusionsBlock(string List, bool AutoExcludeLan);

    /// <summary>
    /// Serializes a bundle to indented JSON.
    /// </summary>
    public static string Serialize(Bundle bundle)
    {
        return JsonSerializer.Serialize(bundle, _options);
    }

    /// <summary>
    /// Parses a bundle from JSON. Throws <see cref="JsonException"/> on malformed input.
    /// </summary>
    public static Bundle? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<Bundle>(json, _options);
    }
}
