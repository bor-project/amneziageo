using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A card dialled while the tunnel stands on another configuration: the window takes the tunnel down first, except
/// where the agent moves the tunnel itself and keeps it when the next server is silent.
/// </summary>
public sealed class SwitchUnderTunnelTests
{
    private const string Disconnect = IpcContract.OpSetConnection + " disconnect";
    private const string Connect = IpcContract.OpSetConnection + " connect";
    private const string Select = IpcContract.OpSelectConfig + " office";

    [Fact]
    public async Task WhereTheAgentMovesTheTunnel_TheWindowLeavesItStanding()
    {
        var link = new Recorder();
        var host = Window(link);
        UiPlatform.AgentMovesTunnel = true;
        try
        {
            await host.Home.ConnectConfigCommand.ExecuteAsync(host.Config.Configs.First(row => row.Name == "office"));
        }
        finally
        {
            UiPlatform.AgentMovesTunnel = false;
        }

        Assert.DoesNotContain(Disconnect, link.Sent);
        Assert.Contains(Select, link.Sent);
        Assert.Equal(Connect, link.Sent[^1]);
    }

    [Fact]
    public async Task Elsewhere_TheWindowTakesTheTunnelDownFirst()
    {
        var link = new Recorder();
        var host = Window(link);
        link.Stopped = () => host.Home.Apply(Snapshot(active: false));

        await host.Home.ConnectConfigCommand.ExecuteAsync(host.Config.Configs.First(row => row.Name == "office"));

        Assert.Equal(Disconnect, link.Sent[0]);
        Assert.Contains(Select, link.Sent);
        Assert.Equal(Connect, link.Sent[^1]);
    }

    // The window of an agent whose tunnel stands on the configuration "home".
    private static MainWindowViewModel Window(Recorder link)
    {
        var host = new MainWindowViewModel(link, new UiPreferences());
        var snapshot = Snapshot(active: true);
        host.Home.SetConnected();
        host.Config.Apply(snapshot);
        host.Home.Apply(snapshot);
        link.Sent.Clear();
        return host;
    }

    private static StatusSnapshot Snapshot(bool active)
    {
        var configs = new[] { "home", "office" }
            .Select(name => new ConfigEntry(
                name,
                name + ".example:51820",
                false,
                active && name == "home" ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                []))
            .ToList();

        return new StatusSnapshot(
            "1",
            active ? "home" : null,
            configs,
            Active: active,
            BoundStatus: active ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
            SelectedTarget: "home");
    }

    // Keeps the commands of the window and answers each with an ack.
    private sealed class Recorder : IAgentConnection
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

        /// <summary>
        /// The commands sent, each as its operation and arguments.
        /// </summary>
        public List<string> Sent { get; } = [];

        /// <summary>
        /// Called when the window asks the tunnel down.
        /// </summary>
        public Action? Stopped { get; set; }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            var sent = string.Join(' ', [command.Op, .. command.Args]);
            Sent.Add(sent);
            if (sent == Disconnect)
            {
                Stopped?.Invoke();
            }

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
