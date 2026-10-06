using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The leak guard: a tunnel that has stood is taken down by the user alone, so the ladder that repairs it never
/// raises the session again and never stands down; it stands for every configuration and nothing switches it.
/// </summary>
public sealed class LeakGuardTests
{
    private static readonly RecoveryStep[] _ladder = [RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Restart];

    // Every echo lost: the link is measurable and measures as gone.
    private static readonly LinkSample _silent = new(true, false, 100, false, 20);

    // Traffic both ways, every echo answered.
    private static readonly LinkSample _carrying = new(true, true, 0, false, 20);

    [Fact]
    public void ATunnelThatNeverStood_IsNotHeld()
    {
        var hold = new LeakHold();

        Assert.False(hold.Active);
        Assert.False(hold.Stalled());
    }

    [Fact]
    public void ATunnelThatStood_IsHeldWhileItCarriesNothing()
    {
        var hold = new LeakHold();
        hold.Raised();

        Assert.True(hold.Active);
        Assert.True(hold.Stalled());
        Assert.False(hold.Stalled());
        Assert.True(hold.Down);
        Assert.True(hold.Carries());
        Assert.False(hold.Carries());
        Assert.False(hold.Down);
        Assert.True(hold.Active);
    }

    [Fact]
    public void TheUserTakingTheTunnelDown_EndsTheHold()
    {
        var hold = new LeakHold();
        hold.Raised();
        hold.Stalled();

        hold.Released();

        Assert.False(hold.Active);
        Assert.False(hold.Down);
    }

    [Fact]
    public void AHeldTunnel_IsNeverRaisedAgainByTheLadder()
    {
        var recovery = new LinkRecovery(_ladder, jitterPercent: 0) { Held = true };

        var steps = Feed(recovery, 600, _silent);

        Assert.DoesNotContain(RecoveryStep.Restart, steps);
        Assert.Equal([RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Rebind, RecoveryStep.Resolve], [.. steps[..4]]);
    }

    [Fact]
    public void AHeldTunnel_IsRepairedForAsLongAsItStaysDead()
    {
        var recovery = new LinkRecovery(_ladder, jitterPercent: 0) { Held = true };

        Feed(recovery, LinkRecovery.GiveUpSeconds + 120, _silent);
        var later = Feed(recovery, 300, _silent, LinkRecovery.GiveUpSeconds + 120);

        Assert.False(recovery.GivenUp);
        Assert.NotEmpty(later);
    }

    [Fact]
    public void AHeldTunnel_IsStuckOnceEveryRungThatKeepsItStandingWasTried()
    {
        var recovery = new LinkRecovery(_ladder, jitterPercent: 0) { Held = true };
        var stuckAt = 0;
        var steps = 0;

        for (var second = 1; second <= 120 && stuckAt == 0; second++)
        {
            if (recovery.Sample(_silent, second * 1000L) is not null)
            {
                steps++;
            }

            if (recovery.Stuck)
            {
                stuckAt = steps;
            }
        }

        Assert.Equal(3, stuckAt);
    }

    [Fact]
    public void AHeldTunnelThatCarriesAgain_IsNoLongerStuck()
    {
        var recovery = new LinkRecovery(_ladder, jitterPercent: 0) { Held = true };
        Feed(recovery, 120, _silent);
        Assert.True(recovery.Stuck);

        Feed(recovery, LinkRecovery.HealthySeconds + 2, _carrying, 120);

        Assert.False(recovery.Repairing);
        Assert.False(recovery.Stuck);
        Assert.True(recovery.Held);
    }

    [Fact]
    public void ALadderThatOnlyRaisesTheSessionAgain_AsksNothingOfAHeldTunnel()
    {
        var recovery = new LinkRecovery([RecoveryStep.Restart], jitterPercent: 0) { Held = true };

        var steps = Feed(recovery, 300, _silent);

        Assert.Empty(steps);
    }

    [Fact]
    public void NothingCarriesASwitchOfTheGuard()
    {
        Assert.Null(typeof(ConfigTransport).GetProperty("LeakGuard"));
        Assert.Null(typeof(ConfigEntry).GetProperty("LeakGuard"));
        Assert.Null(typeof(PortableBundle.TransportBlock).GetProperty("LeakGuard"));
        Assert.Null(typeof(ConfigTransportViewModel).GetProperty("LeakGuard"));
        Assert.Null(typeof(ConfigItemViewModel).GetProperty("LeakGuard"));
        Assert.Null(typeof(IpcContract).GetField("OpSetLeakGuard"));
    }

    [Fact]
    public void ABundle_NamesNoSwitchOfTheGuard()
    {
        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            [new PortableBundle.ConfigBlock("office", "[Interface]\n", new PortableBundle.TransportBlock(true, string.Empty, 0, 1380), null)],
            []);

        Assert.DoesNotContain("LeakGuard", PortableBundle.Serialize(bundle), StringComparison.Ordinal);
    }

    [Fact]
    public void ABundleThatStillNamesTheSwitch_IsRead()
    {
        const string json = """
            {
              "Format": "amneziageo-bundle",
              "Version": 2,
              "Configs": [
                {
                  "Name": "office",
                  "ConfigText": "[Interface]\n",
                  "Transport": { "UseWebSocket": true, "Host": "", "Port": 0, "Mtu": 1380, "LeakGuard": true }
                }
              ],
              "RoutingLists": []
            }
            """;

        var transport = PortableBundle.Deserialize(json)?.Configs[0].Transport;

        Assert.True(transport?.UseWebSocket);
        Assert.Equal(1380, transport?.Mtu);
    }

    // Reads the link once a second from the moment given and returns the steps asked for.
    private static List<RecoveryStep> Feed(LinkRecovery recovery, int seconds, LinkSample sample, int from = 0)
    {
        var steps = new List<RecoveryStep>();
        for (var second = from + 1; second <= from + seconds; second++)
        {
            if (recovery.Sample(sample, second * 1000L) is { } step)
            {
                steps.Add(step);
            }
        }

        return steps;
    }
}
