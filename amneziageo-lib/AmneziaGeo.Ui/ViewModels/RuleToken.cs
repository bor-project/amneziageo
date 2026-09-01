using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Reads a rule token: what kind of entry it is, its type badge and the line describing it.
/// </summary>
internal static class RuleToken
{
    /// <summary>
    /// Localized badge of the token type.
    /// </summary>
    public static string Badge(string token) => Loc.Instance.Get(BadgeKey(token));

    /// <summary>
    /// Localized kind of the entry.
    /// </summary>
    public static string Kind(string token) => Loc.Instance.Get(KindKey(token));

    /// <summary>
    /// Kind and badge as one caption under the token.
    /// </summary>
    public static string Caption(string token) => Loc.Instance.Get("Main_RuleCaption", Kind(token), Badge(token));

    /// <summary>
    /// Localized line telling what the token covers.
    /// </summary>
    public static string Describe(string token)
    {
        if (Has(token, "geosite:"))
        {
            return Loc.Instance.Get("Main_SuggestGeoSite", Value(token, "geosite:"));
        }

        if (Has(token, "geoip:"))
        {
            return Loc.Instance.Get("Main_SuggestGeoIp", Value(token, "geoip:").ToUpperInvariant());
        }

        if (Has(token, "domain:"))
        {
            return Loc.Instance.Get("Main_SuggestDomain");
        }

        if (Has(token, "cidr:"))
        {
            return Loc.Instance.Get("Main_SuggestCidr");
        }

        return Loc.Instance.Get("Main_SuggestApp");
    }

    private static string BadgeKey(string token) => token switch
    {
        _ when Has(token, "geosite:") => "Main_BadgeGeoSite",
        _ when Has(token, "geoip:") => "Main_BadgeGeoIp",
        _ when Has(token, "cidr:") => "Main_BadgeCidr",
        _ when Has(token, "app:pkg=") => "Main_BadgePackage",
        _ when Has(token, "app:path=") => "Main_BadgePath",
        _ when Has(token, "app:dir=") => "Main_BadgeFolder",
        _ when Has(token, "app:svc=") => "Main_BadgeService",
        _ => "Main_BadgeDomain",
    };

    private static string KindKey(string token) => token switch
    {
        _ when Has(token, "app:path=") => "Main_KindFile",
        _ when Has(token, "app:dir=") => "Main_KindFolder",
        _ when Has(token, "app:") => "Main_KindApp",
        _ => "Main_KindAddress",
    };

    private static bool Has(string token, string prefix) =>
        token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Value(string token, string prefix) => token[prefix.Length..];
}
