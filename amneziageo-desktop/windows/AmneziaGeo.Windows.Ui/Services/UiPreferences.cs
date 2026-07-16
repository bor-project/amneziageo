using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmneziaGeo.Dal;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Per-user UI preferences persisted in the state database across launches.
/// </summary>
internal sealed class UiPreferences
{
    private const string Scope = "ui";

    /// <summary>
    /// Theme: empty = follow the system, otherwise "light" or "dark".
    /// </summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>
    /// Window width.
    /// </summary>
    public double Width { get; set; } = 987;

    /// <summary>
    /// Window height.
    /// </summary>
    public double Height { get; set; } = 610;

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

    private static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "state.db");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "ui-prefs.json");

    private static string LanguageMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "tray-lang");

    /// <summary>
    /// Loads the saved preferences, or defaults when absent.
    /// </summary>
    public static UiPreferences Load()
    {
        try
        {
            var store = new LocalKeyValueStore(DbPath);
            var values = store.Load(Scope);
            if (values.Count == 0 && TryImportLegacy(store, out var imported))
            {
                return imported;
            }

            return FromValues(values);
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
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            new LocalKeyValueStore(DbPath).Save(Scope, ToValues());
            WriteLanguageMarker();
        }
        catch
        {
        }
    }

    // Mirrors the UI language token to a tiny marker the native tray reads, keeping the tray free of the SQLite
    // state store where the app now keeps its preferences.
    private void WriteLanguageMarker()
    {
        try
        {
            File.WriteAllText(LanguageMarkerPath, Language ?? string.Empty);
        }
        catch (IOException)
        {
        }
    }

    private Dictionary<string, string> ToValues()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["theme"] = Theme,
            ["width"] = Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = Height.ToString(CultureInfo.InvariantCulture),
            ["settings-section"] = SettingsSection,
            ["language"] = Language,
            ["last-profile"] = LastProfile,
        };
    }

    private static UiPreferences FromValues(IReadOnlyDictionary<string, string> values)
    {
        var prefs = new UiPreferences();
        if (values.TryGetValue("theme", out var theme))
        {
            prefs.Theme = theme;
        }

        if (values.TryGetValue("width", out var width) && double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
        {
            prefs.Width = w;
        }

        if (values.TryGetValue("height", out var height) && double.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0)
        {
            prefs.Height = h;
        }

        if (values.TryGetValue("settings-section", out var section) && !string.IsNullOrEmpty(section))
        {
            prefs.SettingsSection = section;
        }

        if (values.TryGetValue("language", out var language))
        {
            prefs.Language = language;
        }

        if (values.TryGetValue("last-profile", out var lastProfile))
        {
            prefs.LastProfile = lastProfile;
        }

        return prefs;
    }

    // One-time import of the pre-DB ui-prefs.json, then delete it so the settings folder is left clean.
    private static bool TryImportLegacy(LocalKeyValueStore store, out UiPreferences prefs)
    {
        prefs = new UiPreferences();
        var path = LegacyFilePath;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyPrefs>(File.ReadAllText(path));
            if (legacy is null)
            {
                return false;
            }

            prefs.Theme = ResolveTheme(legacy);
            prefs.Width = legacy.Width > 0 ? legacy.Width : prefs.Width;
            prefs.Height = legacy.Height > 0 ? legacy.Height : prefs.Height;
            prefs.SettingsSection = string.IsNullOrEmpty(legacy.SettingsSection) ? prefs.SettingsSection : legacy.SettingsSection;
            prefs.Language = legacy.Language ?? string.Empty;
            prefs.LastProfile = legacy.LastProfile ?? string.Empty;

            store.Save(Scope, prefs.ToValues());
            prefs.WriteLanguageMarker();
            TryDelete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveTheme(LegacyPrefs legacy)
    {
        if (!string.IsNullOrEmpty(legacy.Theme))
        {
            return legacy.Theme;
        }

        return legacy.IsDark switch
        {
            true => "dark",
            false => "light",
            null => string.Empty,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class LegacyPrefs
    {
        public string? Theme { get; set; }

        [JsonPropertyName("IsDark")]
        public bool? IsDark { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public string? SettingsSection { get; set; }

        public string? Language { get; set; }

        public string? LastProfile { get; set; }
    }
}
