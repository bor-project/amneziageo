using System;
using System.IO;
using System.Text.Json;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Per-user UI preferences persisted across launches (#51): theme (dark/light), window size, the left-rail
/// splitter width, and the selected settings section. Stored as a small JSON file under
/// %LOCALAPPDATA%\AmneziaGeo - deliberately NOT the agent's machine-wide state.db, since these are per-user
/// presentation preferences (two users on one machine must not share a theme or window size) - and read
/// synchronously before the window is shown, so a restored theme/size produces no flicker. Language is not
/// persisted yet (there is no in-app locale switcher); window position is left to the OS. Best-effort:
/// read/write failures fall back to defaults and are swallowed.
/// </summary>
internal sealed class UiPreferences
{
    /// <summary>Dark theme when true, light when false.</summary>
    public bool IsDark { get; set; }

    /// <summary>Window width; the #22 golden-ratio default.</summary>
    public double Width { get; set; } = 987;

    /// <summary>Window height; the #22 golden-ratio default.</summary>
    public double Height { get; set; } = 610;

    /// <summary>The left rail's pixel width (the #50 splitter position); the rail column default.</summary>
    public double RailWidth { get; set; } = 377;

    /// <summary>The selected settings section: "general" | "sources" | "logs" | "about".</summary>
    public string SettingsSection { get; set; } = "general";

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
