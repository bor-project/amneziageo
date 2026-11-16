using System.Text.Json;
using AmneziaGeo.Ui.Services;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class UiPreferencesPersistenceTests
{
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
