using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window keeps the reach of the access from the tunnel the agent holds, and opens the whole tunnel network
/// only when the switch is turned on in it.
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
    public void TheFieldsLeftEmpty_ShowTheFrontTheConfigNames()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", "10.99.1.1:51821", true, string.Empty, 0, 1420, false, front: "wss://front.example:8443/secret");

        Assert.Equal(string.Empty, transport.WebSocketHost);
        Assert.Equal(string.Empty, transport.WebSocketPort);
        Assert.Equal("front.example", transport.WebSocketHostDefault);
        Assert.Equal("8443", transport.WebSocketPortDefault);
    }

    [Fact]
    public void TheFieldsLeftEmpty_ShowTheEndpointWithoutAFront()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", "10.99.1.1:51821", true, string.Empty, 0, 1420, false);

        Assert.Equal("10.99.1.1", transport.WebSocketHostDefault);
        Assert.Equal("51821", transport.WebSocketPortDefault);
    }

    [Fact]
    public async Task TheFieldsLeftEmpty_AreSavedEmpty()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", "10.99.1.1:51821", false, string.Empty, 0, 1420, false, front: "wss://front.example:8443/secret");

        transport.UseWebSocket = true;
        await transport.CommitAsync();

        Assert.Equal(["e2e", "on", "0", string.Empty], agent.Args.Take(4));
    }

    [Fact]
    public async Task AHostOfItsOwn_IsSavedAsTyped()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", "10.99.1.1:51821", true, string.Empty, 0, 1420, false, front: "wss://front.example:8443/secret");

        transport.WebSocketHost = "own.example";
        transport.WebSocketPort = "9443";
        await transport.CommitAsync();

        Assert.Equal(["e2e", "on", "9443", "own.example"], agent.Args.Take(4));
    }

    private static ConfigTransportViewModel Transport(IAgentConnection agent, bool allow, bool network) =>
        new(agent, "e2e", "10.99.1.1:51821", false, string.Empty, 443, 1420, false, allowInbound: allow, inboundNetwork: network);

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
            Inbound = [command.Args[8], command.Args[9]];

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
