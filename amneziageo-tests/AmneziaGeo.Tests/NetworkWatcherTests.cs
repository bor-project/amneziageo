using System.Net.NetworkInformation;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The watcher of the underlay network starts with the host whether or not the interfaces can be listed.
/// </summary>
public sealed class NetworkWatcherTests
{
    [Fact]
    public async Task AListingThatFailsAtStart_IsLoggedAndTheWatcherStarts()
    {
        var log = new Lines();
        using var watcher = new NetworkWatcher(new AgentControl(), log, () => throw new NetworkInformationException(232));

        await watcher.StartAsync(CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);

        var line = Assert.Single(log.Written);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.IsType<NetworkInformationException>(line.Error);
        Assert.StartsWith("the network interfaces could not be listed", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListingThatWorks_LogsNothing()
    {
        var log = new Lines();
        using var watcher = new NetworkWatcher(new AgentControl(), log, () => "eth0:192.0.2.10");

        await watcher.StartAsync(CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);

        Assert.Empty(log.Written);
    }

    private sealed class Lines : ILogger<NetworkWatcher>
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
