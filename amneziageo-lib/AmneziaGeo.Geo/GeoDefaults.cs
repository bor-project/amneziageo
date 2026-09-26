using System.Globalization;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Geo;

/// <summary>
/// Источник по умолчанию: чем он является, откуда обновляется, какая копия лежит в комплекте, под каким именем
/// хранится (пустое - вид и место) и с какого набора он в нём.
/// </summary>
public readonly record struct GeoDefaultSource(string Kind, string Url, string Bundled, string Name = "", int Since = 1);

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
    public const int SeedVersion = 3;

    private const string SeedVersionKey = "geo.seed-version";

    /// <summary>
    /// Источники по умолчанию в том порядке, в каком они перекрывают друг друга.
    /// </summary>
    public static readonly GeoDefaultSource[] Sources =
    [
        new(
            "geoip",
            "https://github.com/jameszeroX/zkeen-ip/releases/latest/download/zkeenip.dat",
            "",
            "zkeenip",
            3),
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
            "geosite-ru-only.dat",
            Since: 2),
        new(
            "geoip",
            "https://github.com/runetfreedom/russia-blocked-geoip/releases/latest/download/geoip-ru-only.dat",
            "geoip-ru-only.dat",
            Since: 2),
    ];

    /// <summary>
    /// Adds the default sources the install has not been given: every one to a fresh install, the ones that joined
    /// the set since to an install seeded before; unpacks the copies shipped with the app.
    /// </summary>
    public static async Task<bool> SeedAsync(IStateStore store, IGeoFileStore? files, ILogger? logger, CancellationToken ct)
    {
        var stamp = await store.GetSettingAsync(SeedVersionKey, ct).ConfigureAwait(false);
        var existing = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var given = int.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeded)
            ? seeded
            : existing.Count == 0 ? 0 : 1;
        if (given >= SeedVersion)
        {
            return false;
        }

        var held = existing.OrderBy(row => row.Position).ToList();
        var added = false;

        foreach (var source in Sources.Where(source => source.Since > given))
        {
            if (held.Exists(row => Same(row, source)))
            {
                continue;
            }

            var position = Place(held, source);
            foreach (var later in held.Where(row => row.Position >= position).ToList())
            {
                var moved = later with { Position = later.Position + 1 };
                await store.SaveGeoSourceAsync(moved, ct).ConfigureAwait(false);
                held[held.IndexOf(later)] = moved;
            }

            var name = source.Name.Length > 0 ? source.Name : GeoSourceNames.Free(held, source.Kind, position);
            var row = new GeoSource(name, source.Kind, source.Url, position);
            await store.SaveGeoSourceAsync(row, ct).ConfigureAwait(false);
            held.Add(row);
            held.Sort((one, other) => one.Position.CompareTo(other.Position));
            added = true;
            logger?.LogInformation("added the standard rule database {Name} from {Url}; country and service rules are matched against it", name, source.Url);
            await UnpackAsync(store, files, name, source, logger, ct).ConfigureAwait(false);
        }

        await store.SetSettingAsync(SeedVersionKey, SeedVersion.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return added;
    }

    // Отвечает, тот ли это источник по умолчанию: по имени или по адресу.
    private static bool Same(GeoSource row, GeoDefaultSource source) =>
        (source.Name.Length > 0 && string.Equals(row.Name, source.Name, StringComparison.Ordinal))
        || string.Equals(row.Url, source.Url, StringComparison.OrdinalIgnoreCase);

    // Возвращает место источника: перед первым стандартным, который идёт за ним в наборе, иначе последним.
    private static int Place(List<GeoSource> held, GeoDefaultSource source)
    {
        foreach (var next in Sources.SkipWhile(one => one != source).Skip(1))
        {
            var found = held.Find(row => Same(row, next));
            if (found is not null)
            {
                return found.Position;
            }
        }

        return held.Count == 0 ? 1 : held.Max(row => row.Position) + 1;
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
