using System.Text.Json;
using System.Text.Json.Serialization;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loads per-tunnel geo settings from a JSON sidecar.
/// </summary>
internal static class GeoStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads geo settings for a tunnel, or defaults when absent.
    /// </summary>
    public static GeoSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new GeoSettings(false, []);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GeoSettings>(json, _options) ?? new GeoSettings(false, []);
    }
}
