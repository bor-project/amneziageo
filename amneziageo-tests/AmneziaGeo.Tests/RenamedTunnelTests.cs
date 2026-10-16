using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A configuration renamed while its tunnel is up is dialled again under the new name, any other rename leaves the
/// tunnel standing; a renamed routing list is what the running session reports.
/// </summary>
public sealed class RenamedTunnelTests
{
    [Fact]
    public void TheRunningConfiguration_IsDialledAgainUnderItsNewName()
    {
        var control = Running("home");
        var change = control.ChangeToken;

        control.RetargetName("home", "work");

        Assert.True(change.IsCancellationRequested);
        Assert.True(control.Running);
        Assert.Equal("work", control.RunningTarget);
        Assert.Equal("work", control.Target);
    }

    [Fact]
    public void AnotherConfiguration_LeavesTheTunnelStanding()
    {
        var control = Running("home");
        var change = control.ChangeToken;

        control.RetargetName("work", "office");

        Assert.False(change.IsCancellationRequested);
        Assert.Equal("home", control.RunningTarget);
    }

    [Fact]
    public void TheSelectionOfAStoppedAgent_IsRenamedWithoutDialling()
    {
        var control = new AgentControl();
        control.SetTarget("home");
        var change = control.ChangeToken;

        control.RetargetName("home", "work");

        Assert.False(change.IsCancellationRequested);
        Assert.False(control.Running);
        Assert.Equal("work", control.Target);
    }

    [Fact]
    public void TheSelectionRenamedBesideARunningTunnel_LeavesItStanding()
    {
        var control = Running("home");
        control.SetTarget("work");
        var change = control.ChangeToken;

        control.RetargetName("work", "office");

        Assert.False(change.IsCancellationRequested);
        Assert.Equal("home", control.RunningTarget);
        Assert.Equal("office", control.Target);
    }

    [Fact]
    public void TheRedial_TakesThePendingReconnectWithIt()
    {
        var control = Running("home");
        control.SetRestartRequired();

        control.RetargetName("home", "work");

        Assert.False(control.RestartRequired);
    }

    [Fact]
    public void ARenamedList_IsWhatTheRunningSessionReports()
    {
        var session = new LiveSession();
        session.SetPlan("split", "Недоступные сайты", ["10.0.0.0/24"]);

        session.SetListName("Unavailable sites");

        Assert.Equal("Unavailable sites", session.ListName);
        Assert.Equal("split", session.Mode);
        Assert.Equal(["10.0.0.0/24"], session.ConfigRoutes);
    }

    private static AgentControl Running(string target)
    {
        var control = new AgentControl();
        control.SetTarget(target);
        control.SetRunning(true);
        return control;
    }
}
