namespace AmneziaGeo.Decl;

/// <summary>
/// A destination the routing cache decided, with the moment traffic last touched it. The verdict travels as its
/// name, so the store stays free of the routing types.
/// </summary>
public sealed record RememberedRoute(string Address, string Verdict, bool ByName, bool ByApp, DateTimeOffset SeenAt);
