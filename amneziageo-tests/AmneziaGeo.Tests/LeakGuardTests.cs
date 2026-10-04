using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The leak guard of a configuration: a tunnel that has stood is taken down by the user alone, so the ladder that
/// repairs it never raises the session again and never stands down; the flag is kept with the transport, travels to
/// the agent in a command of its own and stays off until it is turned on.
/// </summary>
public sealed class LeakGuardTests : IAsyncLifetime
{
    private static readonly RecoveryStep[] _ladder = [RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Restart];

    // Every echo lost: the link is measurable and measures as gone.
    private static readonly LinkSample _silent = new(true, false, 100, false, 20);

    // Traffic both ways, every echo answered.
    private static readonly LinkSample _carrying = new(true, true, 0, false, 20);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-guard-{Guid.NewGuid():N}.db");
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
    public void AGuardThatIsOff_HoldsNoTunnel()
    {
        var hold = new LeakHold();

        hold.Set(false);
        hold.Raised();

        Assert.False(hold.Active);
        Assert.False(hold.Stalled());
        Assert.False(hold.Down);
    }

    [Fact]
    public void AGuardedTunnelThatNeverStood_IsNotHeld()
    {
        var hold = new LeakHold();

        hold.Set(true);

        Assert.False(hold.Active);
        Assert.False(hold.Stalled());
    }

    [Fact]
    public void AGuardedTunnelThatStood_IsHeldWhileItCarriesNothing()
    {
        var hold = new LeakHold();
        hold.Set(true);
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
        hold.Set(true);
        hold.Raised();
        hold.Stalled();

        hold.Released();

        Assert.False(hold.Active);
        Assert.False(hold.Down);
        Assert.True(hold.Guard);
    }

    [Fact]
    public void TheGuardTurnedOff_EndsTheHold()
    {
        var hold = new LeakHold();
        hold.Set(true);
        hold.Raised();
        hold.Stalled();

        hold.Set(false);

        Assert.False(hold.Active);
        Assert.False(hold.Down);
        Assert.True(hold.Stood);
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
    public async Task TheTransport_KeepsTheLeakGuardAndLeavesItOffUntilItIsTurnedOn()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("kept", false, LeakGuard: true));
        await _store.SetConfigTransportAsync(new ConfigTransport("plain", true, 1380, true, MtuMode.Custom));

        var kept = await _store.GetConfigTransportAsync("kept");
        var plain = await _store.GetConfigTransportAsync("plain");

        Assert.True(kept!.LeakGuard);
        Assert.False(plain!.LeakGuard);

        await _store.SetConfigTransportAsync(kept with { LeakGuard = false });

        Assert.False((await _store.GetConfigTransportAsync("kept"))!.LeakGuard);
    }

    [Fact]
    public async Task TheGuardTurnedOnInTheWindow_GoesToTheAgentInACommandOfItsOwn()
    {
        var agent = new Commands();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false);

        transport.LeakGuard = true;
        Assert.True(transport.IsDirty);
        await transport.CommitAsync();

        Assert.Equal([IpcContract.OpSetWebSocket, IpcContract.OpSetLeakGuard], agent.Sent.Select(command => command.Op));
        Assert.Equal(["e2e", "on"], agent.Sent[1].Args);
    }

    [Fact]
    public async Task TheGuardTurnedOffInTheWindow_IsSentAsOff()
    {
        var agent = new Commands();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, leakGuard: true);

        transport.LeakGuard = false;
        await transport.CommitAsync();

        Assert.Equal(["e2e", "off"], agent.Sent[^1].Args);
        Assert.Equal(IpcContract.OpSetLeakGuard, agent.Sent[^1].Op);
    }

    [Fact]
    public async Task ASaveOfAnotherSetting_SendsNoGuardCommand()
    {
        var agent = new Commands();
        var transport = new ConfigTransportViewModel(agent, "e2e", false, 1420, false, leakGuard: true);

        Assert.True(transport.LeakGuard);
        Assert.False(transport.IsDirty);
        transport.UseIpv6 = true;
        await transport.CommitAsync();

        Assert.Equal([IpcContract.OpSetWebSocket], agent.Sent.Select(command => command.Op));
    }

    [Fact]
    public void AGuardTakenBack_LeavesNothingToSave()
    {
        var transport = new ConfigTransportViewModel(new Commands(), "e2e", false, 1420, false);

        transport.LeakGuard = true;
        transport.Revert();

        Assert.False(transport.LeakGuard);
        Assert.False(transport.IsDirty);
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

    // Keeps every command the window sends instead of an agent.
    private sealed class Commands : IAgentConnection
    {
        public List<IpcCommand> Sent { get; } = [];

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
            Sent.Add(command);
            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
