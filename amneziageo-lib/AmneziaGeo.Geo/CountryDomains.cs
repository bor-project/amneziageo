namespace AmneziaGeo.Geo;

/// <summary>
/// Names the domain suffixes a country owns. A country's top-level domain is its own code; the table holds the
/// codes whose domain differs and the internationalized forms the same country also answers on.
/// </summary>
public static class CountryDomains
{
    private static readonly Dictionary<string, string[]> Owned = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gb"] = ["uk"],
        ["ru"] = ["su", "xn--p1ai", "рф"],
        ["cn"] = ["xn--fiqs8s", "xn--fiqz9s", "中国", "中國"],
        ["kz"] = ["xn--80ao21a", "қаз"],
        ["ua"] = ["xn--j1amh", "укр"],
        ["rs"] = ["xn--90a3ac", "срб"],
        ["kr"] = ["xn--3e0b707e", "한국"],
        ["gr"] = ["xn--qxam", "ελ"],
    };

    /// <summary>
    /// Returns the suffixes a two-letter country code owns; any other token owns none.
    /// </summary>
    public static IReadOnlyList<string> Suffixes(string code)
    {
        if (!IsCountryCode(code))
        {
            return [];
        }

        var lower = code.ToLowerInvariant();
        if (!Owned.TryGetValue(lower, out var extra))
        {
            return [lower];
        }

        var all = new List<string>(extra.Length + 1) { lower };
        all.AddRange(extra);
        return all;
    }

    /// <summary>
    /// Returns whether the token is a two-letter country code.
    /// </summary>
    public static bool IsCountryCode(string code)
    {
        return code.Length == 2 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);
    }
}
