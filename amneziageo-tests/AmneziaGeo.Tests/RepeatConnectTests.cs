using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A connect for the configuration the tunnel already runs leaves it standing; any other connect still dials.
/// </summary>
public sealed class RepeatConnectTests
{
    [Fact]
    public void AConnectedTunnel_IsLeftStanding()
    {
        var control = Running("home");
        control.SetConnected(true);
        var change = control.ChangeToken;

        Assert.True(control.KeepRunning());
        Assert.False(change.IsCancellationRequested);
        Assert.False(control.BeginRetryWait().IsCancellationRequested);
    }

    [Fact]
    public void ATunnelWaitingToRetry_IsDialledAtOnce()
    {
        var control = Running("home");
        var wake = control.BeginRetryWait();
        var change = control.ChangeToken;

        Assert.True(control.KeepRunning());
        Assert.True(wake.IsCancellationRequested);
        Assert.False(change.IsCancellationRequested);
    }

    [Fact]
    public void TheRetryCount_StartsOver()
    {
        var control = Running("home");
        control.NextRetry();
        control.NextRetry();

        Assert.True(control.KeepRunning());
        Assert.Equal(0, control.RetryAttempt);
    }

    [Fact]
    public void AnotherSelectedConfiguration_IsDialled()
    {
        var control = Running("home");
        control.SetTarget("work");

        Assert.False(control.KeepRunning());
    }

    [Fact]
    public void AStoppedTunnel_IsDialled()
    {
        var control = new AgentControl();
        control.SetTarget("home");

        Assert.False(control.KeepRunning());
    }

    [Fact]
    public void ATunnelWaitingForAReconnect_IsDialled()
    {
        var control = Running("home");
        control.SetRestartRequired();

        Assert.False(control.KeepRunning());
    }

    private static AgentControl Running(string target)
    {
        var control = new AgentControl();
        control.SetTarget(target);
        control.SetRunning(true);
        return control;
    }
}
