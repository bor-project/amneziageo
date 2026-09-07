using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Stamp of everything a session plan is built from: an equal stamp means the plan already written still fits.
/// </summary>
public static class RoutingPlanStamp
{
    // Rises with the shape of the plan, so a plan left by an older build is never taken for a fitting one.
    private const int Format = 1;

    /// <summary>
    /// Returns the stamp of a list with its settings and the session around it.
    /// </summary>
    public static string Of(RoutingListStamp? list, RoutingSettings? settings, IReadOnlyList<string> inbound,
        bool useRouter, bool perApp, int ttlSeconds)
    {
        var text = new StringBuilder();
        Number(text, Format);
        Number(text, list?.Id ?? 0);
        Number(text, list?.Generation ?? 0);
        foreach (var rule in list?.Rules ?? [])
        {
            text.Append('|').Append(rule.Kind).Append(':').Append(rule.Role).Append(':').Append(rule.Value);
        }

        text.Append('|').Append(settings?.Exclusions ?? string.Empty)
            .Append('|').Append(settings is { AllUdp: true })
            .Append('|').Append(settings is { UseGlobalProxy: true })
            .Append('|').Append(settings?.Mode ?? string.Empty)
            .Append('|').Append(string.Join(',', inbound))
            .Append('|').Append(useRouter)
            .Append('|').Append(perApp);
        Number(text, ttlSeconds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Number(StringBuilder text, long value)
    {
        text.Append('|').Append(value.ToString(CultureInfo.InvariantCulture));
    }
}
