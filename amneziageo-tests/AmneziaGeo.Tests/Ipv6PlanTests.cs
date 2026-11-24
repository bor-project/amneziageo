using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class Ipv6PlanTests
{
    private const string DualStack = "[Interface]\nAddress = 10.0.0.2/32, fd00::2/128\nDNS = 10.0.0.1, fd00::1\n[Peer]\nEndpoint = 192.0.2.1:443\nAllowedIPs = 0.0.0.0/0, ::/0\n";

    [Theory]
    [InlineData("10.0.0.2/32", "0.0.0.0/0", false)]
    [InlineData("fd00::2/128", "::/0", true)]
    [InlineData("fd00::2/128", "0.0.0.0/0", false)]
    [InlineData("fd00::2/128", "", true)]
    [InlineData("fd00::2/129", "::/0", false)]
    [InlineData("fd00::2/128", "::/129", false)]
    [InlineData("::/128", "::/0", false)]
    [InlineData("::1/128", "::/0", false)]
    [InlineData("fe80::2/64", "::/0", false)]
    [InlineData("ff02::2/128", "::/0", false)]
    [InlineData("::ffff:192.0.2.1/128", "::/0", false)]
    public void AutoUsesTunnelAddressesAndRoutes(string address, string routes, bool enabled)
    {
        var config = $"[Interface]\nAddress = {address}\n[Peer]\nEndpoint = 192.0.2.1:51820\nAllowedIPs = {routes}\n";
        Assert.Equal(enabled, Ipv6Plan.Enabled(null, config));
        Assert.Equal(enabled, Ipv6Plan.Enabled(new ConfigTransport("test", false) { Ipv6Mode = Ipv6Mode.Auto }, config));
    }

    [Fact]
    public void AutoDoesNotRequireIpv6OnTheCarrier()
    {
        Assert.True(Ipv6Plan.Enabled(null, DualStack));
        Assert.True(Ipv6Plan.Enabled(null, DualStack.Replace("192.0.2.1:443", "127.0.0.1:12000")));
        Assert.True(Ipv6Plan.Enabled(null, DualStack.Replace("192.0.2.1:443", "[2001:db8::1]:443")));
        Assert.False(Ipv6Plan.Available(DualStack, supported: false));
    }

    [Fact]
    public void RepeatedLinesAndCommentsAreReadWithinTheirSections()
    {
        var config = "[Interface]\nAddress=10.0.0.2/32\nAddress=fd00::2/128 #assigned\n[Peer]\nAllowedIPs=0.0.0.0/0\nAllowedIPs=::/0 #route\n";
        Assert.True(Ipv6Plan.Available(config));
        Assert.False(Ipv6Plan.Available(config.Replace("Address=fd00::2/128 #assigned", "#Address=fd00::2/128")));
        Assert.False(Ipv6Plan.Available("[Peer]\nAddress=fd00::2/128\nAllowedIPs=::/0"));
    }

    [Theory]
    [InlineData(true, Ipv6Mode.On)]
    [InlineData(false, Ipv6Mode.Off)]
    public void LegacyExplicitChoicesKeepTheirMeaning(bool enabled, Ipv6Mode mode)
    {
        var transport = new ConfigTransport("old", false, UseIpv6: enabled);
        Assert.Equal(mode, Ipv6Modes.Of(transport));
        Assert.Equal(enabled, Ipv6Plan.Enabled(transport, DualStack));
    }

    [Fact]
    public void OffRemovesEveryIpv6AddressRouteAndDnsWithoutChangingTheEndpoint()
    {
        var config = DualStack + "Address = fd00::3/128\nAllowedIPs = fd00::/64\n";
        var filtered = Ipv6Plan.ForTunnel(config, false);
        Assert.DoesNotContain("fd00::", filtered);
        Assert.DoesNotContain("::/0", filtered);
        Assert.Contains("Address = 10.0.0.2/32", filtered);
        Assert.Contains("Endpoint = 192.0.2.1:443", filtered);
        Assert.Equal(config, Ipv6Plan.ForTunnel(config, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Ipv6Mode.Auto)]
    [InlineData(Ipv6Mode.On)]
    [InlineData(Ipv6Mode.Off)]
    public async Task PolicySurvivesStorageAndNeighbouringEdits(Ipv6Mode? mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipv6-policy-{Guid.NewGuid():N}.db");
        var store = new SqliteStateStore(path);
        try
        {
            await store.InitializeAsync().ConfigureAwait(false);
            var transport = new ConfigTransport("sample", false, UseIpv6: true) { Ipv6Mode = mode };
            await store.SetConfigTransportAsync(transport).ConfigureAwait(false);
            Assert.Equal(transport, await store.GetConfigTransportAsync("sample").ConfigureAwait(false));
            await ConfigServicePortCommands.SetAsync(store, ["sample", "9443"], true).ConfigureAwait(false);
            Assert.Equal(transport with { ServicesPort = 9443 }, await store.GetConfigTransportAsync("sample").ConfigureAwait(false));
            await store.InitializeAsync().ConfigureAwait(false);
            Assert.Equal(mode, (await store.GetConfigTransportAsync("sample").ConfigureAwait(false))!.Ipv6Mode);
        }
        finally
        {
            store.ClearPool();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldDatabaseMigrationPreservesTheExplicitSwitch(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipv6-legacy-{Guid.NewGuid():N}.db");
        var store = new SqliteStateStore(path);
        try
        {
            await store.InitializeAsync().ConfigureAwait(false);
            var transport = new ConfigTransport("old", false, UseIpv6: enabled);
            await store.SetConfigTransportAsync(transport).ConfigureAwait(false);
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE config_transport DROP COLUMN ipv6_mode";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await store.InitializeAsync().ConfigureAwait(false);
            var restored = await store.GetConfigTransportAsync("old").ConfigureAwait(false);
            Assert.Equal(transport, restored);
            Assert.Equal(enabled, Ipv6Plan.Enabled(restored, DualStack));
        }
        finally
        {
            store.ClearPool();
            File.Delete(path);
        }
    }

    [Fact]
    public void OldIpcAndBundlesRetainTheirBooleanChoice()
    {
        var block = JsonSerializer.Deserialize<PortableBundle.TransportBlock>("{\"UseWebSocket\":false,\"Host\":\"\",\"Port\":0,\"Mtu\":1420,\"UseIpv6\":true}");
        Assert.Null(block!.Ipv6Mode);
        Assert.True(block.UseIpv6);
        var entry = new ConfigEntry("sample", "192.0.2.1:443", false, "idle", [], UseIpv6: true);
        var restored = JsonSerializer.Deserialize<ConfigEntry>(JsonSerializer.Serialize(entry, IpcJson.Options), IpcJson.Options);
        Assert.Null(restored!.Ipv6Mode);
        Assert.True(restored.UseIpv6);
        var auto = entry with { Ipv6Mode = Ipv6Mode.Auto, Ipv6Available = true };
        var restoredAuto = JsonSerializer.Deserialize<ConfigEntry>(JsonSerializer.Serialize(auto, IpcJson.Options), IpcJson.Options);
        Assert.Equal(Ipv6Mode.Auto, restoredAuto!.Ipv6Mode);
        Assert.True(restoredAuto.Ipv6Available);
        var exported = block with { Ipv6Mode = Ipv6Mode.Auto };
        Assert.Equal(exported, JsonSerializer.Deserialize<PortableBundle.TransportBlock>(JsonSerializer.Serialize(exported)));
    }

    [Fact]
    public async Task EditorSavesAutoAndRevertsDraftPolicy()
    {
        var recorder = new Recorder();
        var editor = new ConfigTransportViewModel(recorder, "sample", false, 1420, false, resolvedMtu: 1330,
            ipv6Mode: Ipv6Mode.Off, ipv6Available: true);
        editor.Ipv6Mode = (int)Ipv6Mode.Auto;
        Assert.True(editor.ConnectionChanged);
        Assert.Equal(Loc.Instance.Get("Transport_Ipv6On"), editor.Ipv6Status);
        Assert.True(await editor.CommitAsync().ConfigureAwait(false));
        Assert.Equal("auto", recorder.Last!.Args[3]);
        editor.CaptureBaseline();
        editor.Ipv6Mode = (int)Ipv6Mode.Off;
        editor.Revert();
        Assert.Equal((int)Ipv6Mode.Auto, editor.Ipv6Mode);
        Assert.False(editor.IsDirty);
        editor.RefreshNetworkValues(1290, false);
        Assert.Equal(Loc.Instance.Get("Transport_Ipv6Off"), editor.Ipv6Status);
        Assert.Equal(Loc.Instance.Get("Transport_MtuResolved", 1290), editor.ResolvedMtuText);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void MtuAutoShowsOnlyAResolvedValueAndCardsOmitNetworkToggles()
    {
        var editor = new ConfigTransportViewModel(new Recorder(), "sample", false, 1420, false);
        Assert.True(editor.ShowResolvedMtu);
        Assert.Equal(Loc.Instance.Get("Transport_MtuPending"), editor.ResolvedMtuText);
        editor.MtuMode = (int)MtuMode.Custom;
        Assert.False(editor.ShowResolvedMtu);
        var card = new ConfigItemViewModel { UseIpv6 = true, Mtu = 1330, MtuMode = MtuMode.Custom };
        Assert.DoesNotContain(card.Tags, tag => tag.Text.Contains("IPv6", StringComparison.OrdinalIgnoreCase) || tag.Text.Contains("MTU", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Recorder : IAgentConnection
    {
        public IpcCommand? Last { get; private set; }
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);
        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Last = command;
            return Task.FromResult(new IpcAck(true, string.Empty));
        }
    }
}
