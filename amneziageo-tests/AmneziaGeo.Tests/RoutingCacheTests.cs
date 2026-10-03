using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The cache is what replaces pre-installed routes: it decides per destination, so its precedence must match the
/// eager path, and it must install a bypass only where the default route would take the address the wrong way.
/// </summary>
public sealed class RoutingCacheTests
{
    private const string YandexRange = "77.88.32.0/19";
    private const string YandexAddress = "77.88.55.242";

    private sealed class FakeApplier : IRouteApplier
    {
        public int Generation { get; set; }

        public List<uint> Permitted { get; } = [];

        public List<uint> PermittedStreams { get; } = [];

        public List<uint> Dropped { get; } = [];

        public List<string> Added { get; } = [];

        public List<string> Removed { get; } = [];

        public List<(ulong Out, ulong In)> Deleted { get; } = [];

        public List<string> Tunneled { get; } = [];

        public List<string> Untunneled { get; } = [];

        public int DeleteCalls { get; private set; }

        public int UntunnelCalls { get; private set; }

        public int TunnelBatches { get; private set; }

        public bool RouteFails { get; set; }

        public bool TunnelFails { get; set; }

        public bool FiltersOutliveRearm { get; set; }

        private ulong _nextId = 1;

        public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
        {
            Permitted.Add(address);
            outId = _nextId++;
            inId = _nextId++;
            generation = Generation;
            return true;
        }

        public bool TryPermitStreams(uint address, out ulong outId, out ulong inId, out int generation)
        {
            PermittedStreams.Add(address);
            outId = _nextId++;
            inId = _nextId++;
            generation = Generation;
            return true;
        }

        public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
        {
            Dropped.Add(address);
            outId = _nextId++;
            inId = _nextId++;
            generation = Generation;
            return true;
        }

        public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
        {
            interfaceIndex = 7;
            if (RouteFails)
            {
                return false;
            }

            Added.Add(address.ToString());
            return true;
        }

        public void RemoveRoute(IPAddress address, uint interfaceIndex)
        {
            Removed.Add(address.ToString());
        }

        public bool TryTunnel(IPAddress address)
        {
            if (TunnelFails)
            {
                return false;
            }

            Tunneled.Add(address.ToString());
            return true;
        }

        public IReadOnlyList<IPAddress> AddTunnel(IReadOnlyList<IPAddress> addresses)
        {
            TunnelBatches++;
            if (TunnelFails)
            {
                return [];
            }

            foreach (var address in addresses)
            {
                Tunneled.Add(address.ToString());
            }

            return addresses;
        }

        public void RemoveTunnel(IReadOnlyCollection<IPAddress> addresses)
        {
            if (addresses.Count == 0)
            {
                return;
            }

            UntunnelCalls++;
            foreach (var address in addresses)
            {
                Untunneled.Add(address.ToString());
            }
        }

        public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
        {
            DeleteCalls++;
            Deleted.AddRange(filters);
        }
    }

    // The tests drive Sweep directly, so nothing is ever live.
    private sealed class IdleLive : ILiveDestinations
    {
        public LiveDestinations Snapshot() => new([], []);
    }

    // Hot defaults to none here: the idle window is what most of these tests are about, and the entries a real
    // cache keeps whatever it says are exercised on their own.
    private static RoutingCache Cache(FakeApplier applier, bool split, IReadOnlyList<string>? proxy = null, IReadOnlyList<string>? direct = null, IReadOnlyList<string>? block = null, int ttlSeconds = 300, IReadOnlyCollection<string>? pinned = null, int hot = 0, bool directStanding = false)
    {
        return new RoutingCache(applier, new IdleLive(), split, proxy ?? [], direct ?? [], block ?? [], ttlSeconds, NullLogger<RoutingCache>.Instance, pinned, hot: hot, directStanding: directStanding);
    }

    private static uint Numeric(string address)
    {
        Assert.True(GeoIpRanges.TryToNumeric(IPAddress.Parse(address), out var value));
        return value;
    }

    // Runs the pump over what the firewall reported until the condition holds, then stops it.
    private static async Task PumpAsync(RoutingCache cache, Func<bool> done)
    {
        using var stop = new CancellationTokenSource();
        var pump = cache.PumpAsync(stop.Token);
        for (var wait = 0; wait < 300 && !done(); wait++)
        {
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await pump;
    }

    [Fact]
    public async Task WhereEveryDatagramRidesTheTunnel_ADroppedDatagramToAnUnlistedAddress_IsRoutedThereAndEarnsNoPermit()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var asked = new List<string>();
        cache.SetDatagramRule(address =>
        {
            asked.Add(address.ToString());
            return true;
        });

        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => asked.Count > 0 || applier.Permitted.Count > 0);

        Assert.Equal(new[] { "208.67.222.222" }, asked);
        Assert.Empty(applier.Permitted);
        Assert.Empty(applier.PermittedStreams);
    }

    [Fact]
    public async Task ADatagramTheRuleLeavesAlone_IsPermittedWhole()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.SetDatagramRule(_ => false);

        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => applier.Permitted.Count > 0 || applier.PermittedStreams.Count > 0);

        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.Permitted);
        Assert.Empty(applier.PermittedStreams);
    }

    [Fact]
    public void WhereEveryDatagramRidesTheTunnel_AnUnlistedAddressIsPermittedWithoutDatagrams()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.SetDatagramRule(_ => true);

        cache.Note(IPAddress.Parse("208.67.222.222"));

        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.PermittedStreams);
        Assert.Empty(applier.Permitted);
    }

    [Fact]
    public async Task ADatagramToAnAddressPermittedWithoutThem_IsStillHandedToTheRule()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var asked = 0;
        cache.SetDatagramRule(_ =>
        {
            asked++;
            return true;
        });
        cache.Note(IPAddress.Parse("208.67.222.222"));

        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => asked > 0 || applier.Permitted.Count > 0);

        Assert.Equal(1, asked);
        Assert.Empty(applier.Permitted);
    }

    [Fact]
    public async Task AnAddressTheRuleLeavesAloneLater_TradesItsPermitForAWholeOne()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.SetDatagramRule(_ => false);
        cache.Note(IPAddress.Parse("208.67.222.222"));
        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.PermittedStreams);

        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => applier.Permitted.Count > 0);

        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.Permitted);
        Assert.Single(applier.Deleted);
    }

    [Fact]
    public async Task AnAddressAListKeepsDirect_IsPermittedWholeAndNeverHandedToTheRule()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, direct: [YandexRange]);
        var asked = 0;
        cache.SetDatagramRule(_ =>
        {
            asked++;
            return true;
        });

        cache.Report(IPAddress.Parse(YandexAddress), null, datagram: true);
        await PumpAsync(cache, () => applier.Permitted.Count > 0 || asked > 0);

        Assert.Equal(0, asked);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Empty(applier.PermittedStreams);
    }

    [Fact]
    public async Task APacketThatIsNoDatagram_IsDecidedByTheListsAlone()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var asked = 0;
        cache.SetDatagramRule(_ =>
        {
            asked++;
            return true;
        });

        cache.Report(IPAddress.Parse("208.67.222.222"), null);
        await PumpAsync(cache, () => applier.PermittedStreams.Count > 0 || applier.Permitted.Count > 0);

        Assert.Equal(0, asked);
        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.PermittedStreams);
    }

    [Fact]
    public async Task WithoutTheRule_ADroppedDatagramEarnsAWholePermit()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);

        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => applier.Permitted.Count > 0 || applier.PermittedStreams.Count > 0);

        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.Permitted);
        Assert.Empty(applier.PermittedStreams);
    }

    [Fact]
    public void AddressInDirectSet_EarnsABypassRouteAndPermit()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
        Assert.Equal(new[] { YandexAddress }, applier.Added);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Equal(1, cache.Active);
    }

    [Fact]
    public void AddressOutsideEverySet_IsUnlistedAndInstallsNothing()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);

        cache.Note(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(RouteVerdict.None, cache.Classify(IPAddress.Parse("8.8.8.8")));
        Assert.Empty(applier.Added);
        Assert.Equal(0, cache.Active);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void ProxyAddress_KeepsItsVerdictButInstallsNothing()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, proxy: ["8.8.8.0/24"]);

        cache.Note(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(RouteVerdict.Proxy, cache.Classify(IPAddress.Parse("8.8.8.8")));
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void BlockWinsOverDirect_SoABlockedAddressNeverEarnsABypass()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: ["10.0.0.0/8"], block: ["10.1.2.0/24"]);

        cache.Note(IPAddress.Parse("10.1.2.3"));

        Assert.Equal(RouteVerdict.Block, cache.Classify(IPAddress.Parse("10.1.2.3")));
        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse("10.1.3.3")));
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void DirectWinsOverProxy_SoAnOverlapCannotInstallCompetingRoutes()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, proxy: ["77.88.0.0/16"], direct: [YandexRange]);

        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void Ipv6_IsUnlisted()
    {
        var applier = new FakeApplier();
        var cache = Cache(applier, split: false, direct: ["10.0.0.0/8"]);

        cache.Note(IPAddress.Parse("2a02:6b8::2:242"));

        Assert.Equal(RouteVerdict.None, cache.Classify(IPAddress.Parse("2a02:6b8::2:242")));
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void InSplit_DirectAddress_EarnsAPermitAndStaysOffTheTunnel()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Empty(applier.Added);
        Assert.Empty(applier.Tunneled);
    }

    [Fact]
    public void InSplit_ProxyAddress_EarnsATunnelRouteInsteadOfAPermit()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(RouteVerdict.Proxy, cache.Classify(IPAddress.Parse(YandexAddress)));
        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Empty(applier.Permitted);
        Assert.Equal(1, cache.Active);
    }

    [Fact]
    public void InSplit_DirectWinsOverProxy_SoAnOverlapNeverRidesTheTunnel()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"], direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Empty(applier.Tunneled);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
    }

    [Fact]
    public void InSplit_UnlistedAddress_EarnsAPermitSoItLeavesTheBlockedPhysicalPath()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);

        cache.Note(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(RouteVerdict.None, cache.Classify(IPAddress.Parse("8.8.8.8")));
        Assert.Equal(new[] { Numeric("8.8.8.8") }, applier.Permitted);
        Assert.Empty(applier.Tunneled);
    }

    [Fact]
    public void InSplit_BlockedAddress_EarnsADropInsteadOfAPermitOrTunnel()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"], block: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(RouteVerdict.Block, cache.Classify(IPAddress.Parse(YandexAddress)));
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Dropped);
        Assert.Empty(applier.Permitted);
        Assert.Empty(applier.Tunneled);
    }

    [Fact]
    public void InFullTunnel_BlockedAddress_EarnsADropOnContact()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Dropped);
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void IdleBlockedAddress_ReleasesItsFilter()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Single(applier.Deleted);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void InSplit_AppOwnedDestination_TakesTheTunnelThoughNoRangeCoversIt()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Empty(applier.Permitted);
    }

    [Fact]
    public void InSplit_AppClaimsAnAlreadyPermittedDestination_ItsPermitIsWithdrawn()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Single(applier.Deleted);
        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
    }

    [Fact]
    public void InSplit_AppOwnedDestinationInADirectRange_StaysOffTheTunnel()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, direct: [YandexRange]);

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Empty(applier.Tunneled);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
    }

    [Fact]
    public void InSplit_AppOwnedDestinationInABlockRange_IsStillDropped()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, block: [YandexRange]);

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Empty(applier.Tunneled);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Dropped);
    }

    [Fact]
    public void InSplit_AppOwnedDestinationOutsideEveryRange_IsReportedForRemembering()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var learned = new List<string>();
        cache.AppDestination += address => learned.Add(address.ToString());

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Equal(new[] { YandexAddress }, learned);
    }

    [Fact]
    public void InSplit_AppOwnedDestinationAProxyRangeCovers_IsNotReported()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: [YandexRange]);
        var learned = new List<string>();
        cache.AppDestination += address => learned.Add(address.ToString());

        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Empty(learned);
    }

    [Fact]
    public void InSplit_PermittedDestinationLaterClaimedByAnApp_IsReportedOnce()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var learned = new List<string>();
        cache.AppDestination += address => learned.Add(address.ToString());
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Note(Numeric(YandexAddress), app: true);
        cache.Note(Numeric(YandexAddress), app: true);

        Assert.Equal(new[] { YandexAddress }, learned);
    }

    [Fact]
    public void ConfiguredTtl_HoldsAnEntryForItsOwnWindow()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange], ttlSeconds: 600);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + (7 * 60 * 1000));

        Assert.Empty(applier.Deleted);
        Assert.Equal(1, cache.Size);

        cache.Sweep([], Environment.TickCount64 + (11 * 60 * 1000));

        Assert.Single(applier.Deleted);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void ShortTtl_IsHonouredAsEntered()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange], ttlSeconds: 5);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + 6_000);

        Assert.Equal(5, cache.TtlSeconds);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void ZeroTtl_HoldsNothingPastTheFirstSweep()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange], ttlSeconds: 0);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + 1);

        Assert.Equal(0, cache.TtlSeconds);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void InSplit_IdleTunnelledAddress_IsWithdrawnFromThePeer()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(new[] { YandexAddress }, applier.Untunneled);
        Assert.Equal(0, cache.Active);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void InSplit_IdleAppOwnedDestination_KeepsItsRoute()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(Numeric(YandexAddress), app: true);

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Empty(applier.Untunneled);
        Assert.Equal(1, cache.Active);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void InSplit_ManyIdleTunnelledAddresses_AreWithdrawnInOneCall()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);
        cache.Note(IPAddress.Parse("77.88.55.1"));
        cache.Note(IPAddress.Parse("77.88.55.2"));
        cache.Note(IPAddress.Parse("77.88.55.3"));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(3, applier.Untunneled.Count);
        Assert.Equal(1, applier.UntunnelCalls);
    }

    [Fact]
    public void InSplit_FailedTunnelInstall_LeavesTheEntryUnapplied()
    {
        var applier = new FakeApplier { Generation = 1, TunnelFails = true };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(0, cache.Active);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void AdoptedAddress_StillHeldByItsDomain_IsNeitherInstalledNorReclaimed()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);
        cache.SetAdoptionCheck(_ => true);

        cache.Adopt([IPAddress.Parse(YandexAddress)]);
        cache.Note(IPAddress.Parse(YandexAddress));
        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Empty(applier.Tunneled);
        Assert.Empty(applier.Untunneled);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void AdoptedAddress_WhoseDomainIsGone_IsReclaimedLikeAnyOther()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);
        cache.SetAdoptionCheck(_ => false);

        cache.Adopt([IPAddress.Parse(YandexAddress)]);
        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void ForgottenAddress_LeavesTheCacheToBeDecidedAgain()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["77.88.0.0/16"]);
        cache.Adopt([IPAddress.Parse(YandexAddress)]);

        cache.Forget([IPAddress.Parse(YandexAddress)]);
        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
    }

    [Fact]
    public void ShortenedTtl_AppliesToWhatIsAlreadyHeld()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], ttlSeconds: 3600);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.SetTtl(30);
        cache.Sweep([], Environment.TickCount64 + (31 * 1000));

        Assert.Equal(30, cache.TtlSeconds);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void LengthenedTtl_KeepsWhatTheOldWindowWouldHaveDropped()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], ttlSeconds: 30);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.SetTtl(3600);
        cache.Sweep([], Environment.TickCount64 + (31 * 1000));

        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void RepeatContact_InstallsNothingTwice()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));
        cache.Note(IPAddress.Parse(YandexAddress));
        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Single(applier.Added);
        Assert.Single(applier.Permitted);
    }

    [Fact]
    public void RearmedFilterSet_ReinstallsPermitsWithoutTouchingRoutes()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        applier.Generation = 2;
        cache.Reinstall();

        Assert.Equal(2, applier.Permitted.Count);
        Assert.Single(applier.Added);
    }

    [Fact]
    public void IdleEntry_IsReclaimed()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(new[] { YandexAddress }, applier.Removed);
        Assert.Single(applier.Deleted);
        Assert.Equal(1, applier.DeleteCalls);
        Assert.Equal(0, cache.Active);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void IdleEntryStillCarryingTraffic_KeepsItsRoute()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        var address = Numeric(YandexAddress);
        cache.Note(address);

        cache.Sweep([address], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Empty(applier.Removed);
        Assert.Equal(1, cache.Active);
    }

    [Fact]
    public void Sweep_ReclaimsInSlices()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: ["9.0.0.0/8"]);
        for (var i = 0u; i < 100; i++)
        {
            cache.Note(0x09000000u + i);
        }

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(64, applier.Removed.Count);
        Assert.Equal(36, cache.Active);
    }

    [Fact]
    public void PastTheResourceCeiling_AddressesFollowTheDefaultRoute()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: ["9.0.0.0/8"]);

        for (var i = 0u; i < 8300; i++)
        {
            cache.Note(0x09000000u + i);
        }

        Assert.Equal(8192, cache.Active);
        Assert.Equal(8192, applier.Added.Count);
    }

    [Fact]
    public void RebuiltRules_MoveAHeldAddressToTheSideTheNewOnesAskFor()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Rebuild([], [], [YandexRange]);

        Assert.Equal(new[] { YandexAddress }, applier.Removed);
        Assert.Equal(1, cache.Size);
        Assert.Equal(RouteVerdict.Block, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void RebuiltRules_LeaveAnAddressTheNewOnesStillAgreeAbout()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Rebuild([], [YandexRange], []);

        Assert.Empty(applier.Removed);
        Assert.Equal(1, cache.Active);
        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void AnAddressSettledByAName_IsNotTakenBackByARange()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Direct);

        cache.Note(Numeric(YandexAddress));

        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void RemoveAll_DropsEveryRouteAndFilter()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: ["9.0.0.0/8"]);
        for (var i = 0u; i < 10; i++)
        {
            cache.Note(0x09000000u + i);
        }

        cache.RemoveAll();

        Assert.Equal(10, applier.Removed.Count);
        Assert.Equal(10, applier.Deleted.Count);
        Assert.Equal(0, cache.Active);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void FailedRouteInstall_LeavesTheEntryUnapplied()
    {
        var applier = new FakeApplier { Generation = 1, RouteFails = true };
        var cache = Cache(applier, split: false, direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress));

        Assert.Equal(0, cache.Active);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void EmptyRules_MatchNothing()
    {
        var applier = new FakeApplier();
        var cache = Cache(applier, split: false);

        Assert.False(cache.HasRules);
        Assert.Equal(RouteVerdict.None, cache.Classify(IPAddress.Parse("1.2.3.4")));
    }

    // The tunnel resolver: its route is installed with the connection, and the queries that keep it alive belong
    // to the agent itself, which is attributed to no process - so an unpinned resolver is reclaimed as idle and
    // the tunnel's own name lookups stop dead until some other traffic happens to restore it.
    [Fact]
    public void PinnedResolverCoveredByAProxyRange_IsNeverTakenIntoTheCache()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["1.1.1.0/24"], pinned: ["1.1.1.1/32"]);

        cache.Note(IPAddress.Parse("1.1.1.1"));

        Assert.Empty(applier.Tunneled);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void PinnedResolver_KeepsItsRouteThroughASweep()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["1.1.1.0/24"], pinned: ["1.1.1.1"]);
        cache.Note(IPAddress.Parse("1.1.1.1"));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Empty(applier.Untunneled);
        Assert.Empty(applier.Removed);
    }

    // Access from the tunnel pins a whole network, and a Direct rule covering it must not pull the answers onto
    // the physical path.
    [Fact]
    public void PinnedNetwork_HoldsEveryAddressInIt()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, direct: ["10.0.0.0/8"], pinned: ["10.8.2.0/24"]);

        cache.Note(IPAddress.Parse("10.8.2.1"));
        cache.Note(IPAddress.Parse("10.8.2.200"));

        Assert.Empty(applier.Permitted);
        Assert.Equal(0, cache.Size);
    }

    // A list edit changes the way back for access from the tunnel, so the pinned set follows it on the running cache.
    [Fact]
    public void Pin_ReleasesWhatTheNewRangesCoverAndDecidesWhatLeftThem()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["10.80.0.0/16"], pinned: ["1.1.1.1"]);
        cache.Note(IPAddress.Parse("10.80.1.1"));

        cache.Pin(["1.1.1.1", "10.80.0.0/16"]);
        cache.Note(IPAddress.Parse("10.80.1.2"));

        Assert.Equal(new[] { "10.80.1.1" }, applier.Untunneled);
        Assert.Equal(0, cache.Size);
        Assert.Equal(new[] { "1.1.1.1", "10.80.0.0/16" }, cache.PinnedRoutes);

        cache.Pin(["1.1.1.1"]);
        cache.Note(IPAddress.Parse("10.80.1.2"));

        Assert.Equal(new[] { "10.80.1.1", "10.80.1.2" }, applier.Tunneled);
        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void UnpinnedResolver_IsStillDecidedByTheRanges()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["1.1.1.0/24"], pinned: ["9.9.9.9"]);

        cache.Note(IPAddress.Parse("1.1.1.1"));
        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Equal(new[] { "1.1.1.1" }, applier.Tunneled);
        Assert.Equal(new[] { "1.1.1.1" }, applier.Untunneled);
    }

    [Fact]
    public void PinnedResolver_IsNotAdoptedFromTheDomainTracker()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["1.1.1.0/24"], pinned: ["1.1.1.1"]);

        cache.Adopt([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.1.1.2")]);

        Assert.Equal(1, cache.Size);
    }

    [Fact]
    public void WithinTheHotCount_AnIdleEntryIsKept()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 2);
        cache.Note(Numeric(YandexAddress));
        cache.Note(Numeric("77.88.55.243"));

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        Assert.Empty(applier.Removed);
        Assert.Equal(2, cache.Size);
        Assert.Equal(2, cache.Active);
    }

    [Fact]
    public void PastTheHotCount_TheLeastRecentlyUsedGoesOnTheIdleWindow()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 1);
        var now = DateTimeOffset.UtcNow;
        cache.Restore([
            new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, now),
            new RememberedRoute("77.88.55.243", nameof(RouteVerdict.Direct), false, false, now.AddHours(-1)),
        ]);

        cache.Sweep([], Environment.TickCount64 + (16 * 60 * 1000));

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(YandexAddress, held.Address.ToString());
    }

    [Fact]
    public void RestoredEntry_KeepsTheVerdictANameSettled()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, hot: 8);

        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Proxy), true, false, DateTimeOffset.UtcNow)]);

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(RouteVerdict.Proxy, held.Verdict);
        Assert.True(held.ByName);
    }

    [Fact]
    public void RestoredAddress_TakesItsVerdictUnderTheListInForce()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, block: [YandexRange], hot: 8);

        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, DateTimeOffset.UtcNow)]);

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(RouteVerdict.Block, held.Verdict);
    }

    [Fact]
    public void Restore_TakesNoMoreThanTheHotCount()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 2);
        var now = DateTimeOffset.UtcNow;

        cache.Restore([
            new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, now),
            new RememberedRoute("77.88.55.243", nameof(RouteVerdict.Direct), false, false, now),
            new RememberedRoute("77.88.55.244", nameof(RouteVerdict.Direct), false, false, now),
        ]);

        Assert.Equal(2, cache.Size);
    }

    [Fact]
    public void Hot_CarriesOnlyTheEntriesTheNextSessionStartsFrom()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 1);
        cache.Note(Numeric(YandexAddress));
        cache.Note(Numeric("77.88.55.243"));

        Assert.Single(cache.Hot());
    }

    [Fact]
    public void ANewCache_TakesBackWhatThePreviousOneUsed()
    {
        var first = Cache(new FakeApplier { Generation = 1 }, split: false, direct: [YandexRange], hot: 8);
        first.Note(Numeric(YandexAddress));

        var applier = new FakeApplier { Generation = 1 };
        var second = Cache(applier, split: false, direct: [YandexRange], hot: 8);
        second.Restore(first.Hot());

        Assert.Equal(1, second.Size);
        Assert.Empty(applier.Added);

        second.Note(Numeric(YandexAddress));

        Assert.Equal(new[] { YandexAddress }, applier.Added);
    }

    [Fact]
    public async Task Warming_PutsARestoredAddressOnItsRouteWithoutWaitingForAPacket()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 8);
        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, DateTimeOffset.UtcNow)]);

        await cache.WarmAsync(CancellationToken.None);

        Assert.Equal(new[] { YandexAddress }, applier.Added);
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Equal(1, cache.Active);
    }

    [Fact]
    public async Task Warming_TakesTheRestoredTunnelAddressesInOneBatch()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: [YandexRange], hot: 8);
        var now = DateTimeOffset.UtcNow;
        cache.Restore([
            new RememberedRoute(YandexAddress, nameof(RouteVerdict.Proxy), false, false, now),
            new RememberedRoute("77.88.55.243", nameof(RouteVerdict.Proxy), false, false, now),
        ]);

        await cache.WarmAsync(CancellationToken.None);

        Assert.Equal(2, applier.Tunneled.Count);
        Assert.Equal(1, applier.TunnelBatches);
        Assert.Equal(2, cache.Active);
    }

    [Fact]
    public async Task Warming_KeepsTheAgeEachDestinationCameBackWith()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange, "8.8.8.0/24"], ttlSeconds: 300, hot: 1);
        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, DateTimeOffset.UtcNow.AddHours(-1))]);

        await cache.WarmAsync(CancellationToken.None);
        cache.Note(Numeric("8.8.8.8"));
        cache.Sweep([], Environment.TickCount64);

        Assert.Contains(YandexAddress, applier.Added);
        Assert.Equal(new[] { YandexAddress }, applier.Removed);
    }

    [Fact]
    public async Task Warming_InstallsNothingForADestinationNoRangeNamesAnyMore()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, hot: 8);
        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Direct), false, false, DateTimeOffset.UtcNow)]);

        await cache.WarmAsync(CancellationToken.None);

        Assert.Empty(applier.Added);
        Assert.Equal(0, cache.Active);
    }

    [Fact]
    public void ARebuild_MovesAHeldAddressToTheSideItsNameNowAsksFor()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        cache.Rebuild([], [], [], _ => RouteVerdict.Direct);

        Assert.Equal(new[] { YandexAddress }, applier.Untunneled);
        Assert.Equal(RouteVerdict.Direct, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void ARebuild_KeepsInTheTunnelAnAddressItsNameStillSendsThere()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        cache.Rebuild([], [], [], _ => RouteVerdict.Proxy);

        Assert.Empty(applier.Untunneled);
        Assert.Equal(RouteVerdict.Proxy, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void ARebuild_PutsBackThePermitOfAnAddressItMoves()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Direct);

        cache.Rebuild([], [], []);

        Assert.NotEmpty(applier.Deleted);
        Assert.Equal(2, applier.Permitted.Count);
    }

    [Fact]
    public void ARebuild_DropsAnAddressItMovesToBlock()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Direct);

        cache.Rebuild([], [], [YandexRange]);

        Assert.Equal(RouteVerdict.Block, cache.Classify(IPAddress.Parse(YandexAddress)));
        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Dropped);
    }

    [Fact]
    public void WhereTheDirectRangesStand_AnAddressANameTunnelsPastOne_TakesARouteOfItsOwn()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], directStanding: true);

        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Empty(applier.Added);
        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(RoutePlan.Tunnel, held.Plan);
        Assert.True(held.Routed);
    }

    [Fact]
    public void WhereTheDirectRangesStand_AnAddressANameTunnelsOutsideThem_InstallsNothing()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], directStanding: true);

        cache.Note(IPAddress.Parse("8.8.8.8"), RouteVerdict.Proxy);

        Assert.Empty(applier.Tunneled);
        Assert.Equal(0, cache.Active);
    }

    [Fact]
    public void WhereNoDirectRangeStands_AnAddressANameTunnelsPastOne_InstallsNothing()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);

        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        Assert.Empty(applier.Tunneled);
        Assert.Equal(0, cache.Active);
    }

    [Fact]
    public void WhereTheDirectRangesStand_AnAddressOfOneThatANameThenTunnels_LeavesItsBypassForTheTunnel()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], directStanding: true);
        cache.Note(Numeric(YandexAddress));

        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        Assert.Equal(new[] { YandexAddress }, applier.Removed);
        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
    }

    [Fact]
    public void ARebuild_RoutesIntoTheTunnelAnAddressANameSendsThereOnceADirectRangeCoversIt()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, directStanding: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);
        Assert.Empty(applier.Tunneled);

        cache.Rebuild([], [YandexRange], [], _ => RouteVerdict.Proxy);

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Equal(RouteVerdict.Proxy, cache.Classify(IPAddress.Parse(YandexAddress)));
    }

    [Fact]
    public void ARebuild_TakesTheRouteBackOnceTheDirectRangeIsGone()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], directStanding: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        cache.Rebuild([], [], [], _ => RouteVerdict.Proxy);

        Assert.Equal(new[] { YandexAddress }, applier.Untunneled);
        Assert.Equal(0, cache.Active);
    }

    [Fact]
    public void ARebuild_LeavesTheRouteOfAnAddressTheDirectRangeStillCovers()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], directStanding: true);
        cache.Note(IPAddress.Parse(YandexAddress), RouteVerdict.Proxy);

        cache.Rebuild([], [YandexRange, "8.8.8.0/24"], [], _ => RouteVerdict.Proxy);

        Assert.Empty(applier.Untunneled);
        Assert.Single(applier.Tunneled);
    }

    [Fact]
    public void InSplit_WhereTheDirectRangesAreSaidToStand_NothingChanges()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, direct: [YandexRange], directStanding: true);

        cache.Note(Numeric(YandexAddress));
        cache.Rebuild([], [YandexRange], []);

        Assert.Equal(new[] { Numeric(YandexAddress) }, applier.Permitted);
        Assert.Empty(applier.Tunneled);
        Assert.Empty(applier.Deleted);
    }

    [Fact]
    public async Task Warming_RoutesIntoTheTunnelARestoredAddressANameSendsPastADirectRange()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange], hot: 8, directStanding: true);
        cache.Restore([new RememberedRoute(YandexAddress, nameof(RouteVerdict.Proxy), true, false, DateTimeOffset.UtcNow)]);

        await cache.WarmAsync(CancellationToken.None);

        Assert.Equal(new[] { YandexAddress }, applier.Tunneled);
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void AForcedBypass_OfAnAddressNothingHeld_IsTakenBackWhole()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, hot: 8);

        var forced = cache.Force(IPAddress.Parse("8.8.8.8"), RouteVerdict.Direct);
        Assert.NotNull(forced);
        Assert.Equal(new[] { "8.8.8.8" }, applier.Added);

        cache.Unforce(forced);

        Assert.Equal(new[] { "8.8.8.8" }, applier.Removed);
        Assert.Empty(cache.Snapshot());
        Assert.Empty(cache.Hot());
        Assert.Equal(RouteVerdict.None, cache.Classify(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void AForcedVerdict_OnAnAddressANameDecided_GivesTheNameItsPathBack()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var address = IPAddress.Parse("8.8.8.8");
        cache.Note(address, RouteVerdict.Proxy);

        var forced = cache.Force(address, RouteVerdict.Direct);
        Assert.Equal(new[] { "8.8.8.8" }, applier.Untunneled);
        cache.Unforce(forced!);

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(RouteVerdict.Proxy, held.Verdict);
        Assert.Equal(RoutePlan.Tunnel, held.Plan);
        Assert.True(held.ByName);
        Assert.Equal(new[] { "8.8.8.8", "8.8.8.8" }, applier.Tunneled);
    }

    [Fact]
    public void AForcedVerdict_OnAnAddressTheRangesDecided_LeavesNoNameVerdictForTheNextSession()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, hot: 8);
        var address = IPAddress.Parse("8.8.8.8");
        cache.Note(address);

        cache.Unforce(cache.Force(address, RouteVerdict.Direct)!);

        var kept = Assert.Single(cache.Hot());
        Assert.False(kept.ByName);
        Assert.Equal(nameof(RouteVerdict.None), kept.Verdict);
        Assert.Equal(applier.Added, applier.Removed);
    }

    [Fact]
    public void ARuleEditWhileAVerdictIsForced_IsWhatTheAddressKeeps()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false);
        var forced = cache.Force(IPAddress.Parse(YandexAddress), RouteVerdict.Direct);

        cache.Rebuild([], [YandexRange], []);
        cache.Unforce(forced!);

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(RouteVerdict.Direct, held.Verdict);
        Assert.Empty(applier.Removed);
    }

    [Fact]
    public void WhereFiltersOutliveARearm_ABlockLaidBeforeIt_GoesWithItsEntry()
    {
        var applier = new FakeApplier { Generation = 1, FiltersOutliveRearm = true };
        var cache = Cache(applier, split: true, block: ["10.0.0.0/8"]);
        cache.Note(IPAddress.Parse("10.1.2.3"));
        Assert.Equal(new[] { Numeric("10.1.2.3") }, applier.Dropped);

        applier.Generation = 2;
        cache.RemoveAll();

        Assert.Single(applier.Deleted);
    }

    [Fact]
    public void WhereARearmRebuildsTheFilterSet_AFilterOfTheOlderGenerationIsNotDeletedAgain()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, block: ["10.0.0.0/8"]);
        cache.Note(IPAddress.Parse("10.1.2.3"));

        applier.Generation = 2;
        cache.RemoveAll();

        Assert.Empty(applier.Deleted);
    }

    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("169.254.10.20")]
    [InlineData("0.0.0.1")]
    [InlineData("240.0.0.1")]
    public void AnAddressNoPacketGoesToThroughATunnel_EarnsNoEntryWhateverClaimsIt(string address)
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["0.0.0.0/0"]);
        var value = Numeric(address);

        cache.Note(value, app: true);
        cache.Note(value, RouteVerdict.Proxy);
        cache.Note(IPAddress.Parse(address));

        Assert.Empty(cache.Snapshot());
        Assert.Empty(applier.Tunneled);
        Assert.Empty(applier.Permitted);
        Assert.Empty(applier.Added);
    }

    [Fact]
    public void WhatAnEarlierSessionHeldOfTheMachineItselfOrAGroup_IsNotTakenBack()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, hot: 8);
        var now = DateTimeOffset.UtcNow;

        cache.Restore([
            new RememberedRoute("127.0.0.1", nameof(RouteVerdict.Proxy), false, true, now),
            new RememberedRoute("224.0.0.251", nameof(RouteVerdict.Proxy), false, true, now),
            new RememberedRoute(YandexAddress, nameof(RouteVerdict.Proxy), false, true, now),
        ]);
        cache.Adopt([IPAddress.Parse("127.0.0.2"), IPAddress.Parse("239.255.255.250")]);

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal(YandexAddress, held.Address.ToString());
    }

    [Fact]
    public async Task ADroppedDatagramToAGroup_IsNeitherHandedToTheRuleNorPermitted()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true);
        var asked = new List<string>();
        cache.SetDatagramRule(address =>
        {
            asked.Add(address.ToString());
            return false;
        });

        cache.Report(IPAddress.Parse("224.0.0.251"), null, datagram: true);
        cache.Report(IPAddress.Parse("208.67.222.222"), null, datagram: true);
        await PumpAsync(cache, () => applier.Permitted.Count > 0);

        Assert.Equal(new[] { "208.67.222.222" }, asked);
        Assert.Equal(new[] { Numeric("208.67.222.222") }, applier.Permitted);
    }

    [Fact]
    public void AnAddressOfTheTunnelAdapterItself_EarnsNoEntryUnderTheRangeOfItsNetwork()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: true, proxy: ["10.8.0.0/24"], hot: 8);
        cache.Spare([IPAddress.Parse("10.8.0.11"), IPAddress.Parse("fdcc:ad94::cafe:10")]);

        cache.Note(Numeric("10.8.0.11"), app: true);
        cache.Restore([new RememberedRoute("10.8.0.11", nameof(RouteVerdict.Proxy), false, false, DateTimeOffset.UtcNow)]);
        cache.Note(Numeric("10.8.0.1"));

        var held = Assert.Single(cache.Snapshot());
        Assert.Equal("10.8.0.1", held.Address.ToString());
        Assert.Equal(new[] { "10.8.0.1" }, applier.Tunneled);
    }
}
