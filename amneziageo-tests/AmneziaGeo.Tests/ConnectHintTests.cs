using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The line under the status on the home screen speaks of a connection being established only while one is.
/// </summary>
public sealed class ConnectHintTests
{
    [Theory]
    [InlineData(ConnectionStatus.Disconnecting)]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.Connecting)]
    public void WhileTheTunnelGoesDown_NothingSpeaksOfConnecting(string status)
    {
        var home = Home(active: false, status);

        Assert.Equal(Loc.Instance.Get("Status_Disconnecting"), home.AgentStatusText);
        Assert.False(home.ShowConnectHint);
        Assert.Equal(Loc.Instance.Get("Status_Disconnecting"), home.ConnectHint);
    }

    [Fact]
    public void WhileTheTunnelComesUp_TheHintSaysSo()
    {
        var home = Home(active: true, ConnectionStatus.Connecting);

        Assert.Equal(Loc.Instance.Get("Status_Connecting"), home.AgentStatusText);
        Assert.True(home.ShowConnectHint);
        Assert.Equal(Loc.Instance.Get("MainVm_ConnectHintConnecting"), home.ConnectHint);
    }

    [Fact]
    public void WithNothingSelectedAndNothingRunning_TheHintAsksForAConfiguration()
    {
        var home = Home(active: false, ConnectionStatus.Disconnected);

        Assert.True(home.ShowConnectHint);
        Assert.Equal(Loc.Instance.Get("MainVm_ConnectHintSelectConfig"), home.ConnectHint);
    }

    [Fact]
    public void TheHintLine_FollowsTheSwitchAtOnce()
    {
        var home = Home(active: true, ConnectionStatus.Connecting);
        var raised = new List<string?>();
        home.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        home.IsTunnelActive = false;

        Assert.Contains(nameof(ConnectionViewModel.ShowConnectHint), raised);
        Assert.False(home.ShowConnectHint);
    }

    [Fact]
    public void TheHintLine_FollowsTheStatusAtOnce()
    {
        var home = Home(active: true, ConnectionStatus.Disconnected);
        var raised = new List<string?>();
        home.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        home.BoundStatus = ConnectionStatus.Connected;

        Assert.Contains(nameof(ConnectionViewModel.ShowConnectHint), raised);
        Assert.False(home.ShowConnectHint);
    }

    // The home screen of a window the agent answers, with the switch and the tunnel status as given.
    private static ConnectionViewModel Home(bool active, string status)
    {
        var home = new MainWindowViewModel(new Silent(), new UiPreferences()).Home;
        home.SetConnected();
        home.IsTunnelActive = active;
        home.BoundStatus = status;
        return home;
    }

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
