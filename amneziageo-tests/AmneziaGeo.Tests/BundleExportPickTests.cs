using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window picks the routing lists of an export by number: two lists under one name go apart.
/// </summary>
public sealed class BundleExportPickTests
{
    [Fact]
    public async Task OneOfTwoListsUnderOneName_GoesByItsNumber_WithItsRules()
    {
        var agent = new Recorder();
        var export = new BundleExportViewModel(agent, [], [List(6, "main"), List(8, "main")]);
        await export.LoadRoutingRulesAsync();
        var second = export.RoutingItems[1];
        second.IsChecked = true;
        second.Rules[1].IsChecked = false;

        await export.ExportCommand.ExecuteAsync(null);

        Assert.Contains("\"routingLists\":[\"8\"]", agent.Selection, StringComparison.Ordinal);
        Assert.Contains("\"routingRules\":{\"8\":[\"proxy|domain:a.example\"]}", agent.Selection, StringComparison.Ordinal);
    }

    private static RoutingListSummaryViewModel List(long id, string name) => new() { Id = id, Name = name };

    // Hands every list the same two rules and keeps the selection of the export.
    private sealed class Recorder : IAgentConnection
    {
        public string Selection { get; private set; } = string.Empty;

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
            if (command.Op == IpcContract.OpExportBundle)
            {
                Selection = command.Args[0];
            }

            return Task.FromResult(command.Op == IpcContract.OpGetRoutingList
                ? new IpcAck(true, "proxy|domain:a.example\nproxy|domain:b.example")
                : new IpcAck(true, "{}"));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
