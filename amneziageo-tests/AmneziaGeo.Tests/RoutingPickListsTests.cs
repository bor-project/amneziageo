using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Buckets, add methods, export forms, new-list methods and connection areas are picked from lists: each row sets
/// its state, and a cleared pick keeps the one before.
/// </summary>
public sealed class RoutingPickListsTests
{
    [Fact]
    public void TheBucketRow_ShowsItsBucket()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.RoleIndex = 2;
        editor.RoleIndex = -1;

        Assert.Equal("block", editor.SelectedRole);
        Assert.Same(editor.BlockRules, editor.Rules);
        Assert.Equal(2, editor.RoleIndex);
    }

    [Fact]
    public void TheApplicationRow_TakesApplications()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.AddMethodIndex = 1;
        editor.AddMethodIndex = -1;

        Assert.True(editor.IsAppMethod);
        Assert.Equal(1, editor.AddMethodIndex);
    }

    [Fact]
    public void TheTextRow_ShowsTheText()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.TransferIndex = 1;
        editor.TransferIndex = -1;

        Assert.True(editor.IsTransferText);
        Assert.Equal(1, editor.TransferIndex);
    }

    [Fact]
    public void AddingAList_OpensThePresetFormUnderTheList()
    {
        var routing = Routing();

        routing.BeginAddListCommand.Execute(null);

        Assert.True(routing.ShowImportMethods);
        Assert.True(routing.ShowImportPresets);
        Assert.False(routing.ShowImportEditor);
        Assert.Equal(0, routing.ImportMethodIndex);
        Assert.Equal("Closed", routing.SelectedPresetCard?.Preset.Key);
    }

    [Fact]
    public void TheManualRow_PutsTheEditorUnderTheList()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        routing.ImportMethodIndex = 1;

        Assert.True(routing.ShowImportMethods);
        Assert.True(routing.ShowImportEditor);
        Assert.False(routing.ShowImportPresets);
        Assert.True(routing.ShowSaveButton);
    }

    [Fact]
    public void TheImportRow_ShowsItsButtonsAlone()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        routing.ImportMethodIndex = 2;

        Assert.True(routing.IsImportExternal);
        Assert.True(routing.ShowImportMethods);
        Assert.False(routing.ShowImportPresets);
        Assert.False(routing.ShowImportEditor);
    }

    [Fact]
    public async Task ThePickedPreset_FillsADraftWithoutTheList()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        await routing.ApplyPresetCommand.ExecuteAsync(routing.SelectedPresetCard);

        Assert.True(routing.IsImportDraft);
        Assert.True(routing.ShowImportEditor);
        Assert.False(routing.ShowImportMethods);
        Assert.Contains("geosite:ru-blocked", routing.RoutingEditor!.ProxyRules);
    }

    [Fact]
    public void BackFromThePresetForm_LeavesTheNewList()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        Assert.True(routing.TryNavigateBack());

        Assert.False(routing.IsSectionImport);
    }

    [Fact]
    public void TheAreaRow_ShowsItsArea()
    {
        var connections = new ConnectionsViewModel(new Silent());

        connections.ShareTabIndex = 2;
        connections.ShareTabIndex = -1;

        Assert.Equal(2, connections.ShareTabIndex);
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), connections.IsWifiTab);
    }

    private static RoutingViewModel Routing() => new MainWindowViewModel(new Silent(), new UiPreferences()).Routing;

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
