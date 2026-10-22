using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The names that go through the VPN of a config are asked of what the config picks, else what its server picked, else
/// the DNS line of the config: the server names its pick in its offer, the choice is kept beside the name servers of the
/// names that go directly and travels in a bundle.
/// </summary>
public sealed class VpnDnsTests : IAsyncLifetime
{
    private static readonly string[] Line = ["10.0.0.1"];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-vpndns-{Guid.NewGuid():N}.db");
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
    public void ThePick_TakesTheChoiceOfTheConfigThenThePickOfTheServerThenTheLine()
    {
        var panel = new OfferedDns("panel", ["10.8.1.1"], ["10.8.1.1"]);
        var routing = new OfferedDns("routing", ["1.1.1.1"], ["10.8.1.1"]);
        var network = new OfferedDns("network", [], ["10.8.1.1"]);
        var silent = new OfferedDns("routing", ["1.1.1.1"], []);

        Assert.Equal(new VpnDnsPick(false, Line), Pick(null, null));
        Assert.Equal(["10.8.1.1"], Pick(null, panel).Servers);
        Assert.Equal(["1.1.1.1"], Pick(Held(VpnDnsMode.Default), routing).Servers);
        Assert.Equal(new VpnDnsPick(false, Line), Pick(null, new OfferedDns("endpoint", [], [])));
        Assert.True(Pick(null, network).Network);
        Assert.True(Pick(Held(VpnDnsMode.Network), panel).Network);
        Assert.Empty(Pick(Held(VpnDnsMode.Network), panel).Servers);
        Assert.Equal(["10.8.1.1"], Pick(Held(VpnDnsMode.Server), routing).Servers);
        Assert.Equal(["1.1.1.1"], Pick(Held(VpnDnsMode.Server), silent).Servers);
        Assert.Equal(["1.1.1.1"], Pick(Held(VpnDnsMode.Server), new OfferedDns("routing", ["1.1.1.1"], ["fd00::1"])).Servers);
        Assert.Equal(Line, Pick(Held(VpnDnsMode.Server), null).Servers);
        Assert.Equal(["9.9.9.9", "2001:db8::53"], Pick(Held(VpnDnsMode.Own, "9.9.9.9, odd; 2001:db8::53 9.9.9.9"), panel).Servers);
        Assert.Equal(VpnDns.Usual, Pick(Held(VpnDnsMode.Own), panel).Servers);
        Assert.False(Pick(Held(VpnDnsMode.Own, "9.9.9.9"), network).Network);
    }

    [Fact]
    public void TheWindow_ShowsWhatThePickSettlesOnAndTheResolverOfTheServer()
    {
        var routing = new OfferedDns("routing", ["10.8.1.1"], ["10.8.1.1"]);

        var network = new OfferedDns("network", [], []);

        Assert.Equal(new VpnDnsShowing("10.8.1.1", false, "10.8.1.1", "10.8.1.1", false), VpnDns.Showing(null, routing, Line));
        Assert.Equal(new VpnDnsShowing(string.Empty, true, "10.8.1.1", "10.8.1.1", false), VpnDns.Showing(Held(VpnDnsMode.Network), routing, Line));
        Assert.Equal(new VpnDnsShowing("1.1.1.1, 1.0.0.1", false, string.Empty, "1.1.1.1, 1.0.0.1", false), VpnDns.Showing(null, null, []));
        Assert.Equal(new VpnDnsShowing("9.9.9.9", false, string.Empty, "10.0.0.1", false), VpnDns.Showing(Held(VpnDnsMode.Own, "9.9.9.9"), null, Line));
        Assert.Equal(new VpnDnsShowing("9.9.9.9", false, string.Empty, string.Empty, true), VpnDns.Showing(Held(VpnDnsMode.Own, "9.9.9.9"), network, Line));
        Assert.Equal(string.Empty, VpnDns.Showing(null, new OfferedDns("routing", ["1.1.1.1"], ["fd00::1"]), Line).Server);
        Assert.Equal("10.8.1.1, fd00::1", VpnDns.Showing(null, new OfferedDns("routing", ["1.1.1.1"], ["10.8.1.1", "fd00::1"]), Line).Server);
    }

    [Fact]
    public void TheWordsOfTheChoice_ReadBackAndTheOddAddressesAreNamed()
    {
        foreach (var mode in Enum.GetValues<VpnDnsMode>())
        {
            Assert.Equal(mode, VpnDns.Read(VpnDns.NameOf(mode)));
        }

        Assert.Equal(VpnDnsMode.Own, VpnDns.Read(" OWN "));
        Assert.Null(VpnDns.Read("panel"));
        Assert.Null(VpnDns.Read(null));
        Assert.Equal("default", VpnDns.NameOf((VpnDnsMode)7));
        Assert.Equal(["dns.example", "1.1.1", "010.0.0.1"], VpnDns.Odd("1.1.1.1, dns.example 1.1.1 ::1 010.0.0.1 2001:DB8::53"));
        Assert.Empty(VpnDns.Odd(" "));
        Assert.Equal(VpnDns.MaxServers, VpnDns.Servers(string.Join(' ', Enumerable.Range(1, 12).Select(n => $"10.0.0.{n}"))).Count);
        Assert.True(VpnDns.LacksV4("2001:db8::53, 2001:db8::54"));
        Assert.False(VpnDns.LacksV4("2001:db8::53 9.9.9.9"));
        Assert.False(VpnDns.LacksV4(" "));
        Assert.False(VpnDns.LacksV4(null));
        Assert.Equal("own 9.9.9.9", VpnDns.Describe(VpnDnsMode.Own, "9.9.9.9"));
        Assert.Equal("own", VpnDns.Describe(VpnDnsMode.Own, null));
        Assert.Equal("network", VpnDns.Describe(VpnDnsMode.Network, "9.9.9.9"));
    }

    [Fact]
    public void TheOffer_NamesThePickOfTheServerWithItsAddressesAlone()
    {
        var offer = ServerOffer.Parse(
            """{"server":"amneziageo","version":"1","client":"c","features":{"dns":{"mode":" Routing ","servers":["1.1.1.1"," 10.8.1.1 ","dns.example","1.1.1.1",7],"resolver":["10.8.1.1"]}}}""");
        var twelve = string.Join(',', Enumerable.Range(1, 12).Select(n => $"\"10.0.0.{n}\""));
        var many = ServerOffer.Parse(
            """{"server":"amneziageo","version":"1","client":"c","features":{"dns":{"mode":"own","servers":[""" + twelve + "]}}}");
        var none = ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":8446}}}""");

        var dns = Assert.IsType<OfferedDns>(offer.Dns());
        Assert.Equal("routing", dns.Mode);
        Assert.Equal(["1.1.1.1", "10.8.1.1"], dns.Servers);
        Assert.Equal(["10.8.1.1"], dns.Resolver);
        Assert.Equal(ServerOffer.MaxDnsServers, many.Dns()!.Servers.Count);
        Assert.Empty(many.Dns()!.Resolver);
        Assert.Null(none.Dns());
        Assert.Null(ServerOffer.None.Dns());
    }

    [Fact]
    public async Task TheChoice_IsKeptBesideTheDirectServersAndTheRecordGoesOnceItHoldsNothing()
    {
        await VpnDns.SaveAsync(_store, "a", "8.8.8.8", null, null, CancellationToken.None);
        await VpnDns.SaveAsync(_store, "a", null, VpnDnsMode.Own, " 9.9.9.9 ", CancellationToken.None);

        Assert.Equal(new ConfigDns("a", "8.8.8.8", VpnDnsMode.Own, "9.9.9.9"), await _store.GetConfigDnsAsync("a"));

        await VpnDns.SaveAsync(_store, "a", null, VpnDnsMode.Network, null, CancellationToken.None);

        Assert.Equal(new ConfigDns("a", "8.8.8.8", VpnDnsMode.Network, "9.9.9.9"), await _store.GetConfigDnsAsync("a"));

        await VpnDns.SaveAsync(_store, "a", string.Empty, VpnDnsMode.Own, null, CancellationToken.None);

        Assert.Equal(new ConfigDns("a", string.Empty, VpnDnsMode.Own, "9.9.9.9"), await _store.GetConfigDnsAsync("a"));

        await VpnDns.SaveAsync(_store, "a", null, VpnDnsMode.Default, string.Empty, CancellationToken.None);

        Assert.Null(await _store.GetConfigDnsAsync("a"));
    }

    [Fact]
    public async Task TheBundle_CarriesTheChoiceAndPutsItBack()
    {
        var plain = new PortableBundle.ConfigBlock("a", "[Interface]", null, null, "8.8.8.8");
        var own = plain.WithVpnDns(new ConfigDns("a", "8.8.8.8", VpnDnsMode.Own, "9.9.9.9"));
        var bundle = new PortableBundle.Bundle(PortableBundle.FormatTag, PortableBundle.CurrentVersion, [own, plain with { Name = "b" }], []);

        Assert.Same(plain, plain.WithVpnDns(null));
        Assert.Same(plain, plain.WithVpnDns(new ConfigDns("a", "8.8.8.8")));
        Assert.Equal(("own", "9.9.9.9"), (own.VpnDnsMode, own.VpnDns));

        var json = PortableBundle.Serialize(bundle);
        var back = PortableBundle.Deserialize(json)!;
        Assert.DoesNotContain("vpnDns", json.Split("\"b\"")[1], StringComparison.OrdinalIgnoreCase);
        foreach (var block in back.Configs)
        {
            await block.ApplyDnsAsync(_store, block.Name, CancellationToken.None);
        }

        Assert.Equal(new ConfigDns("a", "8.8.8.8", VpnDnsMode.Own, "9.9.9.9"), await _store.GetConfigDnsAsync("a"));
        Assert.Equal(new ConfigDns("b", "8.8.8.8"), await _store.GetConfigDnsAsync("b"));

        await new PortableBundle.ConfigBlock("c", "[Interface]", null, null).ApplyDnsAsync(_store, "c", CancellationToken.None);
        await new PortableBundle.ConfigBlock("a", "[Interface]", null, null, VpnDnsMode: "network").ApplyDnsAsync(_store, "a", CancellationToken.None);

        Assert.Null(await _store.GetConfigDnsAsync("c"));
        Assert.Equal(new ConfigDns("a", "8.8.8.8", VpnDnsMode.Network, "9.9.9.9"), await _store.GetConfigDnsAsync("a"));
    }

    private static ConfigDns Held(VpnDnsMode mode, string servers = "") => new("a", string.Empty, mode, servers);

    private static VpnDnsPick Pick(ConfigDns? held, OfferedDns? offer) => VpnDns.Pick(held, offer, Line);
}
