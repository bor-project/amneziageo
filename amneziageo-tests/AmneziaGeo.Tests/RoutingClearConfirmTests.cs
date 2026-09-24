using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Clearing the shown bucket of a routing list waits for a pair of buttons, so a stray touch leaves the rules be.
/// </summary>
public sealed class RoutingClearConfirmTests
{
    [Fact]
    public async Task TheLink_ArmsThePair_AndKeepsTheRules()
    {
        var editor = await OpenAsync();

        editor.RequestClearRulesCommand.Execute(null);

        Assert.True(editor.ClearPending);
        Assert.Equal(["geosite:youtube", "geosite:meta"], editor.Rules);
    }

    [Fact]
    public async Task TheConfirmation_EmptiesTheShownBucketAlone()
    {
        var editor = await OpenAsync();
        editor.RequestClearRulesCommand.Execute(null);

        editor.ClearRulesCommand.Execute(null);

        Assert.False(editor.ClearPending);
        Assert.Empty(editor.ProxyRules);
        Assert.Equal(["geoip:ru"], editor.DirectRules);
    }

    [Fact]
    public async Task Cancel_KeepsTheRules()
    {
        var editor = await OpenAsync();
        editor.RequestClearRulesCommand.Execute(null);

        editor.CancelClearRulesCommand.Execute(null);

        Assert.False(editor.ClearPending);
        Assert.Equal(2, editor.ProxyRules.Count);
    }

    [Fact]
    public async Task AnotherBucket_Disarms()
    {
        var editor = await OpenAsync();
        editor.RequestClearRulesCommand.Execute(null);

        editor.RoleIndex = 1;

        Assert.False(editor.ClearPending);
        Assert.Equal(2, editor.ProxyRules.Count);
    }

    [Fact]
    public async Task AnEditOfTheRules_Disarms()
    {
        var editor = await OpenAsync();
        editor.RequestClearRulesCommand.Execute(null);

        editor.Rules.Add("geosite:telegram");

        Assert.False(editor.ClearPending);
        Assert.Equal(3, editor.ProxyRules.Count);
    }

    [Fact]
    public async Task AnEmptyBucket_DoesNotArm()
    {
        var editor = await OpenAsync();
        editor.RoleIndex = 2;

        editor.RequestClearRulesCommand.Execute(null);

        Assert.False(editor.ClearPending);
    }

    private static async Task<RoutingListEditorViewModel> OpenAsync()
    {
        var editor = new RoutingListEditorViewModel(new Lists(), 6, "main");
        await editor.LoadAsync();
        return editor;
    }

    // Answers the list with two rules through the tunnel and one direct, and every other command with nothing.
    private sealed class Lists : IAgentConnection
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

        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(command.Op == IpcContract.OpGetRoutingList
            ? new IpcAck(true, "proxy|geosite:youtube\nproxy|geosite:meta\ndirect|geoip:ru")
            : new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
