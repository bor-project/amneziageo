using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A configuration holds its traffic while its tunnel is repaired or not: the switch is kept with the transport and
/// stands on by default, also where an earlier version stored the leak guard off, travels in a bundle and goes to the
/// agent in a command of its own.
/// </summary>
public sealed class HoldTrafficTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-hold-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheSwitch_IsKeptWithTheTransportAndStandsOnByDefault()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("plain", false));
        await _store.SetConfigTransportAsync(new ConfigTransport("loose", false, HoldTraffic: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("back", false, HoldTraffic: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("back", false));

        Assert.True((await _store.GetConfigTransportAsync("plain"))?.HoldTraffic);
        Assert.False((await _store.GetConfigTransportAsync("loose"))?.HoldTraffic);
        Assert.True((await _store.GetConfigTransportAsync("back"))?.HoldTraffic);
    }

    [Fact]
    public async Task AnotherSwitchSaved_KeepsTheHold()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("office", false, HoldTraffic: false));
        var stored = await _store.GetConfigTransportAsync("office");

        await _store.SetConfigTransportAsync(stored! with { AllowSignal = false, UseIpv6 = true });

        var again = await _store.GetConfigTransportAsync("office");
        Assert.False(again?.HoldTraffic);
        Assert.False(again?.AllowSignal);
    }

    [Fact]
    public void ABundle_CarriesTheSwitchAndAnEarlierOneLeavesItOn()
    {
        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            [new PortableBundle.ConfigBlock("office", "[Interface]\n", new PortableBundle.TransportBlock(true, string.Empty, 0, 1380, HoldTraffic: false), null)],
            []);
        const string earlier = """
            {
              "Format": "amneziageo-bundle",
              "Version": 2,
              "Configs": [
                {
                  "Name": "office",
                  "ConfigText": "[Interface]\n",
                  "Transport": { "UseWebSocket": true, "Host": "", "Port": 0, "Mtu": 1380, "LeakGuard": false }
                }
              ],
              "RoutingLists": []
            }
            """;

        Assert.False(PortableBundle.Deserialize(PortableBundle.Serialize(bundle))?.Configs[0].Transport?.HoldTraffic);
        Assert.True(PortableBundle.Deserialize(earlier)?.Configs[0].Transport?.HoldTraffic);
    }

    [Fact]
    public void AnEntry_HoldsTheTrafficUnlessItSaysOtherwise()
    {
        Assert.True(new ConfigEntry("office", "10.9.1.1:51821", false, "idle", []).HoldTraffic);
        Assert.False(new ConfigEntry("office", "10.9.1.1:51821", false, "idle", [], HoldTraffic: false).HoldTraffic);
    }

    [Fact]
    public async Task TheSwitchOfTheWindow_GoesInACommandOfItsOwn()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false);

        Assert.True(transport.HoldTraffic);
        transport.HoldTraffic = false;
        var dirty = transport.IsDirty;
        await transport.CommitAsync();

        Assert.True(dirty);
        Assert.Equal([IpcContract.OpSetWebSocket, IpcContract.OpSetHoldTraffic], agent.Ops);
        Assert.Equal(["e2e", "off"], agent.Last);
    }

    [Fact]
    public async Task ASaveOfAnotherSetting_SendsNoCommandOfTheSwitch()
    {
        var agent = new Recorder();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, holdTraffic: false);

        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.False(transport.HoldTraffic);
        Assert.Equal([IpcContract.OpSetWebSocket], agent.Ops);
    }

    [Fact]
    public void TheSwitchPutBack_LeavesNothingToSave()
    {
        var transport = new ConfigTransportViewModel(new Recorder(), "e2e", false, 1420, false);

        transport.HoldTraffic = false;
        transport.Revert();

        Assert.True(transport.HoldTraffic);
        Assert.False(transport.IsDirty);
    }

    // Keeps the commands the window sends instead of an agent.
    private sealed class Recorder : IAgentConnection
    {
        public List<string> Ops { get; } = [];

        public IReadOnlyList<string> Last { get; private set; } = [];

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
            Ops.Add(command.Op);
            Last = command.Args;

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
