using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Панель x-ui 3.7 отдаёт клиента AmneziaWG тремя способами: ссылкой vpn:// с текстом конфигурации внутри,
/// самим текстом и подпиской из таких ссылок. Здесь закреплены первые два - то, что приходит из QR и файла.
/// </summary>
public sealed class XuiInteropTests
{
    private const string ClientKey = "QW1uZXppYUdlbyB4LXVpIGNsaWVudCBrZXkgMDAwMSE=";
    private const string ServerKey = "QW1uZXppYUdlbyB4LXVpIHNlcnZlciBrZXkgMDAwMSE=";
    private const string PresharedKey = "QW1uZXppYUdlbyB4LXVpIHNoYXJlZCBrZXkgMDAwMSE=";
    private const string HeaderKey = "QW1uZXppYUdlbyB4LXVpIGhlYWRlciBrZXkgMDAxISE=";
    private const string Remark = "AmneziaWG 3.1 -bor_android_phone";
    private const string Name = "AmneziaWG-3.1-bor_android_phone";

    // Порядок и набор строк повторяют amneziaWGConfigText из internal/sub/service.go панели.
    private static string XuiConfig(params string[] interfaceExtra)
    {
        var lines = new List<string>
        {
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.0.1.2/32",
            "DNS = 1.1.1.1, 9.9.9.9",
            "MTU = 1420",
            "Jc = 6",
            "Jmin = 52",
            "Jmax = 241",
            "S1 = 63",
            "S2 = 149",
            "S3 = 13",
            "S4 = 25",
            "H1 = 194488238-453280017",
            "H2 = 945380663-959625713",
            "H3 = 1220926369-1460941108",
            "H4 = 2008138652-2111657743",
            "I1 = <r 246>",
            $"HeaderProtectionKey = {HeaderKey}",
            "ContentPaddingAddition = 12-44",
            "RekeyAfterTime = 100-135",
            "RekeyTimeout = 5-6",
            "RejectAfterTime = 186-259",
            "KeepaliveTimeout = 12-16",
            "MaxHandshakeAttempts = 17-33",
            "RandomTrailers = on",
            "DisableCookies = on",
        };

        lines.AddRange(interfaceExtra);
        lines.AddRange(
        [
            string.Empty,
            $"# {Remark}",
            "[Peer]",
            $"PublicKey = {ServerKey}",
            $"PresharedKey = {PresharedKey}",
            "AllowedIPs = 0.0.0.0/0, ::/0",
            "Endpoint = example.net:51821",
            "PersistentKeepalive = 25",
        ]);

        return string.Join('\n', lines);
    }

    // Панель кодирует ссылку base64url без выравнивания (RawURLEncoding).
    private static string XuiLink(string confText)
    {
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(confText));
        return "vpn://" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Заменяет строку магического заголовка, чтобы в конфиге остался один H-ключ.
    private static string WithHeader(string line)
    {
        var key = line[..line.IndexOf(' ')];
        var lines = XuiConfig().Split('\n').Select(l => l.StartsWith(key + " ", StringComparison.Ordinal) ? line : l);
        return string.Join('\n', lines);
    }

    [Fact]
    public void XuiConfig_Validates()
    {
        WgConfigValidator.Validate(XuiConfig());
    }

    [Fact]
    public void XuiConfig_ReachesTheDeviceWhole()
    {
        var uapi = WgQuickToUapi.Convert(XuiConfig());
        var headerHex = Convert.ToHexStringLower(Convert.FromBase64String(HeaderKey));

        Assert.NotNull(uapi);
        Assert.Contains("h1=194488238-453280017\n", uapi);
        Assert.Contains("h4=2008138652-2111657743\n", uapi);
        Assert.Contains("i1=<r 246>\n", uapi);
        Assert.Contains($"header_protection_key={headerHex}\n", uapi);
        Assert.Contains("random_trailers=1\n", uapi);
        Assert.Contains("disable_cookies=1\n", uapi);
        Assert.Contains("endpoint=example.net:51821\n", uapi);
        Assert.Contains("persistent_keepalive_interval=25\n", uapi);
    }

    [Fact]
    public void XuiLink_CarriesThePlainConfig()
    {
        // Amnezia кладёт в vpn:// сжатый JSON, x-ui - сам текст конфигурации.
        var config = XuiConfig();

        var imported = VpnLinkCodec.TryDecode(XuiLink(config));

        Assert.NotNull(imported);
        Assert.Equal(config, imported!.ConfText);
        Assert.Equal(Name, imported.Name);
    }

    [Fact]
    public void XuiLink_FromQr_CarriesThePlainConfig()
    {
        var config = XuiConfig();

        var imported = VpnLinkCodec.TryDecodeQr(XuiLink(config));

        Assert.NotNull(imported);
        Assert.Equal(config, imported!.ConfText);
    }

    [Fact]
    public void XuiLink_Imported_IsAcceptedByTheValidator()
    {
        var imported = VpnLinkCodec.TryDecode(XuiLink(XuiConfig()));

        Assert.NotNull(imported);
        WgConfigValidator.Validate(imported!.ConfText);
        Assert.NotNull(WgQuickToUapi.Convert(imported.ConfText));
    }

    [Fact]
    public void AmneziaLink_StillDecodes()
    {
        var config = XuiConfig();

        var imported = VpnLinkCodec.TryDecode(VpnLinkCodec.Encode(config, "amnezia"));

        Assert.NotNull(imported);
        Assert.Equal(config, imported!.ConfText);
        Assert.Equal("amnezia", imported.Name);
    }

    [Fact]
    public void PlainConfig_TakesItsNameFromTheRemark()
    {
        var imported = VpnLinkCodec.TryDecode(XuiConfig());

        Assert.NotNull(imported);
        Assert.Equal(Name, imported!.Name);
    }

    [Fact]
    public void PlainConfig_WithoutARemark_HasNoName()
    {
        var config = XuiConfig().Replace($"# {Remark}\n", string.Empty);

        var imported = VpnLinkCodec.TryDecode(config);

        Assert.NotNull(imported);
        Assert.Null(imported!.Name);
    }

    [Fact]
    public void CommentInsideThePeer_IsNotTheName()
    {
        var config = XuiConfig().Replace($"# {Remark}\n", string.Empty) + "\n# not a name";

        var imported = VpnLinkCodec.TryDecode(config);

        Assert.NotNull(imported);
        Assert.Null(imported!.Name);
    }

    [Theory]
    [InlineData("H1 = 5")]
    [InlineData("H2 = 5-6")]
    [InlineData("H3 = 1220926369-1460941108")]
    [InlineData("H4 = 4294967295")]
    public void MagicHeaderRange_IsAccepted(string line)
    {
        WgConfigValidator.Validate(WithHeader(line));
    }

    [Theory]
    [InlineData("H1 = 6-5")]
    [InlineData("H2 = soon")]
    [InlineData("H3 = 5-6-7")]
    [InlineData("H4 = -1")]
    public void MagicHeaderThatTheEngineWouldRefuse_IsRejected(string line)
    {
        // Движок читает H1-H4 через UintRange.FromString, опечатка валит весь IpcSet при подъёме.
        Assert.Throws<WgConfigFormatException>(() => WgConfigValidator.Validate(WithHeader(line)));
    }
}
