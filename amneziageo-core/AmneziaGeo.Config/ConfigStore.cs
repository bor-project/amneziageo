using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Config;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON.
/// </summary>
public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the config, or returns defaults when the file is absent.
    /// </summary>
    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, _options, ct).ConfigureAwait(false);
            return config ?? new AppConfig();
        }
    }

    /// <summary>
    /// Writes the config to disk.
    /// </summary>
    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, config, _options, ct).ConfigureAwait(false);
        }
    }
}
