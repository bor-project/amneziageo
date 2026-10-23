using System.IO.Compression;
using System.Text.Json;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Ссылка vpn:// приложения несёт рядом с файлом поля, по которым подключается AmneziaVPN 5.
/// </summary>
public sealed class VpnLinkCodecTests
{
    private const string ClientKey = "QW1uZXppYUdlbyBsaW5rIGNsaWVudCBrZXkgMDAwMDEh";
    private const string ServerKey = "QW1uZXppYUdlbyBsaW5rIHNlcnZlciBrZXkgMDAwMDEh";
    private const string Preshared = "QW1uZXppYUdlbyBsaW5rIHByZXNoYXJlZCBrZXkgMDEh";

    private static string Conf(string dns = "DNS = 1.1.1.1, 1.0.0.1", params string[] obfuscation) =>
        string.Join('\n',
        [
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.8.0.2/32, fd00::2/128",
            dns,
            "MTU = 1420",
            .. obfuscation,
            string.Empty,
            "[Peer]",
            $"PublicKey = {ServerKey}",
            $"PresharedKey = {Preshared}",
            "AllowedIPs = 0.0.0.0/0, ::/0",
            "Endpoint = vpn.example.net:51820",
            "PersistentKeepalive = 25",
        ]);

    [Fact]
    public void TheFieldsAmneziaVpnConnectsByComeBesideTheFile()
    {
        var conf = Conf(obfuscation: ["Jc = 5", "Jmin = 10", "Jmax = 50", "S1 = 109", "S2 = 61", "H1 = 1", "H2 = 194488238-194553774", "H3 = 3", "H4 = 4", "ContentPaddingAddition = 12-44"]);

        var (document, last) = Read(VpnLinkCodec.Encode(conf, "office"));

        Assert.Equal("amnezia-awg", document.GetProperty("defaultContainer").GetString());
        Assert.Equal("amnezia-awg", document.GetProperty("containers")[0].GetProperty("container").GetString());
        Assert.Equal("office", document.GetProperty("description").GetString());
        Assert.Equal(conf, last.GetProperty("config").GetString());
        Assert.Equal("vpn.example.net", last.GetProperty("hostName").GetString());
        Assert.Equal(51820, last.GetProperty("port").GetInt32());
        Assert.Equal("10.8.0.2/32, fd00::2/128", last.GetProperty("client_ip").GetString());
        Assert.Equal(ClientKey, last.GetProperty("client_priv_key").GetString());
        Assert.Equal(ServerKey, last.GetProperty("server_pub_key").GetString());
        Assert.Equal(Preshared, last.GetProperty("psk_key").GetString());
        Assert.Equal(["0.0.0.0/0", "::/0"], last.GetProperty("allowed_ips").EnumerateArray().Select(range => range.GetString()!).ToArray());
        Assert.Equal("1420", last.GetProperty("mtu").GetString());
        Assert.Equal("25", last.GetProperty("persistent_keep_alive").GetString());
        Assert.Equal("5", last.GetProperty("Jc").GetString());
        Assert.Equal("109", last.GetProperty("S1").GetString());
        Assert.Equal("194488238-194553774", last.GetProperty("H2").GetString());
        Assert.Equal("12-44", last.GetProperty("ContentPaddingAddition").GetString());
    }

    [Fact]
    public void AnObfuscatedFileWithoutTheDemandedSettingsTakesTheirDefaults()
    {
        var (_, last) = Read(VpnLinkCodec.Encode(Conf(obfuscation: ["H1 = 5", "H2 = 6", "H3 = 7", "H4 = 8"]), "office"));

        Assert.Equal(["0", "0", "0", "0", "0"], new[] { "Jc", "Jmin", "Jmax", "S1", "S2" }.Select(key => last.GetProperty(key).GetString()!).ToArray());
        Assert.Equal(["5", "6", "7", "8"], new[] { "H1", "H2", "H3", "H4" }.Select(key => last.GetProperty(key).GetString()!).ToArray());
    }

    [Fact]
    public void AFileWithoutObfuscationTravelsInTheWireGuardContainer()
    {
        var (document, last) = Read(VpnLinkCodec.Encode(Conf(), "office"));
        var container = document.GetProperty("containers")[0];

        Assert.Equal("amnezia-wireguard", document.GetProperty("defaultContainer").GetString());
        Assert.Equal("amnezia-wireguard", container.GetProperty("container").GetString());
        Assert.True(container.TryGetProperty("wireguard", out _));
        Assert.False(container.TryGetProperty("awg", out _));
        Assert.False(last.TryGetProperty("Jc", out _));
        Assert.Equal(ClientKey, last.GetProperty("client_priv_key").GetString());
    }

    [Theory]
    [InlineData("DNS = 1.1.1.1", "1.1.1.1", "1.1.1.1")]
    [InlineData("DNS = 1.1.1.1, 1.0.0.1", "1.1.1.1", "1.0.0.1")]
    [InlineData("DNS = 2606:4700:4700::1111, 9.9.9.9", "9.9.9.9", "9.9.9.9")]
    [InlineData("DNS = 2606:4700:4700::1111", null, null)]
    [InlineData("", null, null)]
    public void TheLinkNamesTheIpv4ServersOfTheDnsLine(string dns, string? first, string? second)
    {
        var (document, _) = Read(VpnLinkCodec.Encode(Conf(dns), "office"));

        Assert.Equal(first, document.TryGetProperty("dns1", out var one) ? one.GetString() : null);
        Assert.Equal(second, document.TryGetProperty("dns2", out var two) ? two.GetString() : null);
    }

    [Fact]
    public void TheLinkReadsBackAsTheFile()
    {
        var conf = Conf(obfuscation: ["Jc = 5"]);

        var imported = VpnLinkCodec.TryDecode(VpnLinkCodec.Encode(conf, "office"));

        Assert.Equal(conf, imported?.ConfText);
    }

    // The document of a link and the fields of its last_config.
    private static (JsonElement Document, JsonElement Last) Read(string link)
    {
        Assert.StartsWith("vpn://", link, StringComparison.Ordinal);
        var text = link["vpn://".Length..].Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - (text.Length % 4)) % 4);
        var data = Convert.FromBase64String(text);
        using var input = new MemoryStream(data, 4, data.Length - 4);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(zlib);
        var root = document.RootElement.Clone();
        var container = root.GetProperty("containers")[0];
        var proto = container.TryGetProperty("awg", out var awg) ? awg : container.GetProperty("wireguard");
        using var last = JsonDocument.Parse(proto.GetProperty("last_config").GetString()!);

        return (root, last.RootElement.Clone());
    }
}
