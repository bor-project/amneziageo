namespace AmneziaGeo.Decl;

/// <summary>
/// A single geosite domain rule; Named marks a host the list names outright rather than one a category brings.
/// </summary>
public sealed record GeoDomain(GeoDomainKind Kind, string Value, bool Named = false);
