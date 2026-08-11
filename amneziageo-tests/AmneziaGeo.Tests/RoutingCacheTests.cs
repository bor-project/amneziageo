using System.Net;
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

        public List<uint> Dropped { get; } = [];

        public List<string> Added { get; } = [];

        public List<string> Removed { get; } = [];

        public List<(ulong Out, ulong In)> Deleted { get; } = [];

        public List<string> Tunneled { get; } = [];

        public List<string> Untunneled { get; } = [];

        public int DeleteCalls { get; private set; }

        public int UntunnelCalls { get; private set; }

        public bool RouteFails { get; set; }

        public bool TunnelFails { get; set; }

        private ulong _nextId = 1;

        public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
        {
            Permitted.Add(address);
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

    private static RoutingCache Cache(FakeApplier applier, bool split, IReadOnlyList<string>? proxy = null, IReadOnlyList<string>? direct = null, IReadOnlyList<string>? block = null, int ttlSeconds = 300, IReadOnlyCollection<string>? pinned = null)
    {
        return new RoutingCache(applier, new IdleLive(), split, proxy ?? [], direct ?? [], block ?? [], ttlSeconds, NullLogger<RoutingCache>.Instance, pinned);
    }

    private static uint Numeric(string address)
    {
        Assert.True(GeoIpRanges.TryToNumeric(IPAddress.Parse(address), out var value));
        return value;
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
    public void RebuiltRules_DropWhatTheOldOnesInstalledAndRedecide()
    {
        var applier = new FakeApplier { Generation = 1 };
        var cache = Cache(applier, split: false, direct: [YandexRange]);
        cache.Note(IPAddress.Parse(YandexAddress));

        cache.Rebuild([], [], [YandexRange]);

        Assert.Equal(new[] { YandexAddress }, applier.Removed);
        Assert.Equal(0, cache.Size);
        Assert.Equal(RouteVerdict.Block, cache.Classify(IPAddress.Parse(YandexAddress)));
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
}
