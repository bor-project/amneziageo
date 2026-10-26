using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The tunnel mode of a routing list is picked from a list, and the picked row sets the global-proxy flag.
/// </summary>
public sealed class RoutingVpnModeTests
{
    [Fact]
    public void TheSecondRow_CarriesEverything()
    {
        var settings = new RoutingSettingsViewModel(new Silent(), 0);

        settings.VpnModeIndex = 1;

        Assert.True(settings.UseGlobalProxy);
        Assert.False(settings.IsSelectedOnly);
        Assert.True(settings.IsDirty);
    }

    [Fact]
    public void TheFirstRow_CarriesTheSelectedOnly()
    {
        var settings = new RoutingSettingsViewModel(new Silent(), 0);
        settings.UseGlobalProxy = true;

        settings.VpnModeIndex = 0;

        Assert.False(settings.UseGlobalProxy);
        Assert.True(settings.IsSelectedOnly);
    }

    [Fact]
    public void AClearedPick_KeepsTheMode()
    {
        var settings = new RoutingSettingsViewModel(new Silent(), 0);
        settings.VpnModeIndex = 1;

        settings.VpnModeIndex = -1;

        Assert.True(settings.UseGlobalProxy);
        Assert.Equal(1, settings.VpnModeIndex);
    }

    [Fact]
    public void Cancel_ReturnsTheRow()
    {
        var settings = new RoutingSettingsViewModel(new Silent(), 0);
        settings.CaptureBaseline();
        settings.VpnModeIndex = 1;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Revert();

        Assert.Equal(0, settings.VpnModeIndex);
        Assert.Contains(nameof(RoutingSettingsViewModel.VpnModeIndex), raised);
        Assert.False(settings.IsDirty);
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
