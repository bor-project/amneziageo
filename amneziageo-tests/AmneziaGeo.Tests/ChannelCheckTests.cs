using System.Net;

using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The check has one job: name the leg that is broken. A verdict that blames the wrong leg sends the user to the
/// wrong place, so every ladder here is one the support thread actually saw.
/// </summary>
public sealed class ChannelCheckTests
{
    [Fact]
    public void LossOnTheGateway_BlamesTheLocalNetwork()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Bad, RttMs: 3, LossPercent: 22),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, RttMs: 40, LossPercent: 0, MaxPacketBytes: 1472),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.LocalLoss, key);
        Assert.Equal(CheckLegs.Gateway, culprit);
        Assert.Equal("22", args[0]);
    }

    [Fact]
    public void LossPastACleanGateway_BlamesThePathToTheServer()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, RttMs: 2, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Bad, RttMs: 41, LossPercent: 20, MaxPacketBytes: 1472),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.PathLoss, key);
        Assert.Equal(CheckLegs.Endpoint, culprit);
    }

    [Fact]
    public void APathThatCutsPackets_NamesTheMtuToSet()
    {
        var (key, args, _) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1280),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.PathMtu, key);
        Assert.Equal("1280", args[0]);
        Assert.Equal("1248", args[1]);
    }

    [Fact]
    public void ASessionBeingReestablished_IsBlamedBeforeTheThroughput()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Bad, AgeSeconds: 15, RekeysPerMinute: 4),
                new CheckLeg(CheckLegs.Tunnel, LegState.Bad, BitsPerSecond: 140_000),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.Rekeying, key);
        Assert.Equal(CheckLegs.Handshake, culprit);
    }

    [Fact]
    public void LossPastACleanTunnel_BlamesWhatStandsBehindTheServer()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, RttMs: 42, LossPercent: 0),
                new CheckLeg(CheckLegs.Beyond, LegState.Weak, RttMs: 48, LossPercent: 9),
                new CheckLeg(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 39_000_000),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.BeyondLoss, key);
        Assert.Equal(CheckLegs.Beyond, culprit);
        Assert.Equal("9", args[0]);
        Assert.Contains("not the channel", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void LossOnBothSidesOfTheExit_BlamesTheNearerLeg()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Weak, LossPercent: 12),
                new CheckLeg(CheckLegs.Beyond, LegState.Weak, LossPercent: 9),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.ServerLoss, key);
        Assert.Equal(CheckLegs.Peer, culprit);
    }

    [Fact]
    public void ASlowTunnelBehindAHealthyServer_BlamesTheServer()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, RttMs: 40, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, RttMs: 42, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Bad, BitsPerSecond: 140_000),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.ServerSlow, key);
        Assert.Equal(CheckLegs.Tunnel, culprit);
        Assert.Equal("0.14", args[0]);
    }

    [Fact]
    public void ATunnelFarBehindTheSamePathBesideIt_BlamesTheTunnel()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 3_000_000),
                new CheckLeg(CheckLegs.Direct, LegState.Ok, BitsPerSecond: 78_000_000),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.TunnelBehindDirect, key);
        Assert.Equal(CheckLegs.Tunnel, culprit);
        Assert.Equal("3", args[0]);
        Assert.Equal("78", args[1]);
    }

    [Fact]
    public void ASourceCrawlingWhileTheTunnelRuns_BlamesTheSource()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 40_000_000),
                new CheckLeg(CheckLegs.Source, LegState.Bad, BitsPerSecond: 700_000, Note: "iptv.example"),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.SourceBehindTunnel, key);
        Assert.Equal(CheckLegs.Source, culprit);
        Assert.Equal("0.7", args[0]);
        Assert.Equal("40", args[1]);
        Assert.Contains("not the tunnel", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceKeepingUpWithTheTunnel_BlamesNobody()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 40_000_000),
                new CheckLeg(CheckLegs.Source, LegState.Ok, BitsPerSecond: 22_000_000, Note: "iptv.example"),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.Healthy, key);
        Assert.Equal(string.Empty, culprit);
    }

    [Fact]
    public void ASlowTunnelWithASlowSource_StillBlamesTheTunnel()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Bad, BitsPerSecond: 400_000),
                new CheckLeg(CheckLegs.Source, LegState.Bad, BitsPerSecond: 40_000, Note: "iptv.example"),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.ServerSlow, key);
        Assert.Equal(CheckLegs.Tunnel, culprit);
    }

    [Fact]
    public void AHealthyLadder_BlamesNobody()
    {
        var (key, _, culprit) = ChannelVerdict.Decide(
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1472),
                new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 30, RekeysPerMinute: 0),
                new CheckLeg(CheckLegs.Peer, LegState.Ok, LossPercent: 0),
                new CheckLeg(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 39_000_000),
            ],
            connected: true);

        Assert.Equal(CheckVerdicts.Healthy, key);
        Assert.Equal(string.Empty, culprit);
    }

    [Fact]
    public void ThePayload_SurvivesTheTrip()
    {
        var report = new CheckReport(
            1_700_000_000_000,
            "srv",
            [
                new CheckLeg(CheckLegs.Gateway, LegState.Ok, RttMs: 2, JitterMs: 1, LossPercent: 0, MaxPacketBytes: 1472, Note: "192.168.1.1"),
                new CheckLeg(CheckLegs.Tunnel, LegState.Bad, BitsPerSecond: 140_000),
            ],
            CheckVerdicts.ServerSlow,
            ["0.14"],
            CheckLegs.Tunnel);

        var back = CheckReport.Parse(report.ToPayload());

        Assert.Equal(2, back.Legs.Count);
        Assert.Equal(1472, back.Legs[0].MaxPacketBytes);
        Assert.Equal("192.168.1.1", back.Legs[0].Note);
        Assert.Equal(140_000, back.Legs[1].BitsPerSecond);
        Assert.Equal(CheckVerdicts.ServerSlow, back.VerdictKey);
        Assert.Equal(CheckLegs.Tunnel, back.Culprit);
        Assert.Equal("0.14", back.VerdictArgs[0]);
    }

    [Fact]
    public void TheRenderedRun_CarriesTheVerdictInWords()
    {
        var report = new CheckReport(1_700_000_000_000, "srv",
            [new CheckLeg(CheckLegs.Endpoint, LegState.Bad, LossPercent: 20)],
            CheckVerdicts.PathLoss, ["20"], CheckLegs.Endpoint);

        var text = report.Render();

        Assert.Contains("endpoint", text, StringComparison.Ordinal);
        Assert.Contains("loss 20%", text, StringComparison.Ordinal);
        Assert.Contains("the path to the server loses 20%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMtuTheMeasuredPathCarries_EarnsNoAdvice()
    {
        Assert.Null(MtuAdvice.For(1472, 1420));
        Assert.Null(MtuAdvice.For(1472, 0));
        Assert.Null(MtuAdvice.For(0, 1420));
    }

    [Fact]
    public void AnMtuTooLargeForTheMeasuredPath_NamesTheOneToSet()
    {
        var advice = MtuAdvice.For(1280, 1420);

        Assert.NotNull(advice);
        Assert.Equal(1308, advice.PathMtu);
        Assert.Equal(1420, advice.ConfiguredMtu);
        Assert.Equal(1248, advice.PreferredMtu);
        Assert.Contains("set it to 1248", advice.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvice_SurvivesTheTripAndTheRendering()
    {
        var report = new CheckReport(
            1_700_000_000_000,
            "srv",
            [new CheckLeg(CheckLegs.Endpoint, LegState.Ok, LossPercent: 0, MaxPacketBytes: 1360)],
            CheckVerdicts.Healthy,
            ["39"],
            string.Empty,
            MtuAdvice.For(1360, 1420));

        var back = CheckReport.Parse(report.ToPayload());

        Assert.NotNull(back.Advice);
        Assert.Equal(1360, back.Advice.PayloadBytes);
        Assert.Equal(1328, back.Advice.PreferredMtu);
        Assert.Contains("set it to 1328", report.Render(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The targeted check: why one destination goes where it goes. The application case is the one the support
/// thread keeps hitting - a rule that names the app while the app talks to addresses no rule covers.
/// </summary>
public sealed class TargetCheckTests
{
    private static RoutingList List(
        IReadOnlyList<GeoRule>? rules = null,
        IReadOnlyList<string>? proxy = null,
        IReadOnlyList<GeoDomain>? domains = null,
        IReadOnlyList<string>? block = null,
        IReadOnlyList<GeoDomain>? blockDomains = null)
    {
        return new RoutingList(1, "main", rules ?? [], proxy ?? [], domains ?? [], [], [], [], block ?? [], blockDomains ?? []);
    }

    [Fact]
    public void ANameInTheBlockBucket_IsNamedByItsEntry()
    {
        var inspector = new TargetInspector(List(blockDomains: [new GeoDomain(GeoDomainKind.Domain, "ads.example")]), split: true);

        var claim = inspector.ForDomain("track.ads.example");

        Assert.Equal(RoleToken.Block, claim.Role);
        Assert.Equal("ads.example", claim.Rule);
    }

    [Fact]
    public void AnAddressInTheProxyBucket_NamesTheRangeThatCoversIt()
    {
        var inspector = new TargetInspector(List(proxy: ["149.154.160.0/20"]), split: true);

        var claim = inspector.ForAddress(IPAddress.Parse("149.154.167.51"));

        Assert.Equal(RoleToken.Proxy, claim.Role);
        Assert.Equal("149.154.160.0-149.154.175.255", claim.Rule);
    }

    [Fact]
    public async Task AnAppTalkingToBareAddresses_IsToldToAddAGeoRule()
    {
        var list = List(rules: [new GeoRule(GeoRuleKind.App, "pkg=org.telegram.messenger")]);
        var probes = new TargetProbes(AppAddresses: _ => ["149.154.167.51", "91.108.56.130"]);

        var report = await new TargetInspector(list, split: true)
            .InspectAsync("app:pkg=org.telegram.messenger", "srv", probes, CancellationToken.None);

        Assert.Equal(TargetVerdicts.AppBareIp, report.VerdictKey);
        Assert.Equal("2", report.VerdictArgs[1]);
        Assert.Contains(report.Facts, fact => fact.Kind == "app" && fact.State == "listed");
        Assert.Contains("an app rule is reactive", TargetPhrase.English(report.VerdictKey, report.VerdictArgs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressNoRuleCoversInSplit_LeavesThroughThePhysicalPath()
    {
        var report = await new TargetInspector(List(proxy: ["149.154.160.0/20"]), split: true)
            .InspectAsync("8.8.8.8", "srv", new TargetProbes(), CancellationToken.None);

        Assert.Equal(TargetVerdicts.UnlistedSplit, report.VerdictKey);
    }

    [Fact]
    public async Task WithNoListInForce_NothingIsDecidedPerDestination()
    {
        var report = await new TargetInspector(null, split: false)
            .InspectAsync("8.8.8.8", "srv", new TargetProbes(), CancellationToken.None);

        Assert.Equal(TargetVerdicts.NoRules, report.VerdictKey);
    }

    [Fact]
    public async Task ABlockedAddress_SaysWhichRuleDropsIt()
    {
        var report = await new TargetInspector(List(block: ["10.0.0.0/8"]), split: true)
            .InspectAsync("10.1.2.3", "srv", new TargetProbes(), CancellationToken.None);

        Assert.Equal(TargetVerdicts.Blocked, report.VerdictKey);
        Assert.Contains("10.0.0.0-10.255.255.255", report.VerdictArgs[0], StringComparison.Ordinal);
    }
}
