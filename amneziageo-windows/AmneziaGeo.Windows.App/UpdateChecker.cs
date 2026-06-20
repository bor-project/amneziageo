using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The result of an application update check.
/// </summary>
internal sealed record UpdateInfo(bool Available, string Version, string SetupUrl, string Description, bool IsDowngrade);

/// <summary>
/// Checks an HTTP-hosted update metadata file for a different application version. The metadata is a
/// small JSON document (version / description / setup) sitting next to the installer; the setup field
/// is resolved relative to the metadata URL. A version that merely differs (newer OR older) counts as
/// available, since the installer permits downgrade (rollback).
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

        var (available, downgrade) = Compare(meta.Version, currentVersion);
        return new UpdateInfo(available, meta.Version, setupUrl, meta.Description ?? string.Empty, downgrade);
    }

    private static (bool Available, bool Downgrade) Compare(string remote, string current)
    {
        if (System.Version.TryParse(remote, out var r) && System.Version.TryParse(current, out var c))
        {
            var cmp = r.CompareTo(c);
            return (cmp != 0, cmp < 0);
        }

        // Unparseable versions: treat any string difference as an available (forward) update.
        return (!string.Equals(remote.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase), false);
    }

    private sealed record UpdateMetadata(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("setup")] string? Setup);
}
