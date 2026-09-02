using System.Globalization;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Geo;

/// <summary>
/// Источник по умолчанию: чем он является, откуда обновляется и какая копия лежит в комплекте.
/// </summary>
public readonly record struct GeoDefaultSource(string Kind, string Url, string Bundled);

/// <summary>
/// The default geo sources seeded for a fresh install (standard v2ray-format geosite/geoip the app
/// already parses, e.g. geosite:youtube). Shared by the startup seeder and the installer-triggered
/// download op so both agree on what a fresh install ships.
/// </summary>
public static class GeoDefaults
{
    /// <summary>
    /// Состав набора по умолчанию: растёт, когда добавляются источники.
    /// </summary>
    public const int SeedVersion = 2;

    private const string SeedVersionKey = "geo.seed-version";

    /// <summary>
    /// Источники по умолчанию в порядке добавления.
    /// </summary>
    public static readonly GeoDefaultSource[] Sources =
    [
        new(
            "geosite",
            "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
            ""),
        new(
            "geoip",
            "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
            ""),
        new(
            "geosite",
            "https://github.com/runetfreedom/russia-blocked-geosite/releases/latest/download/geosite-ru-only.dat",
            "geosite-ru-only.dat"),
        new(
            "geoip",
            "https://github.com/runetfreedom/russia-blocked-geoip/releases/latest/download/geoip-ru-only.dat",
            "geoip-ru-only.dat"),
    ];

    /// <summary>
    /// Adds the missing default sources and unpacks the copies shipped with the app.
    /// </summary>
    public static async Task<bool> SeedAsync(IStateStore store, IGeoFileStore? files, ILogger? logger, CancellationToken ct)
    {
        var stamp = await store.GetSettingAsync(SeedVersionKey, ct).ConfigureAwait(false);
        if (int.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeded) && seeded >= SeedVersion)
        {
            return false;
        }

        var existing = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var known = new HashSet<string>(existing.Select(row => row.Url), StringComparer.OrdinalIgnoreCase);
        var position = existing.Count == 0 ? 0 : existing.Max(row => row.Position);
        var added = false;

        foreach (var source in Sources)
        {
            if (!known.Add(source.Url))
            {
                continue;
            }

            position++;
            var name = $"{source.Kind}-{position}";
            await store.SaveGeoSourceAsync(new GeoSource(name, source.Kind, source.Url, position), ct).ConfigureAwait(false);
            added = true;
            logger?.LogInformation("added the standard rule database {Name} from {Url}; country and service rules are matched against it", name, source.Url);
            await UnpackAsync(store, files, name, source, logger, ct).ConfigureAwait(false);
        }

        await store.SetSettingAsync(SeedVersionKey, SeedVersion.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return added;
    }

    // Кладёт копию из комплекта, чтобы правила работали до первой загрузки.
    private static async Task UnpackAsync(IStateStore store, IGeoFileStore? files, string name, GeoDefaultSource source, ILogger? logger, CancellationToken ct)
    {
        if (files is null || source.Bundled.Length == 0 || await store.GetGeoFileAsync(name, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        try
        {
            var data = Bundled(source.Bundled);
            if (data is null)
            {
                return;
            }

            var count = source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase)
                ? GeoIpDatabase.Countries(data).Count
                : GeoSiteDatabase.Categories(data).Count;

            await files.WriteAsync(name, data, ct).ConfigureAwait(false);
            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            await store.SaveGeoFileAsync(new GeoFileMetadata(name, source.Url, DateTimeOffset.UtcNow, sha, count), ct).ConfigureAwait(false);
            logger?.LogInformation("unpacked the bundled copy of {Name} with {Count} categories; the rules work before the first download", name, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "the bundled copy of {Name} could not be unpacked; the database is downloaded on the first update instead", name);
        }
    }

    private static byte[]? Bundled(string file)
    {
        using var stream = typeof(GeoDefaults).Assembly.GetManifestResourceStream($"AmneziaGeo.Geo.Bundled.{file}");
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
