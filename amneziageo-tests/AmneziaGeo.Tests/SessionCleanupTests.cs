using AmneziaGeo.Windows.App;

using Microsoft.Extensions.Logging;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What a session changed is taken down step by step: a step that fails leaves the others their turn and is named
/// as a failure of the cleanup, not of the connection.
/// </summary>
public sealed class SessionCleanupTests
{
    [Fact]
    public async Task AStepThatFails_LeavesTheStepsAfterItTheirTurn()
    {
        var log = new Lines();
        var cleanup = new SessionCleanup(log, "dev-test-b");
        var done = new List<string>();

        cleanup.Run("the firewall protection", () => done.Add("firewall"));
        cleanup.Run("the routes kept out of the tunnel", () => throw new UnauthorizedAccessException("Access to the path 'lan-state-dev-test-b.txt' is denied."));
        await cleanup.RunAsync("the websocket carrier", () => throw new IOException("the pipe is broken"));
        await cleanup.RunAsync("the DNS changes", () =>
        {
            done.Add("dns");
            return Task.CompletedTask;
        });
        cleanup.Run("the inbound firewall rule", () => done.Add("inbound"));

        Assert.Equal(["firewall", "dns", "inbound"], done);
        Assert.Equal(2, cleanup.Failed);
    }

    [Fact]
    public void AStepThatFails_IsWrittenAsAFailureOfTheCleanup()
    {
        var log = new Lines();
        var cleanup = new SessionCleanup(log, "dev-test-b");

        cleanup.Run("the routes kept out of the tunnel", () => throw new UnauthorizedAccessException("Access to the path 'lan-state-dev-test-b.txt' is denied."));

        var (level, text, error) = Assert.Single(log.Written, line => line.Level > LogLevel.Debug);
        Assert.Equal(LogLevel.Error, level);
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Contains("dev-test-b", text);
        Assert.Contains("the routes kept out of the tunnel", text);
        Assert.Contains("lan-state-dev-test-b.txt", text);
        Assert.DoesNotContain("setting up the connection", text);
    }

    [Fact]
    public async Task AStepThatWaitsAndFailsLater_IsCaughtTheSame()
    {
        var log = new Lines();
        var cleanup = new SessionCleanup(log, "dev-test-b");

        await cleanup.RunAsync("the websocket carrier", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("closed twice");
        });

        Assert.Equal(1, cleanup.Failed);
        Assert.Contains("the websocket carrier", Assert.Single(log.Written, line => line.Level == LogLevel.Error).Text);
    }

    [Fact]
    public void StepsThatTakeEverythingDown_WriteNothingButTheirTimes()
    {
        var log = new Lines();
        var cleanup = new SessionCleanup(log, "dev-test-b");

        cleanup.Run("the firewall protection", () => { });
        cleanup.Run("the routes kept out of the tunnel", () => { });

        Assert.All(log.Written, line => Assert.Equal(LogLevel.Debug, line.Level));
        Assert.Equal(0, cleanup.Failed);
    }

    [Fact]
    public async Task EveryStep_WritesHowLongItTook()
    {
        var log = new Lines();
        var clock = new Clock();
        var cleanup = new SessionCleanup(log, "dev-test-b", clock.Read);

        cleanup.Run("the firewall protection", () => clock.Now += 12);
        await cleanup.RunAsync("the websocket carrier", () =>
        {
            clock.Now += 3;
            return Task.CompletedTask;
        });

        Assert.Equal(
            [
                (LogLevel.Debug, "dev-test-b: taking down the firewall protection took 12 ms"),
                (LogLevel.Debug, "dev-test-b: taking down the websocket carrier took 3 ms"),
            ],
            log.Written.Select(line => (line.Level, line.Text)).ToList());
    }

    [Fact]
    public async Task AStepThatHoldsTheCleanup_IsNamedInAWarning()
    {
        var log = new Lines();
        var clock = new Clock();
        var cleanup = new SessionCleanup(log, "dev-test-b", clock.Read);

        await cleanup.RunAsync("the DNS changes", () =>
        {
            clock.Now += 4900;
            return Task.CompletedTask;
        });

        var (level, text, _) = Assert.Single(log.Written);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("dev-test-b: taking down the DNS changes took 4900 ms", text);
    }

    [Fact]
    public void AStepThatFails_IsTimedToo()
    {
        var log = new Lines();
        var clock = new Clock();
        var cleanup = new SessionCleanup(log, "dev-test-b", clock.Read);

        cleanup.Run("the route to the server", () =>
        {
            clock.Now += 40;
            throw new InvalidOperationException("the route is not there");
        });

        Assert.Equal([LogLevel.Error, LogLevel.Debug], log.Written.Select(line => line.Level).ToList());
        Assert.Equal("dev-test-b: taking down the route to the server took 40 ms", log.Written[1].Text);
    }

    [Fact]
    public void TheEndOfTheCleanup_NamesItsTimeAndItsLongestStep()
    {
        var log = new Lines();
        var clock = new Clock();
        var cleanup = new SessionCleanup(log, "dev-test-b", clock.Read);

        cleanup.Run("the state of the session", () => clock.Now += 12);
        cleanup.Run("the DNS changes", () => clock.Now += 4900);
        cleanup.Run("the route to the server", () => clock.Now += 3);
        cleanup.Done();

        var (level, text, _) = log.Written[^1];
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("dev-test-b: the session is taken down in 4915 ms; the longest step, the DNS changes, took 4900 ms", text);
    }

    [Fact]
    public void ACleanupWithoutSteps_NamesItsTimeAlone()
    {
        var log = new Lines();
        var clock = new Clock();
        var cleanup = new SessionCleanup(log, "dev-test-b", clock.Read);

        clock.Now += 2;
        cleanup.Done();

        Assert.Equal("dev-test-b: the session is taken down in 2 ms", Assert.Single(log.Written).Text);
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 1000;

        public long Read() => Now;
    }

    private sealed class Lines : ILogger
    {
        public List<(LogLevel Level, string Text, Exception? Error)> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Written.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
