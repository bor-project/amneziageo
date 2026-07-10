using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Selective snapshot of configs, routing lists, profiles. Configs carry keys in clear; only rule tokens travel.
/// </summary>
internal static class PortableBundle
{
    /// <summary>
    /// Marker stored in every bundle so import can reject unrelated JSON.
    /// </summary>
    public const string FormatTag = "amneziageo-bundle";

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
    /// Exported selection of configs, routing lists, and profiles. Profile references resolve by name on import.
    /// </summary>
    public sealed record Bundle(
        string Format,
        int Version,
        IReadOnlyList<ConfigBlock> Configs,
        IReadOnlyList<RoutingBlock> RoutingLists,
        IReadOnlyList<ProfileBlock> Profiles);

    /// <summary>
    /// A standalone config: wg-quick text, WebSocket transport, geo split.
    /// </summary>
    public sealed record ConfigBlock(
        string Name,
        string ConfigText,
        TransportBlock? Transport,
        GeoBlock? Geo);

    /// <summary>
    /// WebSocket transport and tunnel MTU. Empty Host reuses the config's Endpoint host.
    /// </summary>
    public sealed record TransportBlock(bool UseWebSocket, string Host, int Port, int Mtu);

    /// <summary>
    /// Geo split toggle and rule tokens.
    /// </summary>
    public sealed record GeoBlock(bool Split, IReadOnlyList<string> Rules);

    /// <summary>
    /// A shared routing list: rule tokens and optional traffic settings.
    /// </summary>
    public sealed record RoutingBlock(
        string Name,
        IReadOnlyList<string> Rules,
        RoutingSettingsBlock? Settings);

    /// <summary>
    /// A routing list's traffic policy. Mode is always "split" here.
    /// </summary>
    public sealed record RoutingSettingsBlock(string LocalDns, string Exclusions, bool AllUdp, bool UseIpv6 = false);

    /// <summary>
    /// A thin profile reference: bound config and routing list by name; either may be null.
    /// </summary>
    public sealed record ProfileBlock(
        string Name,
        string? Config,
        string? RoutingList,
        bool UseRouting);

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
