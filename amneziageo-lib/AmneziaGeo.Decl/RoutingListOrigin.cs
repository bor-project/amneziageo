namespace AmneziaGeo.Decl;

/// <summary>
/// Where a routing list came from: the list of a server it was taken from, the configuration it arrived with, when
/// the server changed the version held and the newer version that waits for the owner.
/// </summary>
/// <param name="ListId">The routing list.</param>
/// <param name="PresetId">The identifier the server knows the list by.</param>
/// <param name="Source">The name of the configuration the list arrived with.</param>
/// <param name="Updated">When the server changed the version the list holds.</param>
/// <param name="Pending">The newer version of the server, null when the list holds the newest.</param>
public sealed record RoutingListOrigin(long ListId, string PresetId, string Source, DateTimeOffset Updated, OfferedPreset? Pending = null);
