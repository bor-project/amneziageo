using System.Text.Json;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hidden logging settings kept in logs\settings.json: the per-table retention cap. Not surfaced in the UI.
/// </summary>
internal sealed class LogSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Maximum rows kept per log table; older rows are pruned past it.
    /// </summary>
    public int MaxRowsPerTable { get; init; } = 200_000;

    /// <summary>
    /// Loads the settings file, writing defaults only when it is absent; a present but unreadable file is kept intact.
    /// </summary>
    public static LogSettings LoadOrCreate(string path)
    {
        var exists = File.Exists(path);
        if (exists)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<LogSettings>(File.ReadAllText(path), Json);
                if (loaded is { MaxRowsPerTable: > 0 })
                {
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        var defaults = new LogSettings();
        if (!exists)
        {
            Save(path, defaults);
        }

        return defaults;
    }

    private static void Save(string path, LogSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
