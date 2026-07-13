using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The result of an application update check.
/// </summary>
internal sealed record UpdateInfo(bool Available, string Version, string SetupUrl, string Description);

/// <summary>
/// Checks an HTTP update metadata file for a different version.
/// </summary>
internal sealed class UpdateChecker(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<UpdateInfo?> CheckAsync(string metadataUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            return null;
        }

        var json = await http.GetStringAsync(metadataUrl, ct);
        var meta = JsonSerializer.Deserialize<UpdateMetadata>(json, JsonOptions);
        if (meta is null || string.IsNullOrWhiteSpace(meta.Version))
        {
            return null;
        }

        var setup = string.IsNullOrWhiteSpace(meta.Setup) ? "AmneziaGeoSetup.exe" : meta.Setup;
        var setupUrl = new Uri(new Uri(metadataUrl), setup).ToString();

        return new UpdateInfo(IsNewer(meta.Version, currentVersion), meta.Version, setupUrl, meta.Description ?? string.Empty);
    }

    // Only a strictly newer remote counts as an update: Burn refuses a bundle downgrade (0x80070666), so
    // offering one would only fail silently.
    private static bool IsNewer(string remote, string current)
    {
        if (System.Version.TryParse(remote, out var r) && System.Version.TryParse(current, out var c))
        {
            return r.CompareTo(c) > 0;
        }

        return !string.Equals(remote.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UpdateMetadata(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("setup")] string? Setup);
}
