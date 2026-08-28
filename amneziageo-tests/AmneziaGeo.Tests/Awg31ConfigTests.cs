using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// AmneziaWG 3.1 adds nine [Interface] keys. A client that does not know them drops them on import without a
/// word and the tunnel never comes up, so the validator, the UAPI conversion and the editor round trip are
/// pinned here.
/// </summary>
public sealed class Awg31ConfigTests
{
    private const string PrivateKey = "QW1uZXppYUdlbyB0ZXN0IGludGVyZmFjZSBrZXkxISE=";
    private const string PeerKey = "QW1uZXppYUdlbyB0ZXN0IHBlZXIgcHVibGlja2V5MDE=";
    private const string HeaderKey = "QW1uZXppYUdlbyBkZXNpZ24tdGltZSBzYW1wbGVrZXk=";

    private static readonly string[] Awg31Lines =
    [
        $"HeaderProtectionKey = {HeaderKey}",
        "ContentPaddingAddition = 12-44",
        "RekeyAfterTime = 100-135",
        "RekeyTimeout = 5-6",
        "RejectAfterTime = 186-259",
        "KeepaliveTimeout = 12-16",
        "MaxHandshakeAttempts = 17-33",
        "RandomTrailers = on",
        "DisableCookies = on",
    ];

    private static string Conf(params string[] interfaceExtra)
    {
        var lines = new List<string>
        {
            "[Interface]",
            $"PrivateKey = {PrivateKey}",
            "Address = 10.0.0.2/32, fd00::2/128",
            "DNS = 1.1.1.1",
            "MTU = 1420",
            "Jc = 4",
            "Jmin = 40",
            "Jmax = 70",
            "S1 = 17",
            "S2 = 23",
            "S3 = 17",
            "S4 = 22",
            "H1 = 1234567",
            "H2 = 2345678",
            "H3 = 3456789",
            "H4 = 4567890",
        };

        lines.AddRange(interfaceExtra);
        lines.AddRange(
        [
            string.Empty,
            "[Peer]",
            $"PublicKey = {PeerKey}",
            "AllowedIPs = 0.0.0.0/0",
            "Endpoint = 203.0.113.7:51820",
            "PersistentKeepalive = 25",
        ]);

        return string.Join('\n', lines);
    }

    [Fact]
    public void FullInterface_Validates()
    {
        WgConfigValidator.Validate(Conf(Awg31Lines));
    }

    [Fact]
    public void HeaderProtectionKey_TakesAnEmptyValue()
    {
        WgConfigValidator.Validate(Conf("HeaderProtectionKey ="));
    }

    [Theory]
    [InlineData("QW1uZXppYUdlbw==")]
    [InlineData("not base64 at all")]
    public void HeaderProtectionKey_ThatIsNotThirtyTwoBytes_IsRejected(string value)
    {
        Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(Conf($"HeaderProtectionKey = {value}")));
    }

    [Theory]
    [InlineData("RekeyAfterTime = 120")]
    [InlineData("ContentPaddingAddition = 0-4096")]
    [InlineData("MaxHandshakeAttempts = 17-17")]
    public void Range_ScalarOrOrderedPair_IsAccepted(string line)
    {
        WgConfigValidator.Validate(Conf(line));
    }

    [Theory]
    [InlineData("RekeyAfterTime = 135-100")]
    [InlineData("RekeyTimeout = 5-6-7")]
    [InlineData("RejectAfterTime = -12")]
    [InlineData("KeepaliveTimeout = soon")]
    public void Range_ThatTheEngineWouldRefuse_IsRejected(string line)
    {
        Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(Conf(line)));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("no")]
    [InlineData("1")]
    public void Flag_InEverySpellingTheEngineTakes_IsAccepted(string value)
    {
        WgConfigValidator.Validate(Conf($"RandomTrailers = {value}"));
    }

    [Fact]
    public void Flag_WithATypo_IsRejected()
    {
        // Молчаливый дефолт оставил бы обфускацию выключенной.
        Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(Conf("DisableCookies = enabled")));
    }

    [Fact]
    public void UnknownKey_IsMarkedUnknownAndNamed()
    {
        var ex = Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(Conf("HeaderProtectionKeys = 1")));

        Assert.True(ex.UnknownKey);
        Assert.Equal("headerprotectionkeys", ex.Offender);
    }

    [Fact]
    public void KnownKeyWithABadValue_IsNotMarkedUnknown()
    {
        // Пользователь получает разные сообщения: обновить клиент либо поправить значение.
        var ex = Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(Conf("RandomTrailers = enabled")));

        Assert.False(ex.UnknownKey);
    }

    [Fact]
    public void Uapi_CarriesEveryNewKey()
    {
        var uapi = WgQuickToUapi.Convert(Conf(Awg31Lines));

        Assert.NotNull(uapi);
        Assert.Contains("content_padding_addition=12-44\n", uapi);
        Assert.Contains("rekey_after_time=100-135\n", uapi);
        Assert.Contains("rekey_timeout=5-6\n", uapi);
        Assert.Contains("reject_after_time=186-259\n", uapi);
        Assert.Contains("keepalive_timeout=12-16\n", uapi);
        Assert.Contains("max_handshake_attempts=17-33\n", uapi);
    }

    [Fact]
    public void Uapi_HeaderProtectionKey_GoesOutAsHex()
    {
        var uapi = WgQuickToUapi.Convert(Conf(Awg31Lines));
        var hex = Convert.ToHexStringLower(Convert.FromBase64String(HeaderKey));

        Assert.NotNull(uapi);
        Assert.Contains($"header_protection_key={hex}\n", uapi);
        Assert.DoesNotContain(HeaderKey, uapi);
    }

    [Theory]
    [InlineData("on", "1")]
    [InlineData("true", "1")]
    [InlineData("yes", "1")]
    [InlineData("off", "0")]
    [InlineData("false", "0")]
    [InlineData("no", "0")]
    public void Uapi_Flag_BecomesTheDigitParseBoolTakes(string value, string expected)
    {
        // Движок читает их через strconv.ParseBool, который не знает on/off.
        var uapi = WgQuickToUapi.Convert(Conf($"RandomTrailers = {value}"));

        Assert.NotNull(uapi);
        Assert.Contains($"random_trailers={expected}\n", uapi);
    }

    [Fact]
    public void EditorRoundTrip_KeepsEveryNewKey()
    {
        var config = Conf(Awg31Lines);

        var edited = WgConfigEditor.StripIpv6Addresses(config);
        edited = WgConfigEditor.SetDns(edited, ["9.9.9.9"]);
        edited = WgConfigEditor.SetMtu(edited, 1400);
        edited = WgConfigEditor.ApplyAllowedIps(edited, ["10.0.0.0/24", "192.168.1.0/24"]);
        edited = WgConfigEditor.SetEndpoint(edited, "198.51.100.9:443");
        edited = WgConfigEditor.EnsurePersistentKeepalive(edited, 25);

        var lines = edited.Split('\n').Select(line => line.Trim()).ToList();
        foreach (var expected in Awg31Lines)
        {
            Assert.Contains(expected, lines);
        }

        WgConfigValidator.Validate(edited);
        Assert.NotNull(WgQuickToUapi.Convert(edited));
    }

    [Fact]
    public void EditorRoundTrip_StillAppliesTheEdits()
    {
        var config = Conf(Awg31Lines);

        var edited = WgConfigEditor.StripIpv6Addresses(config);
        edited = WgConfigEditor.SetDns(edited, ["9.9.9.9"]);
        edited = WgConfigEditor.SetMtu(edited, 1400);
        edited = WgConfigEditor.SetEndpoint(edited, "198.51.100.9:443");

        Assert.Equal(["10.0.0.2/32"], WgConfigEditor.GetAddresses(edited));
        Assert.Equal(["9.9.9.9"], WgConfigEditor.GetDns(edited));
        Assert.Equal(1400, WgConfigEditor.GetMtu(edited));
        Assert.Equal("198.51.100.9:443", WgConfigEditor.GetEndpoint(edited));
    }

    [Theory]
    [InlineData("25")]
    [InlineData("10-30")]
    [InlineData("off")]
    [InlineData("(off)")]
    public void PersistentKeepalive_InEveryFormTheEngineReads_IsAccepted(string value)
    {
        WgConfigValidator.Validate(WithKeepalive(value));
    }

    [Theory]
    [InlineData("30-10")]
    [InlineData("soon")]
    [InlineData("5-6-7")]
    public void PersistentKeepalive_ThatTheEngineWouldRefuse_IsRejected(string value)
    {
        Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(WithKeepalive(value)));
    }

    [Fact]
    public void Uapi_KeepaliveRange_ReachesTheDeviceIntact()
    {
        var uapi = WgQuickToUapi.Convert(WithKeepalive("10-30"));

        Assert.NotNull(uapi);
        Assert.Contains("persistent_keepalive_interval=10-30\n", uapi);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("(off)")]
    public void Uapi_KeepaliveOff_GoesOutAsZero(string value)
    {
        // UintRange.FromString reads a number; the word would fail the whole set.
        var uapi = WgQuickToUapi.Convert(WithKeepalive(value));

        Assert.NotNull(uapi);
        Assert.Contains("persistent_keepalive_interval=0\n", uapi);
    }

    private static string WithKeepalive(string value)
    {
        return Conf(Awg31Lines).Replace("PersistentKeepalive = 25", $"PersistentKeepalive = {value}", StringComparison.Ordinal);
    }
}
