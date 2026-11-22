using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Ui.Services;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class UiPreferencesPersistenceTests
{
    [Fact]
    public void DefaultPathsUseTheTestDirectory()
    {
        var preferences = new UiPreferences();
        Assert.Equal(TestStorage.Root, Path.GetDirectoryName(preferences.DbPath));
        Assert.Equal(TestStorage.Root, Path.GetDirectoryName(preferences.LegacyFilePath));
        Assert.Equal(TestStorage.Root, Path.GetDirectoryName(preferences.LegacyMarkerPath));
    }

    [Fact]
    public void SavesAndLoadsPreferencesInTheSelectedDirectory()
    {
        var directory = NewDirectory();
        var preferences = new UiPreferences(true, directory) { Theme = "dark", LastConfig = "test-vpn", LastRoutingList = 7 };
        preferences.Save();
        var loaded = UiPreferences.Load(directory);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("test-vpn", loaded.LastConfig);
        Assert.Equal(7, loaded.LastRoutingList);
        loaded.Language = "ru";
        loaded.Save();
        Assert.Equal("ru", UiPreferences.Load(directory).Language);
    }

    [Fact]
    public void ImportsLegacyPreferencesOnlyFromTheSelectedDirectory()
    {
        var directory = NewDirectory();
        var legacy = Path.Combine(directory, "ui-prefs.json");
        File.WriteAllText(legacy, """{"IsDark":true,"Language":"ru","Width":1200}""");
        var preferences = UiPreferences.Load(directory);
        Assert.Equal("dark", preferences.Theme);
        Assert.Equal("ru", preferences.Language);
        Assert.Equal(1200, preferences.Width);
        Assert.False(File.Exists(legacy));
        Assert.Equal("dark", new LocalKeyValueStore(preferences.DbPath).Read("ui", "theme"));
    }

    [Fact]
    public void SaveDeletesOnlyTheSelectedDirectoryMarker()
    {
        var directory = NewDirectory();
        var other = NewDirectory();
        var preferences = new UiPreferences(true, directory);
        File.WriteAllText(preferences.LegacyMarkerPath, "ru");
        var untouched = Path.Combine(other, "tray-lang");
        File.WriteAllText(untouched, "en");
        preferences.Save();
        Assert.False(File.Exists(preferences.LegacyMarkerPath));
        Assert.Equal("en", File.ReadAllText(untouched));
    }

    [Fact]
    public void NonPersistentPreferencesDoNotCreateFiles()
    {
        var directory = Path.Combine(TestStorage.Root, Guid.NewGuid().ToString("N"));
        new UiPreferences(false, directory) { Theme = "dark" }.Save();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void LoadingMissingPreferencesCreatesAnIsolatedStore()
    {
        var directory = Path.Combine(TestStorage.Root, Guid.NewGuid().ToString("N"));
        var preferences = UiPreferences.Load(directory);
        Assert.Equal(string.Empty, preferences.Theme);
        Assert.True(File.Exists(preferences.DbPath));
    }

    private static string NewDirectory() => Directory.CreateDirectory(
        Path.Combine(TestStorage.Root, Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void NonPersistentPreferencesRemainUsableAcrossSaves()
    {
        var preferences = new UiPreferences(persist: false) { SettingsSection = "config", LastConfig = "vpn" };
        preferences.Save();
        preferences.SettingsSection = "proxies";
        preferences.Theme = "dark";
        preferences.Save();

        Assert.Equal("proxies", preferences.SettingsSection);
        Assert.Equal("vpn", preferences.LastConfig);
        Assert.Equal("dark", preferences.Theme);
    }

    [Fact]
    public void LegacyJsonUsesThePublicParameterlessConstructor()
    {
        var preferences = JsonSerializer.Deserialize<UiPreferences>("""
            {"Theme":"dark","SettingsSection":"config","LastConfig":"saved-vpn","AcceptedTerms":7}
            """);

        Assert.NotNull(preferences);
        Assert.Equal("dark", preferences.Theme);
        Assert.Equal("config", preferences.SettingsSection);
        Assert.Equal("saved-vpn", preferences.LastConfig);
        Assert.Equal(7, preferences.AcceptedTerms);
        Assert.True(preferences.RoutingWarnings);
    }
}
