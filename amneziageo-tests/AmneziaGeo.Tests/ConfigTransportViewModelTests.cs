using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window keeps the reach of the access from the tunnel the agent holds, opens the whole tunnel network only
/// when the switch is turned on in it, leaves the routing to what the server offers, and the websocket to the front the
/// server offers, else the one the config names, else the fields of the settings.
/// </summary>
public sealed class ConfigTransportViewModelTests
{
    [Fact]
    public async Task ASaveOfAnotherSetting_KeepsTheServerAlone()
    {
        var agent = new Recorder();
        var transport = Transport(agent, allow: true, network: false);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal(["on", "off"], agent.Inbound);
    }

    [Fact]
    public async Task ASaveOfAnotherSetting_KeepsTheWholeNetwork()
    {
        var agent = new Recorder();
        var transport = Transport(agent, allow: true, network: true);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal(["on", "on"], agent.Inbound);
    }

    [Fact]
    public async Task TheSwitchTurnedOnInTheWindow_OpensTheWholeNetwork()
    {
        var agent = new Recorder();
        var transport = Transport(agent, allow: false, network: false);

        transport.AllowInbound = true;
        await transport.CommitAsync();

        Assert.Equal(["on", "on"], agent.Inbound);
    }

    [Fact]
    public async Task TheSwitchTurnedOff_ClosesTheAccess()
    {
        var agent = new Recorder();
        var transport = Transport(agent, allow: true, network: false);

        transport.AllowInbound = false;
        await transport.CommitAsync();

        Assert.Equal(["off", "off"], agent.Inbound);
    }

    [Fact]
    public async Task TheWebSocket_IsTurnedOnWhereTheServerOffersAFront()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, webSocketOpen: true);

        transport.WebSocketOn = true;
        await transport.CommitAsync();

        Assert.True(transport.WebSocketOpen);
        Assert.True(transport.UseWebSocket);
        Assert.Equal(["e2e", "on", string.Empty, "off"], agent.Args.Take(4));
    }

    [Fact]
    public async Task AWebSocketTheServerDoesNotOffer_ShowsOffAndKeepsTheStoredSwitch()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", true, 1420, false, webSocketOpen: false);

        transport.WebSocketOn = false;
        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.False(transport.WebSocketOpen);
        Assert.False(transport.WebSocketOn);
        Assert.True(transport.UseWebSocket);
        Assert.Equal("on", agent.Args[1]);
    }

    [Fact]
    public async Task TheRoutingSwitch_IsSavedWithTheTransport()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false);

        transport.UseRouting = false;
        await transport.CommitAsync();

        Assert.False(transport.UseRouting);
        Assert.Equal("off", agent.Args[8]);
    }


    [Fact]
    public void TheFieldsOfTheSettings_ShowTheEndpointWhenNeitherTheServerNorTheConfigNamesAFront()
    {
        var transport = Manual(new Recorder(), string.Empty, 0);

        Assert.True(transport.ShowWebSocketFields);
        Assert.Equal(string.Empty, transport.WebSocketHost);
        Assert.Equal(string.Empty, transport.WebSocketPort);
        Assert.Equal("10.99.1.1", transport.WebSocketHostDefault);
        Assert.Equal("51821", transport.WebSocketPortDefault);
    }

    [Fact]
    public void AFrontTheServerOrTheConfigNames_HidesTheFields()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", true, 1420, false, webSocketOpen: true, endpoint: "10.99.1.1:51821");

        Assert.True(transport.WebSocketOn);
        Assert.False(transport.ShowWebSocketFields);
    }

    [Fact]
    public async Task AHostOfItsOwn_IsSavedAsTyped()
    {
        var agent = new Recorder();
        var transport = Manual(agent, string.Empty, 0);

        transport.WebSocketHost = "own.example";
        transport.WebSocketPort = "9443";
        transport.CancelProbe();
        await transport.CommitAsync();

        Assert.Equal(["9443", "own.example"], agent.Args.Skip(9));
    }

    [Fact]
    public async Task TheFieldsLeftEmpty_AreSavedEmpty()
    {
        var agent = new Recorder();
        var transport = Manual(agent, string.Empty, 0);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal(["0", string.Empty], agent.Args.Skip(9));
    }

    [Fact]
    public async Task AStoredAccount_IsReadIntoTheFieldsAndSavedBackAsItWas()
    {
        var agent = new Recorder();
        var transport = Manual(agent, "wss://us%20er:p%40ss@own.example:9443", 9443);

        Assert.Equal("own.example", transport.WebSocketHost);
        Assert.Equal("9443", transport.WebSocketPort);
        Assert.True(transport.IsBasicAuth);
        Assert.Equal("us er", transport.WebSocketUser);
        Assert.Equal("p@ss", transport.WebSocketPassword);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal(["9443", "wss://us%20er:p%40ss@own.example:9443"], agent.Args.Skip(9));
    }

    [Fact]
    public async Task AFrontNotOfTheSettings_SendsNoAddress()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", true, 1420, false, webSocketOpen: true, endpoint: "10.99.1.1:51821", webSocketHost: "own.example", webSocketPort: 9443);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal(9, agent.Args.Count);
    }

    private static ConfigTransportViewModel Manual(IAgentConnection agent, string host, int port) =>
        new(agent, "e2e", true, 1420, false, webSocketOpen: true, webSocketManual: true, endpoint: "10.99.1.1:51821", webSocketHost: host, webSocketPort: port);

    [Fact]
    public void TheDnsField_ShowsWhatEachChoiceSettlesOn()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", false, 1420, false, vpnDnsMode: VpnDnsMode.Own, vpnDns: "9.9.9.9",
            vpnDnsShown: "9.9.9.9", serverDns: "10.8.1.1", defaultDns: "10.0.1.1");

        Assert.Equal("9.9.9.9", transport.VpnDnsText);
        Assert.False(transport.IsVpnDnsReadOnly);
        Assert.Equal("1.1.1.1, 1.0.0.1", transport.VpnDnsWatermark);

        transport.VpnDnsMode = (int)VpnDnsMode.Default;
        Assert.Equal("10.0.1.1", transport.VpnDnsText);
        Assert.Null(transport.VpnDnsWatermark);

        transport.VpnDnsMode = (int)VpnDnsMode.Server;
        Assert.Equal("10.8.1.1", transport.VpnDnsText);

        transport.VpnDnsMode = (int)VpnDnsMode.Network;
        Assert.Equal(Loc.Instance.Get("Main_VpnDnsNetworkField"), transport.VpnDnsText);

        transport.VpnDnsText = "8.8.8.8";
        Assert.Equal("9.9.9.9", transport.VpnDns);
    }

    [Fact]
    public void TheDnsField_ShowsTheNetworkWhereTheServerLeavesTheNamesToIt()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", false, 1420, false, vpnDnsNetwork: true, defaultDnsNetwork: true);

        Assert.Equal(Loc.Instance.Get("Main_VpnDnsNetworkField"), transport.VpnDnsText);
        Assert.False(transport.ServerDnsAvailable);
        Assert.Equal(Loc.Instance.Get("Main_VpnDnsServerOff"), transport.ServerDnsTip);

        transport.Settled("10.0.1.1", false, "10.8.1.1", "10.0.1.1");
        Assert.Equal("10.0.1.1", transport.VpnDnsText);
        Assert.True(transport.ServerDnsAvailable);
    }

    [Fact]
    public async Task AnotherChoiceOfTheDns_GoesInACommandOfItsOwn()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, vpnDnsMode: VpnDnsMode.Own, vpnDns: "9.9.9.9");

        transport.VpnDnsMode = (int)VpnDnsMode.Network;
        Assert.True(await transport.CommitAsync());

        Assert.Equal([IpcContract.OpSetWebSocket, IpcContract.OpSetConfigVpnDns], agent.Ops);
        Assert.Equal(["e2e", "network", string.Empty], agent.Args);
    }

    [Fact]
    public async Task ServersOfItsOwnThatAreNotAddresses_StayInTheWindow()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, vpnDnsMode: VpnDnsMode.Own, vpnDns: "9.9.9.9");

        transport.VpnDnsText = "9.9.9.9, dns.example";

        Assert.False(await transport.CommitAsync());
        Assert.Empty(agent.Ops);
        Assert.Contains("dns.example", transport.StatusMessage, StringComparison.Ordinal);
    }

    private static ConfigTransportViewModel Transport(IAgentConnection agent, bool allow, bool network) =>
        new(agent, "e2e", false, 1420, false, allowInbound: allow, inboundNetwork: network);

    // Keeps what the window sends for the access instead of an agent.
    private sealed class Recorder : IAgentConnection
    {
        public IReadOnlyList<string> Inbound { get; private set; } = [];

        public IReadOnlyList<string> Args { get; private set; } = [];

        public List<string> Ops { get; } = [];

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
            Args = command.Args;
            if (command.Op == IpcContract.OpSetConfigVpnDns)
            {
                return Task.FromResult(new IpcAck(true, string.Empty));
            }

            Inbound = [command.Args[6], command.Args[7]];

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
