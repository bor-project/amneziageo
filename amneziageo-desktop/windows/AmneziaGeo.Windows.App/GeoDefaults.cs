using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The default geo sources seeded for a fresh install (standard v2ray-format geosite/geoip the app
/// already parses, e.g. geosite:youtube). Shared by the startup seeder and the installer-triggered
/// download op so both agree on what a fresh install ships.
/// </summary>
internal static class GeoDefaults
{
    public static readonly (string Kind, string Url)[] Sources =
    [
        ("geosite", "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat"),
        ("geoip", "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat"),
    ];

    /// <summary>Seeds the default sources only when none are configured. Returns true if it seeded.</summary>
    public static async Task<bool> SeedIfEmptyAsync(IStateStore store, ILogger? logger, CancellationToken ct)
    {
        var existing = await store.ListGeoSourcesAsync(ct);
        if (existing.Count > 0)
        {
            return false;
        }

        var position = 0;
        foreach (var (kind, url) in Sources)
        {
            position++;
            var name = $"{kind}-{position}";
            await store.SaveGeoSourceAsync(new GeoSource(name, kind, url, position), ct);
            logger?.LogInformation("seeded default geo source {Name} ({Url})", name, url);
        }

        return true;
    }
}
