using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ConnectionPreparationTests
{
    [Fact]
    public void CancelledPreparationCannotStartTheService()
    {
        var preparation = new ConnectionPreparation();
        var attempt = preparation.Begin();
        var started = false;
        preparation.Cancel();
        Assert.Throws<OperationCanceledException>(() => preparation.Start(attempt.Token, () => started = true));
        Assert.False(started);
        preparation.End(attempt);
    }

    [Fact]
    public void CompletingThePreviousAttemptDoesNotReleaseTheNextOne()
    {
        var preparation = new ConnectionPreparation();
        var first = preparation.Begin();
        var firstToken = first.Token;
        var second = preparation.Begin();
        Assert.True(firstToken.IsCancellationRequested);
        preparation.End(first);
        preparation.Cancel();
        Assert.True(second.IsCancellationRequested);
        preparation.End(second);
    }

    [Fact]
    public async Task CancellationStopsThePendingPreparation()
    {
        var preparation = new ConnectionPreparation();
        var attempt = preparation.Begin();
        var pending = Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token);
        preparation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        preparation.End(attempt);
        preparation.Cancel();
    }

    [Fact]
    public void ShutdownCancelsThePendingAttempt()
    {
        using var shutdown = new CancellationTokenSource();
        var preparation = new ConnectionPreparation();
        var attempt = preparation.Begin(shutdown.Token);
        shutdown.Cancel();
        Assert.True(attempt.IsCancellationRequested);
        preparation.End(attempt);
    }

    [Fact]
    public async Task CancellationAndCompletionCanRace()
    {
        var preparation = new ConnectionPreparation();
        for (var index = 0; index < 100; index++)
        {
            var attempt = preparation.Begin();
            await Task.WhenAll(Task.Run(preparation.Cancel), Task.Run(() => preparation.End(attempt))).ConfigureAwait(false);
        }
    }
}
