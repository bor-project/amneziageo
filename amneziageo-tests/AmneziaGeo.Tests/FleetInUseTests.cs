using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Ui.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// With several servers a configuration is in use while its server stands in the set or waits for the set to come
/// back on it, the reserve as well as the primary.
/// </summary>
public sealed class FleetInUseTests
{
    private static readonly string[] Names = ["primary", "reserve", "spare"];

    [Fact]
    public void TheReserveOfARunningSet_IsInUse()
    {
        var catalogue = Catalogue(new Silent(), Snapshot(wanted: ["primary", "reserve"], resume: []));

        Assert.Equal([true, true, false], Used(catalogue));
    }

    [Fact]
    public void TheServersTheSetComesBackOn_AreInUseWhileItIsDown()
    {
        var catalogue = Catalogue(new Silent(), Snapshot(wanted: [], resume: ["primary", "reserve"]));

        Assert.Equal([true, true, false], Used(catalogue));
    }

    private static FleetConfigViewModel Catalogue(IAgentConnection link, StatusSnapshot snapshot)
    {
        var host = new MainWindowViewModel(link, new UiPreferences());
        host.Config.Apply(snapshot);
        host.Home.Apply(snapshot);
        var catalogue = new FleetConfigViewModel(host, link);
        catalogue.Apply(snapshot);

        return catalogue;
    }

    private static bool[] Used(FleetConfigViewModel catalogue)
    {
        var used = new List<bool>();
        foreach (var name in Names)
        {
            catalogue.OpenConfig = name;
            used.Add(catalogue.UseOpenConfig);
        }

        return [.. used];
    }

    private static StatusSnapshot Snapshot(string[] wanted, string[] resume)
    {
        var configs = Names
            .Select(name => new ConfigEntry(
                name,
                name + ".example:51820",
                false,
                wanted.Contains(name) ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                []))
            .ToList();
        var servers = Names
            .Select((name, at) => new FleetEntry(
                name,
                at == 0 ? TunnelRoles.Primary : TunnelRoles.Reserve,
                wanted.Contains(name),
                Slot: at < 2 ? at + 1 : TunnelRoles.Aside))
            .ToList();

        return new StatusSnapshot(
            "1",
            wanted.Length > 0 ? "primary" : null,
            configs,
            SelectedTarget: "primary",
            MultiServer: true,
            Fleet: new FleetSnapshot(servers, Primary: "primary", Resume: resume));
    }

    // Answers every command without doing anything.
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
