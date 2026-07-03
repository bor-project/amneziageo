using System;
using System.IO;
using System.Text.Json;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Per-user UI preferences persisted across launches (#51): theme (dark/light), window size, the left-rail
/// splitter width, and the selected settings section. Stored as a small JSON file under
/// %LOCALAPPDATA%\AmneziaGeo - deliberately NOT the agent's machine-wide state.db, since these are per-user
/// presentation preferences (two users on one machine must not share a theme or window size) - and read
/// synchronously before the window is shown, so a restored theme/size produces no flicker, and the UI
/// language (#106) so the chosen culture is applied before the first window. Window position is left to the
/// OS. Best-effort: read/write failures fall back to defaults and are swallowed.
/// </summary>
internal sealed class UiPreferences
{
    /// <summary>Dark theme when true, light when false.</summary>
    public bool IsDark { get; set; }

    /// <summary>Window width; the #22 golden-ratio default.</summary>
    public double Width { get; set; } = 987;

    /// <summary>Window height; the #22 golden-ratio default.</summary>
    public double Height { get; set; } = 610;

    /// <summary>
    /// Legacy: the left-rail splitter width. The sidebar was removed in the profile-presets shell refactor,
    /// so this is no longer read; kept only so older ui-prefs.json round-trips without losing other keys.
    /// </summary>
    public double RailWidth { get; set; } = 377;

    /// <summary>The selected settings section: "profile" | "config" | "routing" | "sources" | "logs" | "general".</summary>
    public string SettingsSection { get; set; } = "profile";

    /// <summary>
    /// The UI language token (#106): "" follows the system UI language, otherwise a supported code ("ru" /
    /// "en"). Read at startup to pick the culture (system -> English fallback) before any window shows.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The name of the last profile the user had active, restored on launch so the main window opens on the
    /// same profile (the connect button stays disabled until a profile is selected). Empty when none.
    /// </summary>
    public string LastProfile { get; set; } = string.Empty;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "ui-prefs.json");

    /// <summary>Loads the saved preferences, or defaults when absent/unreadable.</summary>
    public static UiPreferences Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath)) ?? new UiPreferences();
        }
        catch
        {
            return new UiPreferences();
        }
    }

    /// <summary>Writes the preferences back (best-effort; a failure just loses this round).</summary>
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
            // best-effort
        }
    }
}
