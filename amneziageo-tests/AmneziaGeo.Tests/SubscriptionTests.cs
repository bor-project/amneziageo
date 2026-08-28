using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Подписка x-ui - список ссылок через перевод строки, при включённом subEncrypt всё тело в base64.
/// В одном списке лежат инбаунды всех протоколов, наши - только два.
/// </summary>
public sealed class SubscriptionTests
{
    private const string ClientKey = "QW1uZXppYUdlbyBzdWIgY2xpZW50IGtleSAwMDAwMSE=";
    private const string ServerKey = "QW1uZXppYUdlbyBzdWIgc2VydmVyIGtleSAwMDAwMSE=";
    private const string PresharedKey = "QW1uZXppYUdlbyBzdWIgc2hhcmVkIGtleSAwMDAwMSE=";
    private const string Foreign = "vless://11111111-2222-3333-4444-555555555555@example.net:2096?type=xhttp&security=tls#vless";

    private static string AwgConfig()
    {
        return string.Join('\n',
        [
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.0.1.2/32",
            "Jc = 6",
            "Jmin = 52",
            "Jmax = 241",
            "S1 = 63",
            "S2 = 149",
            "H1 = 194488238-453280017",
            "H2 = 945380663-959625713",
            "H3 = 1220926369-1460941108",
            "H4 = 2008138652-2111657743",
            "RandomTrailers = on",
            string.Empty,
            "# AmneziaWG 3.1 -bor_android_phone",
            "[Peer]",
            $"PublicKey = {ServerKey}",
            "AllowedIPs = 0.0.0.0/0, ::/0",
            "Endpoint = example.net:51821",
            "PersistentKeepalive = 25",
        ]);
    }

    private static string AwgLink()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(AwgConfig()));
        return "vpn://" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Панель складывает запрос через url.Values.Encode: ключи по алфавиту, base64 экранирован.
    private static string WireguardLink()
    {
        return "wireguard://" + Uri.EscapeDataString(ClientKey) + "@example.net:51820"
            + "?address=" + Uri.EscapeDataString("10.0.0.2/32")
            + "&dns=1.1.1.1"
            + "&keepalive=25"
            + "&mtu=1420"
            + "&presharedkey=" + Uri.EscapeDataString(PresharedKey)
            + "&publickey=" + Uri.EscapeDataString(ServerKey)
            + "#AmneziaWG%202%20-bor_laptop";
    }

    [Fact]
    public void WireguardLink_BecomesTheSameConfig()
    {
        var expected = string.Join('\n',
        [
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.0.0.2/32",
            "DNS = 1.1.1.1",
            "MTU = 1420",
            string.Empty,
            "[Peer]",
            $"PublicKey = {ServerKey}",
            $"PresharedKey = {PresharedKey}",
            "AllowedIPs = 0.0.0.0/0, ::/0",
            "Endpoint = example.net:51820",
            "PersistentKeepalive = 25",
        ]);

        var imported = VpnLinkCodec.TryDecode(WireguardLink());

        Assert.NotNull(imported);
        Assert.Equal(expected, imported!.ConfText);
        Assert.Equal("AmneziaWG-2-bor_laptop", imported.Name);
        WgConfigValidator.Validate(imported.ConfText);
        Assert.NotNull(WgQuickToUapi.Convert(imported.ConfText));
    }

    [Fact]
    public void WireguardLink_WithoutTheOptionalParameters_IsStillAConfig()
    {
        var link = "wireguard://" + Uri.EscapeDataString(ClientKey) + "@example.net:51820"
            + "?address=" + Uri.EscapeDataString("10.0.0.2/32")
            + "&publickey=" + Uri.EscapeDataString(ServerKey);

        var imported = VpnLinkCodec.TryDecode(link);

        Assert.NotNull(imported);
        Assert.Null(imported!.Name);
        Assert.DoesNotContain("PersistentKeepalive", imported.ConfText);
        WgConfigValidator.Validate(imported.ConfText);
    }

    [Fact]
    public void WireguardLink_WithoutAKey_IsNotAConfig()
    {
        Assert.Null(VpnLinkCodec.TryDecode("wireguard://example.net:51820?publickey=" + Uri.EscapeDataString(ServerKey)));
    }

    [Fact]
    public void Body_InBase64_CarriesEveryConfig()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', [AwgLink(), Foreign, WireguardLink(), string.Empty])));

        var configs = SubscriptionCodec.Parse(body);

        Assert.Equal(2, configs.Count);
        Assert.Equal("AmneziaWG-3.1-bor_android_phone", configs[0].Name);
        Assert.Equal("AmneziaWG-2-bor_laptop", configs[1].Name);
    }

    [Fact]
    public void Body_InPlainText_CarriesEveryConfig()
    {
        var body = string.Join("\r\n", [AwgLink(), Foreign, WireguardLink()]);

        var configs = SubscriptionCodec.Parse(body);

        Assert.Equal(2, configs.Count);
        Assert.Equal(AwgConfig(), configs[0].ConfText);
    }

    [Fact]
    public void Body_WithoutOurProtocols_IsEmpty()
    {
        var configs = SubscriptionCodec.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(Foreign)));

        Assert.Empty(configs);
    }

    [Fact]
    public void WireguardLink_IsBuiltBackTheWayThePanelWritesIt()
    {
        var imported = VpnLinkCodec.TryDecode(WireguardLink());

        Assert.NotNull(imported);
        Assert.Equal(WireguardLink(), VpnLinkCodec.EncodeWireguard(imported!.ConfText, "AmneziaWG 2 -bor_laptop"));
    }

    [Fact]
    public void WireguardLink_OfAConfigWithoutAnEndpoint_IsNotBuilt()
    {
        var config = string.Join('\n', ["[Interface]", $"PrivateKey = {ClientKey}", string.Empty, "[Peer]", $"PublicKey = {ServerKey}"]);

        Assert.Null(VpnLinkCodec.EncodeWireguard(config, "no endpoint"));
    }

    [Fact]
    public void WireguardLink_OfAnAmneziaConfig_IsNotBuilt()
    {
        // Ссылка не несёт ни Jc, ни H1 - клиент получил бы конфигурацию, которая не встанет.
        Assert.Null(VpnLinkCodec.EncodeWireguard(AwgConfig(), "bor_android_phone"));
    }

    [Fact]
    public void Usage_IsRead()
    {
        var usage = SubscriptionCodec.ParseUsage("upload=1024; download=2048; total=10737418240; expire=1790000000");

        Assert.NotNull(usage);
        Assert.Equal(1024, usage!.Upload);
        Assert.Equal(2048, usage.Download);
        Assert.Equal(10737418240, usage.Total);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), usage.Expires);
    }

    [Fact]
    public void Usage_WithoutAnExpiry_HasNone()
    {
        var usage = SubscriptionCodec.ParseUsage("upload=0; download=0; total=0; expire=0");

        Assert.NotNull(usage);
        Assert.Null(usage!.Expires);
    }

    [Fact]
    public void Usage_WithoutTheHeader_IsNull()
    {
        Assert.Null(SubscriptionCodec.ParseUsage(null));
        Assert.Null(SubscriptionCodec.ParseUsage("  "));
    }

    [Fact]
    public void Title_IsReadInBothSpellings()
    {
        var encoded = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Мой профиль"));

        Assert.Equal("Мой профиль", SubscriptionCodec.ParseTitle(encoded));
        Assert.Equal("plain title", SubscriptionCodec.ParseTitle("plain title"));
        Assert.Null(SubscriptionCodec.ParseTitle(null));
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 6 ", 6)]
    [InlineData("0", 0)]
    [InlineData("soon", 0)]
    [InlineData(null, 0)]
    public void UpdateInterval_IsReadInHours(string? header, int expected)
    {
        Assert.Equal(expected, SubscriptionCodec.ParseUpdateInterval(header));
    }

    // Импорт различает три способа панели по началу строки, поэтому кнопки под каждый не нужны.
    [Theory]
    [InlineData("https://example.net:9080/sub/path/id", true)]
    [InlineData("  http://example.net/sub/id  ", true)]
    [InlineData("vpn://c29tZXRoaW5n", false)]
    [InlineData("wireguard://key@host:51820", false)]
    [InlineData("[Interface]\nPrivateKey = x", false)]
    [InlineData("ftp://example.net/sub", false)]
    [InlineData("example.net/sub", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Address_IsToldApartFromAConfigAndFromALink(string? text, bool expected)
    {
        Assert.Equal(expected, SubscriptionCodec.LooksLikeAddress(text));
    }

    [Theory]
    [InlineData("http://example.net:9080/sub/x/", true)]
    [InlineData("  HTTP://example.net/sub  ", true)]
    [InlineData("https://example.net:9080/sub/x/", false)]
    [InlineData("vpn://W0ludGVyZmFjZV0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PlainAddress_IsTheOneWithoutTls(string? text, bool expected)
    {
        Assert.Equal(expected, SubscriptionCodec.IsPlainAddress(text));
    }

    [Fact]
    public void AddressName_IsTheHost()
    {
        Assert.Equal("example.net", SubscriptionCodec.AddressName("https://example.net:9080/sub/path/id"));
        Assert.Equal(string.Empty, SubscriptionCodec.AddressName("not an address"));
    }
}
