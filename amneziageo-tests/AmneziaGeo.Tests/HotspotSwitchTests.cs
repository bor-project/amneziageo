using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The access point switch turns on only on a saved name and password, the fields stay open with the switch off,
/// and the hidden network is saved with the fields.
/// </summary>
public sealed class HotspotSwitchTests
{
    private const string Name = "home-ap";
    private const string Password = "12345678";

    [Fact]
    public void TheSwitch_StaysLockedUntilANameAndAPasswordAreSaved()
    {
        var connections = new ConnectionsViewModel(new Recorder());

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true });

        Assert.False(connections.HotspotSwitchEnabled);
        Assert.True(connections.HotspotSettingsEnabled);
        Assert.True(connections.HotspotBlocked);
        Assert.Equal(Loc.Instance.Get("General_HotspotNeedsName"), connections.HotspotBlockedText);

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true, HotspotSsid = Name, HotspotPassword = Password });

        Assert.True(connections.HotspotSwitchEnabled);
        Assert.False(connections.HotspotBlocked);
    }

    [Fact]
    public void ANameTypedButNotSaved_LeavesTheSwitchLocked()
    {
        var connections = new ConnectionsViewModel(new Recorder());
        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true });

        connections.HotspotSsid = Name;
        connections.HotspotPassword = Password;

        Assert.True(connections.CanSave);
        Assert.False(connections.HotspotSwitchEnabled);
    }

    [Fact]
    public void APointThatIsOn_CanBeTurnedOffWhateverItsFields()
    {
        var connections = new ConnectionsViewModel(new Recorder());

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true, ShareMode = ShareModes.Both });

        Assert.True(connections.HotspotEnabled);
        Assert.True(connections.HotspotSwitchEnabled);
        Assert.False(connections.HotspotBlocked);
    }

    [Fact]
    public void AMachineWithoutAPoint_NamesItsCause()
    {
        var connections = new ConnectionsViewModel(new Recorder());

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = false, HotspotReason = HotspotReasons.NoAdapter });

        Assert.False(connections.HotspotSwitchEnabled);
        Assert.False(connections.HotspotSettingsEnabled);
        Assert.Equal(Loc.Instance.Get("General_HotspotNoAdapter"), connections.HotspotBlockedText);
    }

    [Fact]
    public void AnEmptyPassword_IsNotSavedWhileThePointIsOn()
    {
        var connections = new ConnectionsViewModel(new Recorder());
        connections.Apply(new StatusSnapshot("1.0", null, [])
        {
            HotspotSupported = true, ShareMode = ShareModes.Both, HotspotSsid = Name, HotspotPassword = Password,
        });

        connections.HotspotPassword = string.Empty;

        Assert.False(connections.CanSave);

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true, HotspotSsid = Name, HotspotPassword = Password });

        Assert.False(connections.HotspotEnabled);
        Assert.True(connections.CanSave);
    }

    [Fact]
    public async Task TheHiddenNetwork_IsSavedWithTheFieldsAndPutBackByCancel()
    {
        var agent = new Recorder();
        var connections = new ConnectionsViewModel(agent);
        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true, HotspotSsid = Name, HotspotPassword = Password });

        connections.HotspotHidden = true;

        Assert.True(connections.HotspotDirty);
        Assert.True(connections.CanSave);

        await connections.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal([SettingKeys.HotspotHidden, "on"], Assert.Single(agent.Sent));
        Assert.False(connections.HotspotDirty);

        connections.HotspotHidden = false;
        connections.CancelSectionCommand.Execute(null);

        Assert.True(connections.HotspotHidden);
        Assert.False(connections.HotspotDirty);
    }

    [Fact]
    public void TheSnapshot_BringsTheHiddenNetwork()
    {
        var connections = new ConnectionsViewModel(new Recorder());

        connections.Apply(new StatusSnapshot("1.0", null, []) { HotspotSupported = true, HotspotHidden = true });

        Assert.True(connections.HotspotHidden);
        Assert.False(connections.HotspotDirty);
    }

    // Keeps the settings the window sends instead of an agent.
    private sealed class Recorder : IAgentConnection
    {
        public List<IReadOnlyList<string>> Sent { get; } = [];

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
            if (command.Op == IpcContract.OpSetSetting)
            {
                Sent.Add(command.Args);
            }

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
