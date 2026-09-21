using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A config proves its keys to the services of its server and reads what they offer: the token counts the proof the
/// server counts, the sealed answer opens under the same keys and nonce alone, and the offer is kept per config for
/// the services its text asks.
/// </summary>
public sealed class ServerOfferTests : IAsyncLifetime
{
    private const string Answer = """{"server":"amneziageo","version":"0.0.1.0","client":"sticky","features":{"websocket":{"port":8446},"routing":{"allowed":false},"speed":{"inside":{"down":"https://10.9.1.1:8446/api/speed/down?ticket=t","up":"https://10.9.1.1:8446/api/speed/up?ticket=t"},"outside":{"down":"https://vpn.example:8446/api/speed/down?ticket=t","up":"https://vpn.example:8446/api/speed/up?ticket=t"},"limit":104857600,"expires":"2030-01-01T00:00:00+00:00"}}}""";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-offer-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void TheServices_TakeTheLineOfTheServerElseThePortOfTheEndpoint()
    {
        const string plain = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";
        const string moved = "[Interface]\nPrivateKey = a\n# AmneziaGeo Services = 8446\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

        Assert.Equal(new ServiceTarget("vpn.example", 51820, "a", "b"), ConfigServices.Target(plain));
        Assert.Equal(8446, ConfigServices.Port(moved));
        Assert.Equal(8446, ConfigServices.Port("#  amneziageo services =  8446 \r\nEndpoint = vpn.example:51820\r\n"));
        Assert.Equal(51820, ConfigServices.Port("# AmneziaGeo Services = 70000\nEndpoint = vpn.example:51820\n"));
        Assert.Equal("2001:db8::1", ConfigServices.Host("Endpoint = [2001:db8::1]:51820"));
        Assert.Null(ConfigServices.Target("[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n"));
        Assert.Equal("vpn.example:51820", ConfigServices.Target(plain)!.ToString());
    }

    [Fact]
    public void TheMarkOfTheServices_FollowsTheKeysAndThePortAlone()
    {
        var one = new ServiceTarget("vpn.example", 51820, "a", "b");

        Assert.Equal(one.Mark(), new ServiceTarget("vpn.example", 51820, "a", "b").Mark());
        Assert.NotEqual(one.Mark(), new ServiceTarget("vpn.example", 8446, "a", "b").Mark());
        Assert.NotEqual(one.Mark(), new ServiceTarget("vpn.example", 51820, "c", "b").Mark());
    }

    [Fact]
    public void AnAnswerOfAServerOfOurs_NamesItsFeatures()
    {
        var offer = ServerOffer.Read(Encoding.UTF8.GetBytes(Answer))!;

        Assert.True(offer.Ours);
        Assert.Equal("0.0.1.0", offer.Version);
        Assert.Equal("sticky", offer.Client);
        Assert.Equal(8446, offer.WebSocketPort);
        Assert.True(offer.RoutingLocked);
        Assert.Equal("https://10.9.1.1:8446/api/speed/down?ticket=t", offer.Speed(true)!.Down);
        Assert.Equal("https://vpn.example:8446/api/speed/up?ticket=t", offer.Speed(false)!.Up);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), offer.SpeedExpires);
        Assert.Equal(offer.ToPayload(), ServerOffer.Parse(offer.ToPayload()).ToPayload());
        Assert.Null(ServerOffer.Read("""{"server":"someone","version":"1","features":{}}"""u8));
        Assert.Null(ServerOffer.Read("not json"u8));
        Assert.False(ServerOffer.None.Ours);
        Assert.Null(ServerOffer.None.Speed(true));
    }

    [Fact]
    public void TheToken_CarriesTheProofTheServerCounts()
    {
        var (clientPrivate, clientPublic) = Pair();
        var (serverPrivate, serverPublic) = Pair();

        var token = ServiceToken.Make(clientPrivate, serverPublic, 1790000000);
        var shared = Curve25519.Product(Curve25519.Bytes(serverPrivate), Curve25519.Bytes(clientPublic));
        var expected = Convert.ToBase64String(HMACSHA256.HashData(shared, Encoding.UTF8.GetBytes($"amneziageo-hello\n{token.Key}\n{token.Time}\n{token.Nonce}")));

        Assert.Equal(clientPublic, token.Key);
        Assert.Equal(1790000000, token.Time);
        Assert.Equal(16, Convert.FromBase64String(token.Nonce).Length);
        Assert.Equal(expected, token.Proof);
    }

    [Fact]
    public void TheHeader_CarriesTheTokenInBase64Url()
    {
        var (clientPrivate, clientPublic) = Pair();
        var (_, serverPublic) = Pair();

        var header = ServiceToken.Header(clientPrivate, serverPublic, DateTimeOffset.FromUnixTimeSeconds(1790000000));
        var text = header["AmneziaGeo ".Length..].Replace('-', '+').Replace('_', '/');
        using var json = JsonDocument.Parse(Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=')));

        Assert.StartsWith("AmneziaGeo ", header, StringComparison.Ordinal);
        Assert.DoesNotContain('=', header[11..]);
        Assert.Equal(clientPublic, json.RootElement.GetProperty("key").GetString());
        Assert.Equal(1790000000, json.RootElement.GetProperty("time").GetInt64());
        Assert.True(json.RootElement.TryGetProperty("proof", out _));
    }

    [Fact]
    public void ASealedAnswer_OpensUnderItsKeysAndNonceAlone()
    {
        var (clientPrivate, clientPublic) = Pair();
        var (serverPrivate, serverPublic) = Pair();
        var token = ServiceToken.Make(clientPrivate, serverPublic, 1790000000);
        var shared = Curve25519.Product(Curve25519.Bytes(serverPrivate), Curve25519.Bytes(clientPublic));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Convert.FromBase64String(token.Nonce), Encoding.UTF8.GetBytes("amneziageo-reply"));
        var iv = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes(Answer);
        var sealedBytes = new byte[body.Length + 16];
        using (var cipher = new AesGcm(key, 16))
        {
            cipher.Encrypt(iv, body, sealedBytes.AsSpan(0, body.Length), sealedBytes.AsSpan(body.Length));
        }

        var opened = ServiceToken.Open(clientPrivate, serverPublic, token.Nonce, Convert.ToBase64String(iv), Convert.ToBase64String(sealedBytes));
        var other = ServiceToken.Make(clientPrivate, serverPublic, 1790000000);

        Assert.Equal(body, opened);
        Assert.Null(ServiceToken.Open(clientPrivate, serverPublic, other.Nonce, Convert.ToBase64String(iv), Convert.ToBase64String(sealedBytes)));
        Assert.Null(ServiceToken.Open(clientPrivate, serverPublic, token.Nonce, "not base64", Convert.ToBase64String(sealedBytes)));
    }

    [Fact]
    public async Task AnOffer_IsKeptForTheServicesItsTextAsks()
    {
        const string text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";
        const string rekeyed = "[Interface]\nPrivateKey = c\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";
        const string edited = "[Interface]\nPrivateKey = a\nDNS = 1.1.1.1\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";
        var offer = ServerOffer.Parse(Answer);

        await ServerOfferStore.WriteAsync(_store, "office", ConfigServices.Target(text)!, offer, DateTimeOffset.UtcNow);

        Assert.Equal(8446, (await ServerOfferStore.ReadAsync(_store, "office", text)).WebSocketPort);
        Assert.Equal(8446, (await ServerOfferStore.ReadAsync(_store, "office", edited)).WebSocketPort);
        Assert.False((await ServerOfferStore.ReadAsync(_store, "office", rekeyed)).Ours);
        Assert.False((await ServerOfferStore.ReadAsync(_store, "home", text)).Ours);

        await ServerOfferStore.MoveAsync(_store, "office", "work");

        Assert.False((await ServerOfferStore.ReadAsync(_store, "office", text)).Ours);
        Assert.True((await ServerOfferStore.ReadAsync(_store, "work", text)).Ours);

        await ServerOfferStore.ForgetAsync(_store, "work");

        Assert.Null(await ServerOfferStore.KeptAsync(_store, "work", text));
    }

    [Fact]
    public async Task AServerNotOfOurs_IsKeptAsAnsweredWithNothing()
    {
        const string text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

        await ServerOfferStore.WriteAsync(_store, "office", ConfigServices.Target(text)!, ServerOffer.None, DateTimeOffset.UtcNow);
        var kept = await ServerOfferStore.KeptAsync(_store, "office", text);

        Assert.NotNull(kept);
        Assert.False(kept!.Offer.Ours);
    }

    [Fact]
    public async Task TheRoutingOfAStoredConfig_FollowsTheBanItsServerOffers()
    {
        const string text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";
        await _store.SaveConfigAsync("office", text);

        Assert.True(await ConfigRouting.AllowedAsync(_store, "office"));

        await ServerOfferStore.WriteAsync(_store, "office", ConfigServices.Target(text)!, ServerOffer.Parse(Answer), DateTimeOffset.UtcNow);

        Assert.False(await ConfigRouting.AllowedAsync(_store, "office"));
    }

    private static (string Private, string Public) Pair()
    {
        var privateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return (privateKey, Curve25519.PublicOf(privateKey));
    }
}
