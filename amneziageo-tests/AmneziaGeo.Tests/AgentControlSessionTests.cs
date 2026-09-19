using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The control of a tunnel names a new session each time the server answers it again, and keeps it while it stands.
/// </summary>
public sealed class AgentControlSessionTests
{
    [Fact]
    public void BeforeTheServerAnswers_NoSessionIsNamed()
    {
        var control = new AgentControl();

        control.SetRunning(true);

        Assert.Equal(string.Empty, control.Session);
    }

    [Fact]
    public void AnAnswerWithinTheSession_KeepsIt()
    {
        var control = new AgentControl();
        control.SetRunning(true);
        control.SetConnected(true);
        var first = control.Session;

        control.SetConnected(true);

        Assert.NotEqual(string.Empty, first);
        Assert.Equal(first, control.Session);
    }

    [Fact]
    public void ATunnelRaisedAgain_NamesANewSessionOnceTheServerAnswers()
    {
        var control = new AgentControl();
        control.SetRunning(true);
        control.SetConnected(true);
        var first = control.Session;

        control.SetRunning(false);
        control.SetRunning(true);
        var waiting = control.Session;
        control.SetConnected(true);

        Assert.Equal(first, waiting);
        Assert.NotEqual(first, control.Session);
    }

    [Fact]
    public void ALinkThatDroppedAndCameBack_NamesANewSession()
    {
        var control = new AgentControl();
        control.SetRunning(true);
        control.SetConnected(true);
        var first = control.Session;

        control.SetConnected(false);
        control.SetConnected(true);

        Assert.NotEqual(first, control.Session);
    }
}
