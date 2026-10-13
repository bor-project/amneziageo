using System.Security.Cryptography;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A configuration lets its server take the tunnel down by a signal or not: the switch is kept with the transport
/// and stands on by default, is shown where the server leaves the choice to the device, travels to the server with
/// the hello, and a configuration that turned it off listens for no signal.
/// </summary>
public sealed class SignalSwitchTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-signal-{Guid.NewGuid():N}.db");
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
    public void TheOffer_SaysWhetherTheChoiceIsLeftToTheDevice()
    {
        var portless = ServerOffer.Parse("{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"disconnect\":{\"allowed\":true}}}");

        Assert.True(Offer(",\"allowed\":true").SignalAllowed);
        Assert.False(Offer(string.Empty).SignalAllowed);
        Assert.False(Offer(",\"allowed\":false").SignalAllowed);
        Assert.False(Offer(",\"allowed\":\"true\"").SignalAllowed);
        Assert.False(portless.SignalAllowed);
        Assert.False(ServerOffer.None.SignalAllowed);
    }

    [Fact]
    public void AConfigurationThatTurnedTheSwitchOff_ListensForNoSignal()
    {
        var keys = Keys();
        var config = Config(keys, "10.8.0.2/32");

        Assert.NotNull(TunnelSignal.Of(Offer(",\"allowed\":true"), config));
        Assert.NotNull(TunnelSignal.Of(Offer(string.Empty), config));
        Assert.NotNull(TunnelSignal.Of(Offer(",\"allowed\":true"), config, true, true));
        Assert.Null(TunnelSignal.Of(Offer(",\"allowed\":true"), config, true, false));
        Assert.Null(TunnelSignal.Of(Offer(string.Empty), config, true, false));
    }

    [Fact]
    public async Task TheSwitch_IsKeptWithTheTransportAndStandsOnByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("plain", false));
        await _store.SetConfigTransportAsync(new ConfigTransport("deaf", false, AllowSignal: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("back", false, AllowSignal: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("back", false));

        Assert.True((await _store.GetConfigTransportAsync("plain"))?.AllowSignal);
        Assert.False((await _store.GetConfigTransportAsync("deaf"))?.AllowSignal);
        Assert.True((await _store.GetConfigTransportAsync("back"))?.AllowSignal);
    }

    [Fact]
    public void TheHello_SaysWhetherTheDeviceTakesTheSignal()
    {
        var keys = Keys();
        var point = new ServiceTarget("vpn.example", 51820, keys.ClientPrivate, keys.ServerPublic);
        var token = ServiceToken.Make(keys.ClientPrivate, keys.ServerPublic, 1_760_000_000);

        var taking = Said(ServiceToken.Hello(token, point), token, keys);
        var refusing = Said(ServiceToken.Hello(token, point with { TakesSignal = false }), token, keys);

        Assert.True(taking);
        Assert.False(refusing);
    }

    [Fact]
    public void TheHello_CarriesTheTokenAsItStood()
    {
        var keys = Keys();
        var point = new ServiceTarget("vpn.example", 51820, keys.ClientPrivate, keys.ServerPublic);
        var token = ServiceToken.Make(keys.ClientPrivate, keys.ServerPublic, 1_760_000_000);

        using var hello = JsonDocument.Parse(ServiceToken.Hello(token, point));
        var root = hello.RootElement;

        Assert.Equal(token.Key, root.GetProperty("key").GetString());
        Assert.Equal(token.Time, root.GetProperty("time").GetInt64());
        Assert.Equal(token.Nonce, root.GetProperty("nonce").GetString());
        Assert.Equal(token.Proof, root.GetProperty("proof").GetString());
    }

    [Fact]
    public async Task TheServer_IsToldWhatTheConfigurationSays()
    {
        var said = new List<bool>();
        var offers = new ServerOffers(_store, ask: (point, _) =>
        {
            said.Add(point.TakesSignal);

            return Task.FromResult(new HelloReply(null, true));
        });

        await offers.AskAsync("office", Text, CancellationToken.None);
        await _store.SetConfigTransportAsync(new ConfigTransport("office", false, AllowSignal: false));
        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal([true, false], said);
    }

    [Fact]
    public void ABundle_CarriesTheSwitchAndAnEarlierOneLeavesItOn()
    {
        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            [new PortableBundle.ConfigBlock("office", "[Interface]\n", new PortableBundle.TransportBlock(true, string.Empty, 0, 1380, AllowSignal: false), null)],
            []);
        const string earlier = """
            {
              "Format": "amneziageo-bundle",
              "Version": 2,
              "Configs": [
                {
                  "Name": "office",
                  "ConfigText": "[Interface]\n",
                  "Transport": { "UseWebSocket": true, "Host": "", "Port": 0, "Mtu": 1380 }
                }
              ],
              "RoutingLists": []
            }
            """;

        Assert.False(PortableBundle.Deserialize(PortableBundle.Serialize(bundle))?.Configs[0].Transport?.AllowSignal);
        Assert.True(PortableBundle.Deserialize(earlier)?.Configs[0].Transport?.AllowSignal);
    }

    [Fact]
    public async Task TheSwitchOfTheWindow_GoesInACommandOfItsOwn()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, signalOffered: true);

        transport.AllowSignal = false;
        var dirty = transport.IsDirty;
        await transport.CommitAsync();

        Assert.True(transport.SignalOffered);
        Assert.True(dirty);
        Assert.Equal([IpcContract.OpSetWebSocket, IpcContract.OpSetSignal], agent.Ops);
        Assert.Equal(["e2e", "off"], agent.Last);
    }

    [Fact]
    public async Task ASaveOfAnotherSetting_SendsNoCommandOfTheSwitch()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, allowSignal: false);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.False(transport.SignalOffered);
        Assert.False(transport.AllowSignal);
        Assert.Equal([IpcContract.OpSetWebSocket], agent.Ops);
    }

    [Fact]
    public void TheSwitchPutBack_LeavesNothingToSave()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", false, 1420, false, signalOffered: true);

        transport.AllowSignal = false;
        transport.Revert();

        Assert.True(transport.AllowSignal);
        Assert.False(transport.IsDirty);
    }

    // Opens what a hello says about the device the way its server does; null when it says nothing.
    private static bool? Said(string hello, ServiceProof token, (string ClientPrivate, string ClientPublic, string ServerPrivate, string ServerPublic) keys)
    {
        using var json = JsonDocument.Parse(hello);
        if (!json.RootElement.TryGetProperty("says", out var says))
        {
            return null;
        }

        var body = ServiceToken.Open(
            keys.ServerPrivate,
            keys.ClientPublic,
            token.Nonce,
            says.GetProperty("iv").GetString()!,
            says.GetProperty("data").GetString()!,
            ServiceToken.SaysContext);
        if (body is null)
        {
            return null;
        }

        using var said = JsonDocument.Parse(body);

        return said.RootElement.GetProperty("disconnect").GetBoolean();
    }

    private static ServerOffer Offer(string more) =>
        ServerOffer.Parse("{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"disconnect\":{\"port\":28561,\"from\":[\"10.8.0.1\"]" + more + "}}}");

    private static string Config((string ClientPrivate, string ClientPublic, string ServerPrivate, string ServerPublic) keys, string address) =>
        $"[Interface]\nPrivateKey = {keys.ClientPrivate}\nAddress = {address}\n\n[Peer]\nPublicKey = {keys.ServerPublic}\nEndpoint = vpn.example:51820\nAllowedIPs = 0.0.0.0/0\n";

    private static (string ClientPrivate, string ClientPublic, string ServerPrivate, string ServerPublic) Keys()
    {
        var client = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return (client, Curve25519.PublicOf(client), server, Curve25519.PublicOf(server));
    }

    // Keeps the commands the window sends instead of an agent.
    private sealed class Recorder : IAgentConnection
    {
        public List<string> Ops { get; } = [];

        public IReadOnlyList<string> Last { get; private set; } = [];

        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Ops.Add(command.Op);
            Last = command.Args;

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
