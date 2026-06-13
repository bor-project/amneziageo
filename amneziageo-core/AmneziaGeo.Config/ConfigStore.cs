using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Config;

public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, Options, ct).ConfigureAwait(false);
            return config ?? new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, config, Options, ct).ConfigureAwait(false);
        }
    }
}
