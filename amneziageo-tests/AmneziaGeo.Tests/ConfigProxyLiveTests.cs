using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The proxy switch on the open page of a configuration follows the front the next snapshot brings, without the
/// page being opened again.
/// </summary>
public sealed class ConfigProxyLiveTests
{
    [Fact]
    public void AFrontArrivingWhileOpen_OpensTheSwitch()
    {
        var catalogue = Catalogue();
        catalogue.Apply([Entry(string.Empty)]);
        catalogue.OpenConfig = "ours";
        var transport = catalogue.ConfigTransport;
        Assert.NotNull(transport);
        Assert.False(transport.WebSocketOpen);

        catalogue.Apply([Entry("vpn.example:443")]);

        Assert.Same(transport, catalogue.ConfigTransport);
        Assert.True(transport.WebSocketOpen);
    }

    [Fact]
    public void AFrontGoneWhileOpen_ClosesTheSwitch()
    {
        var catalogue = Catalogue();
        catalogue.Apply([Entry("vpn.example:443")]);
        catalogue.OpenConfig = "ours";
        var transport = catalogue.ConfigTransport;
        Assert.NotNull(transport);
        Assert.True(transport.WebSocketOpen);

        catalogue.Apply([Entry(string.Empty)]);

        Assert.Same(transport, catalogue.ConfigTransport);
        Assert.False(transport.WebSocketOpen);
    }

    [Fact]
    public void TheSettingsFront_ComesWithTheSnapshot()
    {
        var catalogue = Catalogue();
        catalogue.Apply([Entry(string.Empty)]);
        catalogue.OpenConfig = "ours";
        var transport = catalogue.ConfigTransport;
        Assert.NotNull(transport);

        catalogue.Apply([Entry("vpn.example:443", manual: true)]);

        Assert.True(transport.WebSocketOpen);
        Assert.True(transport.WebSocketManual);
    }

    private static ConfigViewModel Catalogue()
    {
        var agent = new Silent();
        return new ConfigViewModel(new MainWindowViewModel(agent, new UiPreferences()), agent);
    }

    private static ConfigEntry Entry(string front, bool manual = false) =>
        new("ours", "vpn.example:51820", false, ConnectionStatus.Disconnected, [], WebSocketFront: front, WebSocketManual: manual);

    // Answers every command with nothing.
    private sealed class Silent : IAgentConnection
    {
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

        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
