using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The editor and the agent decide by the same rules, so a name or a password the window accepts is one the
/// agent stores and hostapd or the system tethering will take.
/// </summary>
public sealed class HotspotSettingsTests
{
    [Theory]
    [InlineData("lan", "lan")]
    [InlineData("wifi", "wifi")]
    [InlineData("both", "both")]
    [InlineData("BOTH", "both")]
    [InlineData("  lan  ", "lan")]
    public void ShareMode_ReadsKnownTokens(string text, string expected)
    {
        Assert.Equal(expected, ShareModes.Of(text));
        Assert.True(ShareModes.IsKnown(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ethernet")]
    [InlineData(null)]
    public void ShareMode_FallsBackToBoth(string? text)
    {
        Assert.Equal(ShareModes.Both, ShareModes.Of(text));
        Assert.False(ShareModes.IsKnown(text));
    }

    [Fact]
    public void ShareMode_CarriesWhatItNames()
    {
        Assert.True(ShareModes.CarriesLan(ShareModes.Lan));
        Assert.False(ShareModes.CarriesWifi(ShareModes.Lan));
        Assert.False(ShareModes.CarriesLan(ShareModes.Wifi));
        Assert.True(ShareModes.CarriesWifi(ShareModes.Wifi));
        Assert.True(ShareModes.CarriesLan(ShareModes.Both));
        Assert.True(ShareModes.CarriesWifi(ShareModes.Both));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("2.4", "2.4")]
    [InlineData("5", "5")]
    [InlineData("6", "auto")]
    [InlineData("", "auto")]
    public void Band_ReadsKnownTokens(string text, string expected)
    {
        Assert.Equal(expected, HotspotBands.Of(text));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("AmneziaGeo")]
    [InlineData("12345678901234567890123456789012")]
    public void Ssid_TakesWhatABeaconCarries(string name)
    {
        Assert.True(SettingKeys.IsValidHotspotSsid(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("name\twith a tab")]
    public void Ssid_RefusesTheRest(string name)
    {
        Assert.False(SettingKeys.IsValidHotspotSsid(name));
    }

    [Fact]
    public void Ssid_CountsBytesNotCharacters()
    {
        // Seventeen Cyrillic characters are 34 bytes, and a beacon carries 32.
        Assert.False(SettingKeys.IsValidHotspotSsid(new string('а', 17)));
        Assert.True(SettingKeys.IsValidHotspotSsid(new string('а', 16)));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("a password with spaces")]
    public void Password_TakesWhatWpa2Takes(string password)
    {
        Assert.True(SettingKeys.IsValidHotspotPassword(password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    public void Password_RefusesWhatWpa2Refuses(string password)
    {
        Assert.False(SettingKeys.IsValidHotspotPassword(password));
    }

    [Fact]
    public void Password_StopsAt63()
    {
        Assert.True(SettingKeys.IsValidHotspotPassword(new string('x', 63)));
        Assert.False(SettingKeys.IsValidHotspotPassword(new string('x', 64)));
    }

    [Fact]
    public void Options_ReadTheStoredSettings()
    {
        var options = HotspotOptions.Read(new Dictionary<string, string>
        {
            [SettingKeys.ProxyEnabled] = "on",
            [SettingKeys.ShareMode] = ShareModes.Both,
            [SettingKeys.HotspotSsid] = "AmneziaGeo",
            [SettingKeys.HotspotPassword] = "12345678",
            [SettingKeys.HotspotBand] = HotspotBands.Five,
        });

        Assert.True(options.Enabled);
        Assert.True(options.Complete);
        Assert.True(options.Wanted);
        Assert.Equal("AmneziaGeo", options.Ssid);
        Assert.Equal(HotspotBands.Five, options.Band);
    }

    [Fact]
    public void Options_StayDownWhileTheProxyIs()
    {
        var options = HotspotOptions.Read(new Dictionary<string, string>
        {
            [SettingKeys.ShareMode] = ShareModes.Wifi,
            [SettingKeys.HotspotSsid] = "AmneziaGeo",
            [SettingKeys.HotspotPassword] = "12345678",
        });

        Assert.False(options.Enabled);
        Assert.False(options.Wanted);
    }

    [Fact]
    public void Options_StayDownForTheLanMode()
    {
        var options = HotspotOptions.Read(new Dictionary<string, string>
        {
            [SettingKeys.ProxyEnabled] = "on",
            [SettingKeys.ShareMode] = ShareModes.Lan,
            [SettingKeys.HotspotSsid] = "AmneziaGeo",
            [SettingKeys.HotspotPassword] = "12345678",
        });

        Assert.False(options.Wanted);
    }

    /// <summary>
    /// An installation updated into this version has the proxy on and no access point set. The default mode
    /// names both ways, and that must not raise a point behind the user's back.
    /// </summary>
    [Fact]
    public void Options_RaiseNothingOnAnUpdate()
    {
        var options = HotspotOptions.Read(new Dictionary<string, string>
        {
            [SettingKeys.ProxyEnabled] = "on",
        });

        Assert.Equal(ShareModes.Both, ShareModes.Of(null));
        Assert.True(options.Enabled);
        Assert.False(options.Complete);
        Assert.False(options.Wanted);
    }
}
