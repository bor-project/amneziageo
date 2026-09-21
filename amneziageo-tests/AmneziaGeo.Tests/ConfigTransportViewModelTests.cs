using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window keeps the reach of the access from the tunnel the agent holds, opens the whole tunnel network only
/// when the switch is turned on in it, and leaves the websocket and the routing to what the server offers.
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
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, webSocketOffered: true);

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
        var transport = new ConfigTransportViewModel(agent, "e2e", true, 1420, false, webSocketOffered: false);

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

        transport.RoutingOn = false;
        await transport.CommitAsync();

        Assert.True(transport.RoutingOpen);
        Assert.False(transport.UseRouting);
        Assert.Equal("off", agent.Args[8]);
    }

    [Fact]
    public async Task ARoutingBannedByTheServer_ShowsOffAndKeepsTheStoredSwitch()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, useRouting: true, routingLocked: true);

        transport.RoutingOn = false;
        await transport.CommitAsync();

        Assert.False(transport.RoutingOpen);
        Assert.False(transport.RoutingOn);
        Assert.True(transport.UseRouting);
        Assert.Equal("on", agent.Args[8]);
    }

    private static ConfigTransportViewModel Transport(IAgentConnection agent, bool allow, bool network) =>
        new(agent, "e2e", false, 1420, false, allowInbound: allow, inboundNetwork: network);

    // Keeps what the window sends for the access instead of an agent.
    private sealed class Recorder : IAgentConnection
    {
        public IReadOnlyList<string> Inbound { get; private set; } = [];

        public IReadOnlyList<string> Args { get; private set; } = [];

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
            Args = command.Args;
            Inbound = [command.Args[6], command.Args[7]];

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
