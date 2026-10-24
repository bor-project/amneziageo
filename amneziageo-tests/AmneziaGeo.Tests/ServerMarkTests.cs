using System.Text.Json;
using System.Text.Json.Nodes;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The sign of AmneziaGeo on the page of a configuration says what the server of the configuration is: the agent tells
/// whether a server of ours answered, the page names it under the proxy, the DNS and the signal, and the switch of the
/// signal stands off and takes no click while the server does not leave it to the device.
/// </summary>
public sealed class ServerMarkTests
{
    [Fact]
    public void AnotherServer_IsNamedUnderEverySign()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "other", false, 1420, false, allowSignal: true);

        Assert.False(transport.ServerOurs);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkProxyOther"), transport.ProxyMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkDnsOther"), transport.DnsMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkSignalOther"), transport.SignalMarkState);
        Assert.False(transport.SignalSwitch);
        Assert.True(transport.AllowSignal);
    }

    [Fact]
    public void AServerOfOurs_SaysWhatItOffers()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "ours", false, 1420, false, signalOffered: true)
        {
            ServerOurs = true,
            WebSocketOpen = true,
        };
        transport.Settled("10.0.0.1", false, "10.0.0.1", "10.0.0.1", false);

        Assert.Equal(Loc.Instance.Get("Main_ServerMarkProxyOn"), transport.ProxyMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkDnsOn"), transport.DnsMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkSignalOn"), transport.SignalMarkState);
        Assert.True(transport.SignalSwitch);
    }

    [Fact]
    public void AServerOfOurs_SaysWhatItHoldsBack()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "ours", false, 1420, false) { ServerOurs = true };

        Assert.Equal(Loc.Instance.Get("Main_ServerMarkProxyOff"), transport.ProxyMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkDnsOff"), transport.DnsMarkState);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkSignalOff"), transport.SignalMarkState);
        Assert.False(transport.SignalSwitch);
    }

    [Fact]
    public void TheSignalSwitch_SetsTheChoiceOfTheConfig()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "ours", false, 1420, false, signalOffered: true);

        transport.SignalSwitch = false;

        Assert.False(transport.AllowSignal);
        Assert.False(transport.SignalSwitch);
        Assert.True(transport.IsDirty);
    }

    [Fact]
    public void TheServerAnswering_RenamesTheSigns()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "ours", false, 1420, false);
        var changed = new List<string?>();
        transport.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        transport.ServerOurs = true;

        Assert.Contains(nameof(ConfigTransportViewModel.ProxyMarkState), changed);
        Assert.Contains(nameof(ConfigTransportViewModel.DnsMarkState), changed);
        Assert.Contains(nameof(ConfigTransportViewModel.SignalMarkState), changed);
        Assert.False(transport.IsDirty);
    }

    [Fact]
    public void TheAnswerOfTheAgent_ReachesTheOpenPage()
    {
        var agent = new Silent();
        var catalogue = new ConfigViewModel(new MainWindowViewModel(agent, new UiPreferences()), agent);
        catalogue.Apply([Entry(ours: false)]);
        catalogue.OpenConfig = "office";
        var transport = catalogue.ConfigTransport;
        Assert.NotNull(transport);
        Assert.False(transport.ServerOurs);

        catalogue.Apply([Entry(ours: true)]);

        Assert.Same(transport, catalogue.ConfigTransport);
        Assert.True(transport.ServerOurs);
        Assert.True(catalogue.Configs[0].ServerOurs);
    }

    [Fact]
    public void TheMarkOfTheServer_CrossesTheChannel()
    {
        var line = JsonSerializer.Serialize(Entry(ours: true), IpcJson.Options);
        var earlier = JsonNode.Parse(line)!.AsObject();
        var name = earlier.Select(pair => pair.Key).Single(key => string.Equals(key, "ServerOurs", StringComparison.OrdinalIgnoreCase));
        earlier.Remove(name);

        Assert.True(JsonSerializer.Deserialize<ConfigEntry>(line, IpcJson.Options)!.ServerOurs);
        Assert.False(JsonSerializer.Deserialize<ConfigEntry>(earlier.ToJsonString(), IpcJson.Options)!.ServerOurs);
    }

    [Fact]
    public void AListOfTheServer_CarriesTheSign()
    {
        var served = new RoutingListSummaryViewModel { Source = "office-awg1-anna" };
        var own = new RoutingListSummaryViewModel();

        Assert.True(served.FromServer);
        Assert.Equal(Loc.Instance.Get("Main_ServerMarkList", "office-awg1-anna"), served.ServerMarkText);
        Assert.False(own.FromServer);
    }

    private static ConfigEntry Entry(bool ours) =>
        new("office", "vpn.example:51820", false, ConnectionStatus.Disconnected, [], ServerOurs: ours);

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
