using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The advanced settings of a routing list offer how the system reaches the name proxy; the agent's own transport
/// is set from the command line only.
/// </summary>
public sealed class RoutingDnsSettingsTests
{
    [Fact]
    public void TheSettings_OfferNoAgentTransport()
    {
        var type = typeof(RoutingSettingsViewModel);

        Assert.NotNull(type.GetProperty(nameof(RoutingSettingsViewModel.LocalDohChoice)));
        Assert.Null(type.GetProperty("DnsTransportChoice"));
        Assert.Null(type.GetProperty("ShowDnsTransport"));
    }

    [Fact]
    public async Task SavingTheSystemMode_SendsItAlone()
    {
        var connection = new Recording();
        var settings = new RoutingSettingsViewModel(connection, 7);
        settings.ApplyLocalDoh(LocalDohModes.Auto);

        settings.LocalDohChoice = 1;

        Assert.True(settings.IsDirty);
        Assert.True(await settings.CommitAsync());
        var pushed = Assert.Single(connection.Commands, command => command.Op == IpcContract.OpSetSetting);
        Assert.Equal([SettingKeys.LocalDoh, LocalDohModes.Off], pushed.Args);
    }

    // Keeps every command and answers it with success.
    private sealed class Recording : IAgentConnection
    {
        public List<IpcCommand> Commands { get; } = [];

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
            Commands.Add(command);
            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
