using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Добавляемый текст приходит из файла, буфера обмена и QR; тип выбирается по содержимому.
/// </summary>
public sealed class ImportCodecTests
{
    private const string ClientKey = "QW1uZXppYUdlbyBpbXBvcnQgY2xpZW50IGtleSAwMDEh";
    private const string ServerKey = "QW1uZXppYUdlbyBpbXBvcnQgc2VydmVyIGtleSAwMDEh";

    private static string Conf()
    {
        return string.Join('\n',
        [
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.0.1.2/32",
            "DNS = 1.1.1.1",
            string.Empty,
            "[Peer]",
            $"PublicKey = {ServerKey}",
            "AllowedIPs = 0.0.0.0/0",
            "Endpoint = example.net:51820",
        ]);
    }

    [Fact]
    public void HttpsAddressIsSubscription()
    {
        var recognized = ImportCodec.Recognize("  https://example.net:2096/sub/abcdef  ");

        Assert.Equal(ImportKind.Subscription, recognized.Kind);
        Assert.Equal("https://example.net:2096/sub/abcdef", recognized.Address);
        Assert.Null(recognized.Config);
    }

    [Fact]
    public void PlainAddressIsSubscription()
    {
        var recognized = ImportCodec.Recognize("http://example.net/sub/abcdef");

        Assert.Equal(ImportKind.Subscription, recognized.Kind);
        Assert.True(SubscriptionCodec.IsPlainAddress(recognized.Address));
    }

    [Fact]
    public void ConfTextIsConfig()
    {
        var recognized = ImportCodec.Recognize(Conf());

        Assert.Equal(ImportKind.Config, recognized.Kind);
        Assert.Contains("[Peer]", recognized.Config!.ConfText);
        Assert.Equal(string.Empty, recognized.Address);
    }

    [Fact]
    public void VpnLinkIsConfig()
    {
        var recognized = ImportCodec.Recognize(VpnLinkCodec.Encode(Conf(), "office"));

        Assert.Equal(ImportKind.Config, recognized.Kind);
        Assert.Equal("office", recognized.Config!.Name);
    }

    [Fact]
    public void ScannedVpnLinkIsConfig()
    {
        var recognized = ImportCodec.RecognizeQr(VpnLinkCodec.Encode(Conf(), "office"));

        Assert.Equal(ImportKind.Config, recognized.Kind);
        Assert.Contains("[Interface]", recognized.Config!.ConfText);
    }

    [Fact]
    public void ScannedAddressIsSubscription()
    {
        var recognized = ImportCodec.RecognizeQr("https://example.net/sub/abcdef");

        Assert.Equal(ImportKind.Subscription, recognized.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("just a note")]
    [InlineData("ftp://example.net/sub")]
    [InlineData("example.net/sub/abcdef")]
    public void JunkIsUnknown(string? text)
    {
        var recognized = ImportCodec.Recognize(text);

        Assert.Equal(ImportKind.Unknown, recognized.Kind);
        Assert.Null(recognized.Config);
    }

    // Тело подписки - несколько ссылок строками; адресом оно не является.
    [Fact]
    public void MultilineBodyIsNotSubscriptionAddress()
    {
        var recognized = ImportCodec.Recognize("https://example.net/sub/a\nhttps://example.net/sub/b");

        Assert.NotEqual(ImportKind.Subscription, recognized.Kind);
    }
}
