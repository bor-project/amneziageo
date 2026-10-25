using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Регион устройства без запроса прав: внешний адрес, часовой пояс, настройка системы.
/// </summary>
internal static class RegionProbe
{
    private const string ZoneResource = "AmneziaGeo.Ui.Services.ZoneRegions.txt";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    // Отвечают кодом страны по адресу, с которого пришёл запрос.
    private static readonly (string Url, bool Trace)[] Probes =
    [
        ("https://www.cloudflare.com/cdn-cgi/trace", true),
        ("https://api.country.is/", false),
    ];

    private static Dictionary<string, string>? _zones;

    /// <summary>
    /// Код региона по порядку источников: адрес, часовой пояс, система.
    /// </summary>
    public static async Task<string> DetectAsync(bool allowNetwork, CancellationToken ct)
    {
        if (allowNetwork && await ByAddressAsync(ct) is { Length: 2 } address)
        {
            return address;
        }

        return ByTimeZone() is { Length: 2 } zone ? zone : BySystem();
    }

    /// <summary>
    /// Регион по настройке системы.
    /// </summary>
    public static string BySystem()
    {
        var geo = WindowsGeoName();
        return geo.Length == 2 ? geo : RoutingPresets.CurrentCountry();
    }

    private static async Task<string> ByAddressAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = ProbeTimeout };
        foreach (var probe in Probes)
        {
            try
            {
                var text = await http.GetStringAsync(probe.Url, ct);
                var code = probe.Trace ? TraceCountry(text) : JsonCountry(text);
                if (code.Length == 2)
                {
                    return code.ToLowerInvariant();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException or UriFormatException)
            {
            }
        }

        return string.Empty;
    }

    private static string TraceCountry(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
            {
                return line[4..].Trim();
            }
        }

        return string.Empty;
    }

    private static string JsonCountry(string text)
    {
        using var json = JsonDocument.Parse(text);
        return json.RootElement.TryGetProperty("country", out var country) ? country.GetString() ?? string.Empty : string.Empty;
    }

    private static string ByTimeZone()
    {
        var id = TimeZoneInfo.Local.Id;
        if (Zone(id) is { Length: 2 } direct)
        {
            return direct;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? Zone(iana) : string.Empty;
    }

    private static string Zone(string id) => Zones().GetValueOrDefault(id, string.Empty);

    private static Dictionary<string, string> Zones()
    {
        if (_zones is not null)
        {
            return _zones;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(RegionProbe).Assembly.GetManifestResourceStream(ZoneResource);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var split = line.IndexOf('=');
                    if (split > 0)
                    {
                        map[line[..split]] = line[(split + 1)..];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        _zones = map;
        return map;
    }

    // Настройка «Страна или регион» Windows: язык интерфейса на неё не влияет.
    private static string WindowsGeoName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            var buffer = new char[16];
            var written = GetUserDefaultGeoName(buffer, buffer.Length);
            var name = written > 1 ? new string(buffer, 0, written - 1) : string.Empty;
            return name.Length == 2 ? name.ToLowerInvariant() : string.Empty;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return string.Empty;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName([Out] char[] geoName, int geoNameCount);
}
