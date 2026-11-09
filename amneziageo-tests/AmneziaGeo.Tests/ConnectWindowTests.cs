using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// How long a connect attempt waits for the server: its silence window starts when the tunnel process first
/// answers, not when the service is started.
/// </summary>
public sealed class ConnectWindowTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 29, 22, 43, 47, TimeSpan.Zero);
    private static readonly TimeSpan _launch = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _silence = TimeSpan.FromSeconds(12);

    [Fact]
    public void ASlowBringUp_StillLeavesTheServerItsWholeWindow()
    {
        var window = new ConnectWindow(_start, _launch, _silence);

        window.Answer(At(11));

        Assert.False(window.Silent(At(12)));
        Assert.False(window.Over(At(22)));
        Assert.False(window.Silent(At(22.9)));
        Assert.True(window.Silent(At(23)));
        Assert.True(window.Over(At(23)));
    }

    [Fact]
    public void TheWindow_CountsFromTheFirstAnswer()
    {
        var window = new ConnectWindow(_start, _launch, _silence);

        window.Answer(At(2));
        window.Answer(At(9));

        Assert.Equal(At(2), window.AnsweredAt);
        Assert.False(window.Silent(At(13.9)));
        Assert.True(window.Silent(At(14)));
    }

    [Fact]
    public void AQuickBringUp_KeepsTheLaunchDeadline()
    {
        var window = new ConnectWindow(_start, _launch, _silence);

        window.Answer(At(1));

        Assert.False(window.Over(At(19.9)));
        Assert.True(window.Over(At(20)));
    }

    [Fact]
    public void AProcessThatNeverAnswers_EndsAtTheLaunchDeadline()
    {
        var window = new ConnectWindow(_start, _launch, _silence);

        Assert.False(window.Silent(At(30)));
        Assert.False(window.Over(At(19.9)));
        Assert.True(window.Over(At(20)));
    }

    [Theory]
    [InlineData(ConnectFailureReason.TransportRejected)]
    [InlineData(ConnectFailureReason.EngineStopped)]
    public void AReportedStartupFailure_EndsTheWaitBeforeTheDeadline(ConnectFailureReason reason)
    {
        var window = new ConnectWindow(_start, _launch, _silence);
        window.Answer(At(0.1));

        window.Failure(reason);

        Assert.True(window.Over(At(0.3)));
        Assert.False(window.Silent(At(0.3)));
        window.Answer(At(1));
        Assert.True(window.Over(At(1)));
        Assert.False(new ConnectWindow(_start, _launch, _silence).Over(At(0.3)));
    }

    [Theory]
    [InlineData(ConnectFailureReason.Unknown)]
    [InlineData(ConnectFailureReason.UnderlayUnreachable)]
    [InlineData(ConnectFailureReason.Timeout)]
    public void ANetworkObservation_KeepsTheLaunchAndHandshakeWindow(ConnectFailureReason reason)
    {
        var window = new ConnectWindow(_start, _launch, _silence);
        window.Failure(reason);
        window.Answer(At(11));

        Assert.False(window.Over(At(22.9)));
        Assert.True(window.Over(At(23)));
    }

    private static DateTimeOffset At(double seconds)
    {
        return _start.AddSeconds(seconds);
    }
}
