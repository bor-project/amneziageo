using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The way of export is picked from a list: its row sets the QR mode, and the subscription row counts only where
/// the config has a subscription.
/// </summary>
public sealed class ConfigExportMethodTests
{
    [Fact]
    public void TheRows_SetTheModes()
    {
        var export = new ExportDialogViewModel(new Silent(), "ours") { SubscriptionUrl = "https://vpn.example/sub/1" };

        export.ModeIndex = 1;
        Assert.Equal(ConfigViewMode.QrLink, export.Mode);

        export.ModeIndex = 2;
        Assert.Equal(ConfigViewMode.QrSubscription, export.Mode);

        export.ModeIndex = 0;
        Assert.Equal(ConfigViewMode.QrConf, export.Mode);
    }

    [Fact]
    public void TheSubscriptionRow_FallsBackWithoutASubscription()
    {
        var export = new ExportDialogViewModel(new Silent(), "ours");

        export.ModeIndex = 2;

        Assert.Equal(ConfigViewMode.QrConf, export.Mode);
        Assert.Equal(0, export.ModeIndex);
    }

    [Fact]
    public void NoRow_KeepsTheMode()
    {
        var export = new ExportDialogViewModel(new Silent(), "ours");
        export.ModeIndex = 1;

        export.ModeIndex = -1;

        Assert.Equal(ConfigViewMode.QrLink, export.Mode);
    }

    [Fact]
    public void TheMode_MovesTheRow()
    {
        var export = new ExportDialogViewModel(new Silent(), "ours");
        var raised = new List<string?>();
        export.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        export.ShowQrLinkCommand.Execute(null);

        Assert.Equal(1, export.ModeIndex);
        Assert.Contains(nameof(ExportDialogViewModel.ModeIndex), raised);
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
