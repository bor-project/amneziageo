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

        await using (var stream = File.OpenRead(path))
        {
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, Options, ct);
            return config ?? new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, config, Options, ct);
        }
    }
}
