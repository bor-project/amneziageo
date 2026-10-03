using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A session cancelled while its leak protection is armed reports no failure.
/// </summary>
public sealed class ArmRetryTests
{
    [Fact]
    public async Task ASessionCancelledWhileArming_EndsWithTheCancellationAndReportsNoFailure()
    {
        using var session = new CancellationTokenSource();
        var retries = new List<int>();

        var run = ArmRetry.RunAsync(
            () =>
            {
                session.Cancel();
                return false;
            },
            attempts: 4,
            TimeSpan.Zero,
            retries.Add,
            session.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(retries);
    }

    [Fact]
    public async Task AnArmThatNeverTakes_SaysHowManyAttemptsWereMade()
    {
        var retries = new List<int>();

        var (armed, attempts) = await ArmRetry.RunAsync(() => false, attempts: 4, TimeSpan.Zero, retries.Add, CancellationToken.None);

        Assert.False(armed);
        Assert.Equal(4, attempts);
        Assert.Equal(new[] { 1, 2, 3 }, retries);
    }

    [Fact]
    public async Task AnArmThatTakesOnTheSecondAttempt_IsNotRepeated()
    {
        var calls = 0;

        var (armed, attempts) = await ArmRetry.RunAsync(() => ++calls == 2, attempts: 4, TimeSpan.Zero, _ => { }, CancellationToken.None);

        Assert.True(armed);
        Assert.Equal(2, attempts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ASessionCancelledBetweenAttempts_EndsWithTheCancellation()
    {
        using var session = new CancellationTokenSource();

        var run = ArmRetry.RunAsync(() => false, attempts: 4, TimeSpan.FromSeconds(30), _ => session.Cancel(), session.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
