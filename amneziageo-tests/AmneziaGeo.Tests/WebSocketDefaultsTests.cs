using System.Text.Json;

using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The websocket front a server of ours offers becomes the websocket settings of its configuration while those stand
/// at their defaults or already name the front, and the settings of the user's own stay as they are.
/// </summary>
public sealed class WebSocketDefaultsTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = key\nAddress = 10.9.1.12/32\n\n[Peer]\nPublicKey = peer\nEndpoint = vpn.example:51820\n";

    private static readonly WebSocketArgs Front = new(string.Empty, 443, "s3cret", 51820);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-front-{Guid.NewGuid():N}.db");
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
    public void ATransportNeverSet_TakesTheFrontUnderTheHostOfTheEndpoint()
    {
        var taken = WebSocketDefaults.Follow("home", null, Text, Front);

        Assert.NotNull(taken);
        Assert.Equal("home", taken.Name);
        Assert.Equal("wss://vpn.example:443/s3cret", taken.WebSocketHost);
        Assert.Equal(443, taken.WebSocketPort);
        Assert.False(taken.UseWebSocket);
    }

    [Fact]
    public void ATransportAtItsDefaults_KeepsItsSwitchAndItsOtherSettings()
    {
        var stored = new ConfigTransport("home", true, string.Empty, 443, 1380, UseIpv6: true, MtuMode: MtuMode.Custom, AllowInbound: true, ApiPort: 9443);

        var taken = WebSocketDefaults.Follow("home", stored, Text, Front with { Host = "front.example", Port = 8443 });

        Assert.Equal(stored with { WebSocketHost = "wss://front.example:8443/s3cret", WebSocketPort = 8443 }, taken);
    }

    [Fact]
    public void TheFrontOfTheServer_TakesTheNewPathItIsOffered()
    {
        var stored = new ConfigTransport("home", true, "wss://vpn.example:443/old", 443);

        Assert.Equal("wss://vpn.example:443/s3cret", WebSocketDefaults.Follow("home", stored, Text, Front)?.WebSocketHost);
        Assert.Null(WebSocketDefaults.Follow("home", stored with { WebSocketHost = "wss://vpn.example:443/s3cret" }, Text, Front));
    }

    [Theory]
    [InlineData("wss://user:pass@vpn.example:443")]
    [InlineData("wss://other.example:443/token")]
    [InlineData("wss://vpn.example:8443/token")]
    [InlineData("front.example")]
    public void AFrontOfTheUsersOwn_IsLeftAlone(string address)
    {
        Assert.Null(WebSocketDefaults.Follow("home", new ConfigTransport("home", true, address, 443), Text, Front));
    }

    [Fact]
    public void AFrontThatHandsTheTunnelToAnotherPort_IsNotTaken()
    {
        Assert.Null(WebSocketDefaults.Follow("home", null, Text, Front with { Target = 51821 }));
    }

    [Fact]
    public void AnAddressOfTheSixthVersion_IsBracketed()
    {
        Assert.Equal(
            "wss://[2001:db8::1]:443/s3cret",
            WebSocketDefaults.Follow("home", null, Text, Front with { Host = "2001:db8::1" })?.WebSocketHost);
    }

    [Fact]
    public async Task AnOfferHeardInsideTheTunnel_IsWrittenIntoTheStoreOnce()
    {
        await _store.SaveConfigAsync("home", Text);
        await _store.SaveConfigAsync("away", Text);

        var taken = await WebSocketDefaults.FollowAsync(_store, Offer("home", true), CancellationToken.None);
        var again = await WebSocketDefaults.FollowAsync(_store, Offer("home", true), CancellationToken.None);
        var outside = await WebSocketDefaults.FollowAsync(_store, Offer("away", false), CancellationToken.None);

        Assert.Equal("wss://vpn.example:443/s3cret", taken?.WebSocketHost);
        Assert.Equal("wss://vpn.example:443/s3cret", (await _store.GetConfigTransportAsync("home"))?.WebSocketHost);
        Assert.Null(again);
        Assert.Null(outside);
        Assert.Null(await _store.GetConfigTransportAsync("away"));
    }

    [Fact]
    public async Task AFrontWrittenBefore_FollowsTheServerUnderAnotherName()
    {
        await _store.SaveConfigAsync("home", Text);

        var named = await WebSocketDefaults.FollowAsync(_store, Offer("home", true, "front.example"), CancellationToken.None);
        var renamed = await WebSocketDefaults.FollowAsync(_store, Offer("home", true), CancellationToken.None);

        Assert.Equal("wss://front.example:443/s3cret", named?.WebSocketHost);
        Assert.Equal("wss://vpn.example:443/s3cret", renamed?.WebSocketHost);
        Assert.Equal("wss://vpn.example:443/s3cret", (await _store.GetConfigTransportAsync("home"))?.WebSocketHost);
    }

    [Fact]
    public async Task AFrontAlreadyNamed_IsRememberedAsWrittenBefore()
    {
        await _store.SaveConfigAsync("home", Text);
        await _store.SetConfigTransportAsync(new ConfigTransport("home", false, "wss://front.example:443/s3cret", 443));

        var kept = await WebSocketDefaults.FollowAsync(_store, Offer("home", true, "front.example"), CancellationToken.None);
        var renamed = await WebSocketDefaults.FollowAsync(_store, Offer("home", true), CancellationToken.None);

        Assert.Null(kept);
        Assert.Equal("wss://vpn.example:443/s3cret", renamed?.WebSocketHost);
    }

    [Fact]
    public async Task AFrontOfTheUsersOwn_IsNotRememberedAndStaysUnderAnotherName()
    {
        await _store.SaveConfigAsync("home", Text);
        await _store.SetConfigTransportAsync(new ConfigTransport("home", false, "wss://cdn.example:443/s3cret", 443));

        var taken = await WebSocketDefaults.FollowAsync(_store, Offer("home", true, "front.example"), CancellationToken.None);

        Assert.Null(taken);
        Assert.Equal("wss://cdn.example:443/s3cret", (await _store.GetConfigTransportAsync("home"))?.WebSocketHost);
        Assert.Null(await _store.GetSettingAsync(WebSocketDefaults.WrittenKey));
    }

    [Fact]
    public void TheMarkOfAFront_IsAShortDigestThatIgnoresTheSpaceAround()
    {
        var mark = WebSocketDefaults.Mark("wss://vpn.example:443/s3cret");

        Assert.Equal(32, mark.Length);
        Assert.Equal(mark, WebSocketDefaults.Mark(" wss://vpn.example:443/s3cret "));
        Assert.NotEqual(mark, WebSocketDefaults.Mark("wss://vpn.example:443/s3creT"));
    }

    // An offer of the websocket front as a server of ours hands it.
    private static ServerOffer Offer(string config, bool inside, string host = "")
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { host, port = 443, path = "s3cret", target = 51820 }));

        return new ServerOffer(
            config,
            "http://10.9.1.1:51820",
            inside,
            "1.0.3.0",
            "milena",
            new Dictionary<string, JsonElement> { [WebSocketArgs.Name] = json.RootElement.Clone() });
    }
}
