using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Per-user UI preferences persisted across launches.
/// </summary>
internal sealed class UiPreferences
{
    /// <summary>
    /// Тема: пусто = системная, "light" или "dark".
    /// </summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>
    /// Legacy: булев флаг тёмной темы; читается только для миграции в Theme.
    /// </summary>
    [JsonPropertyName("IsDark")]
    public bool? IsDark { get; set; }

    /// <summary>
    /// Window width.
    /// </summary>
    public double Width { get; set; } = 987;

    /// <summary>
    /// Window height.
    /// </summary>
    public double Height { get; set; } = 610;

    /// <summary>
    /// Legacy left-rail splitter width; no longer read.
    /// </summary>
    public double RailWidth { get; set; } = 377;

    /// <summary>
    /// The selected settings section.
    /// </summary>
    public string SettingsSection { get; set; } = "profile";

    /// <summary>
    /// The UI language token; empty follows the system language.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The last active profile name.
    /// </summary>
    public string LastProfile { get; set; } = string.Empty;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "ui-prefs.json");

    /// <summary>
    /// Loads the saved preferences, or defaults when absent.
    /// </summary>
    public static UiPreferences Load()
    {
        try
        {
            var prefs = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath)) ?? new UiPreferences();
            if (string.IsNullOrEmpty(prefs.Theme) && prefs.IsDark.HasValue)
            {
                prefs.Theme = prefs.IsDark.Value ? "dark" : "light";
            }

            prefs.IsDark = null;
            return prefs;
        }
        catch
        {
            return new UiPreferences();
        }
    }

    /// <summary>
    /// Writes the preferences back.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch
        {
        }
    }
}
