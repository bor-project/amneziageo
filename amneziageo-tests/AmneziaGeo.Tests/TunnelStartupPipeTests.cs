using System.Diagnostics;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TunnelStartupPipeTests
{
    [Fact]
    public async Task CancellationReachesOnlyTheNamedStartup()
    {
        using var stop = new CancellationTokenSource();
        var first = "startup-first-" + Guid.NewGuid().ToString("N");
        var second = "startup-second-" + Guid.NewGuid().ToString("N");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstServer = Serve(first, reached);
        var secondServer = Serve(second, other);
        try
        {
            await RuntimeSnapshotPipe.CancelStartupAsync(first, NullLogger.Instance).ConfigureAwait(false);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.False(other.Task.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(firstServer, secondServer).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        Task Serve(string name, TaskCompletionSource signal)
        {
            return RuntimeSnapshotPipe.ServeAsync(name, (op, _) =>
            {
                if (op == RuntimeSnapshotPipe.OpCancelStartup)
                {
                    signal.TrySetResult();
                }

                return Task.FromResult("ok");
            }, NullLogger.Instance, stop.Token);
        }
    }

    [Fact]
    public async Task MissingStartupDoesNotBlockTheStopLoop()
    {
        var elapsed = Stopwatch.StartNew();
        await RuntimeSnapshotPipe.CancelStartupAsync("missing-" + Guid.NewGuid().ToString("N"), NullLogger.Instance).ConfigureAwait(false);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }
}
