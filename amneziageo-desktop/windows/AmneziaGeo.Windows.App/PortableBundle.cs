using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A portable, SELECTIVE snapshot of zero or more configs, routing lists, and profiles, picked by the user
/// from a tree of checkboxes - any combination of shared catalogue entries plus the thin profile rows that
/// reference them by name. Configs carry their private keys in the clear, so the resulting file must be
/// stored/transferred carefully. Materialized route/domain sets are intentionally omitted from routing
/// lists - only the rule tokens travel, so the target machine re-materializes them against its own geo data.
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
    /// The full exported selection: zero or more configs, routing lists, and profiles. A profile's
    /// <see cref="ProfileBlock.Config"/> / <see cref="ProfileBlock.RoutingList"/> are names that resolve into
    /// <see cref="Configs"/> / <see cref="RoutingLists"/> on import; they may be null (no config / no routing
    /// assigned) or refer to a name not present in this same bundle (the profile was selected without its
    /// dependency - import then leaves that side unbound).
    /// </summary>
    public sealed record Bundle(
        string Format,
        int Version,
        IReadOnlyList<ConfigBlock> Configs,
        IReadOnlyList<RoutingBlock> RoutingLists,
        IReadOnlyList<ProfileBlock> Profiles);

    /// <summary>
    /// A standalone configuration: its wg-quick text (including keys), WebSocket transport (if any), and its
    /// own geo split (if any). DNS and exclusions are NOT carried here - stage #87/#88/#90 moved those onto
    /// the routing preset (<see cref="RoutingSettingsBlock"/>).
    /// </summary>
    public sealed record ConfigBlock(
        string Name,
        string ConfigText,
        TransportBlock? Transport,
        GeoBlock? Geo);

    /// <summary>
    /// The config's WebSocket (UDP-over-TCP) transport and tunnel MTU. Host empty reuses the config's own
    /// Endpoint host.
    /// </summary>
    public sealed record TransportBlock(bool UseWebSocket, string Host, int Port, int Mtu);

    /// <summary>
    /// The config's own geo split: whether it is on and the rule tokens (geosite:openai, geoip:ru, …).
    /// </summary>
    public sealed record GeoBlock(bool Split, IReadOnlyList<string> Rules);

    /// <summary>
    /// A standalone, shared routing list: its rule tokens and its traffic settings (local DNS, exclusions,
    /// all-UDP), when customized.
    /// </summary>
    public sealed record RoutingBlock(
        string Name,
        IReadOnlyList<string> Rules,
        RoutingSettingsBlock? Settings);

    /// <summary>
    /// A routing list's traffic policy. Mode is always "split" here - "full" is a live, per-connect choice,
    /// not part of the shared list's portable state.
    /// </summary>
    public sealed record RoutingSettingsBlock(string LocalDns, string Exclusions, bool AllUdp);

    /// <summary>
    /// A thin profile reference: the config and routing list it binds, by name (resolved against
    /// <see cref="Bundle.Configs"/> / <see cref="Bundle.RoutingLists"/> on import), and whether it uses
    /// routing. Either reference may be null when the profile has none.
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
