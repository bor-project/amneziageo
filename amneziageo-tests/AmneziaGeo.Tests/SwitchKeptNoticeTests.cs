using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A move to another configuration that its server left unanswered: the window names both configurations and offers
/// no reconnect while the tunnel stands on the one before it.
/// </summary>
public sealed class SwitchKeptNoticeTests
{
    private const string Kept = "SwitchNoHandshake";

    [Fact]
    public void AKeptTunnel_IsToldWithBothNames()
    {
        var host = Window();

        host.Home.Apply(Snapshot(active: true, Kept, "office"));

        Assert.True(host.Home.NoticeVisible);
        Assert.Equal(Loc.Instance.Get("MainVm_NoticeSwitchKept", "office", "home"), host.Home.NoticeText);
        Assert.Contains("office", host.Home.NoticeText);
        Assert.Contains("home", host.Home.NoticeText);
    }

    [Fact]
    public void AKeptTunnel_OffersNoReconnect()
    {
        var host = Window();

        host.Home.Apply(Snapshot(active: true, Kept, "office"));

        Assert.False(host.Home.ReconnectAvailable);
        Assert.True(host.Home.CanDismissNotice);
    }

    [Fact]
    public void OnceTheKeptTunnelIsDown_TheNoticeOffersTheReconnect()
    {
        var host = Window();

        host.Home.Apply(Snapshot(active: false, Kept, "office"));

        Assert.Equal(Loc.Instance.Get("MainVm_NoticeConnectFailed_NoHandshake"), host.Home.NoticeText);
        Assert.True(host.Home.ReconnectAvailable);
    }

    [Fact]
    public void ADialThatFailed_OffersTheReconnect()
    {
        var host = Window();

        host.Home.Apply(Snapshot(active: false, "NoHandshake", string.Empty));

        Assert.Equal(Loc.Instance.Get("MainVm_NoticeConnectFailed_NoHandshake"), host.Home.NoticeText);
        Assert.True(host.Home.ReconnectAvailable);
    }

    // The window of an agent whose tunnel stands on the configuration "home".
    private static MainWindowViewModel Window()
    {
        var host = new MainWindowViewModel(new Silent(), new UiPreferences());
        var snapshot = Snapshot(active: true, string.Empty, string.Empty);
        host.Home.SetConnected();
        host.Config.Apply(snapshot);
        host.Home.Apply(snapshot);
        return host;
    }

    // The agent with two configurations; a reason given marks the last connect as failed.
    private static StatusSnapshot Snapshot(bool active, string reason, string detail)
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
            SelectedTarget: "home",
            ConnectFailed: reason.Length > 0,
            ConnectFailReason: reason,
            ConnectFailDetail: detail);
    }

    // Answers every command with an ack.
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
