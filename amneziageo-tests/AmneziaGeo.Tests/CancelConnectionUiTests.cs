using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class CancelConnectionUiTests
{
    [Fact]
    public async Task HeaderCancelsAConnectingSnapshotBeforeTheTunnelIsActive()
    {
        var agent = new Recorder();
        var host = Window(agent, ConnectionStatus.Connecting);
        await host.Home.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(["disconnect"], agent.ConnectionCommands);
    }

    [Fact]
    public async Task CardCancelsAConnectingSnapshotWithoutABoundTarget()
    {
        var agent = new Recorder();
        var host = Window(agent, ConnectionStatus.Connecting);
        var row = host.Config.Configs.Single();
        Assert.True(host.Home.ConnectConfigCommand.CanExecute(row));
        await host.Home.ConnectConfigCommand.ExecuteAsync(row);
        Assert.Equal(["disconnect"], agent.ConnectionCommands);
    }

    [Fact]
    public async Task HeaderRemainsAvailableWhileTheConnectAcknowledgementIsPending()
    {
        var agent = new Recorder { ConnectReply = new() };
        var host = Window(agent);
        var connecting = host.Home.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(host.Home.ToggleConnectionCommand.CanExecute(null));
        await host.Home.ToggleConnectionCommand.ExecuteAsync(null);
        agent.ConnectReply.SetResult(new IpcAck(true, "connecting"));
        await connecting;
        Assert.Equal(["connect", "disconnect"], agent.ConnectionCommands);
        Assert.False(host.Home.IsTunnelActive);
    }

    [Fact]
    public async Task CancellationDuringSelectionDoesNotSendALateConnect()
    {
        var agent = new Recorder { SelectReply = new() };
        var host = Window(agent);
        var connecting = host.Home.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(host.Home.ToggleConnectionCommand.CanExecute(null));
        await host.Home.ToggleConnectionCommand.ExecuteAsync(null);
        agent.SelectReply.SetResult(new IpcAck(true, "selected"));
        await connecting;
        Assert.Equal(["disconnect"], agent.ConnectionCommands);
    }

    [Fact]
    public async Task CardRemainsAvailableWhileItsConnectAcknowledgementIsPending()
    {
        var agent = new Recorder { ConnectReply = new() };
        var host = Window(agent);
        var row = host.Config.Configs.Single();
        var connecting = host.Home.ConnectConfigCommand.ExecuteAsync(row);
        Assert.True(host.Home.ConnectConfigCommand.CanExecute(row));
        await host.Home.ConnectConfigCommand.ExecuteAsync(row);
        agent.ConnectReply.SetResult(new IpcAck(false, "cancelled"));
        await connecting;
        Assert.Equal(["connect", "disconnect"], agent.ConnectionCommands);
        Assert.False(host.Home.IsTunnelActive);
    }

    [Fact]
    public void ASecondClickDuringTeardownCannotRedial()
    {
        var host = Window(new Recorder(), ConnectionStatus.Disconnecting);
        Assert.False(host.Home.ToggleConnectionCommand.CanExecute(null));
        Assert.False(host.Home.ConnectConfigCommand.CanExecute(host.Config.Configs.Single()));
    }

    private static MainWindowViewModel Window(Recorder agent, string status = ConnectionStatus.Disconnected)
    {
        var host = new MainWindowViewModel(agent, new UiPreferences(persist: false));
        host.Home.WatchNetwork(false);
        host.Home.Seen(false);
        var snapshot = new StatusSnapshot("test", null,
            [new ConfigEntry("test", "127.0.0.1:9", false, status, [])],
            Active: false, BoundStatus: status, SelectedTarget: "test");
        host.Home.SetConnected();
        host.Config.Apply(snapshot);
        host.Home.Apply(snapshot);
        agent.ConnectionCommands.Clear();
        return host;
    }

    private sealed class Recorder : IAgentConnection
    {
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public List<string> ConnectionCommands { get; } = [];
        public TaskCompletionSource<IpcAck>? ConnectReply { get; init; }
        public TaskCompletionSource<IpcAck>? SelectReply { get; init; }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op == IpcContract.OpSelectConfig && SelectReply is not null)
            {
                return SelectReply.Task;
            }

            if (command.Op == IpcContract.OpSetConnection)
            {
                ConnectionCommands.Add(command.Args[0]);
                if (command.Args[0] == "connect" && ConnectReply is not null)
                {
                    return ConnectReply.Task;
                }
            }

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);
        public void Start() { }
        public void Dispose() { }
    }
}
