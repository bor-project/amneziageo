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

        var (level, text, error) = Assert.Single(log.Written);
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
        Assert.Contains("the websocket carrier", Assert.Single(log.Written).Text);
    }

    [Fact]
    public void StepsThatTakeEverythingDown_WriteNothing()
    {
        var log = new Lines();
        var cleanup = new SessionCleanup(log, "dev-test-b");

        cleanup.Run("the firewall protection", () => { });
        cleanup.Run("the routes kept out of the tunnel", () => { });

        Assert.Empty(log.Written);
        Assert.Equal(0, cleanup.Failed);
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
