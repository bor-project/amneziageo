using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A session of a tunnel sets up once the session before it has given up the turn of the tunnel, and without the turn
/// when the limit runs out first; tunnels of other names do not wait for each other.
/// </summary>
public sealed class TunnelTurnTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-turn-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ASession_WaitsForTheOneBeforeToGiveUpTheTurn()
    {
        var path = Turn("office.example");
        var before = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));
        var next = TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        Assert.False(next.IsCompleted);

        before.Dispose();
        using var turn = await next.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(turn.Held);
        Assert.True(turn.Waited >= TimeSpan.FromMilliseconds(400), $"waited {turn.Waited}");
    }

    [Fact]
    public async Task AFreeTurn_IsTakenWithoutWaiting()
    {
        using var turn = await TunnelTurn.TakeAsync(Turn("office.example"), TimeSpan.FromSeconds(10));

        Assert.True(turn.Held);
        Assert.Equal(TimeSpan.Zero, turn.Waited);
    }

    [Fact]
    public async Task ATurnGivenUp_IsTakenAgainAtOnce()
    {
        var path = Turn("office.example");
        var before = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));
        before.Dispose();

        using var turn = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));

        Assert.True(turn.Held);
        Assert.Equal(TimeSpan.Zero, turn.Waited);
    }

    [Fact]
    public async Task ASession_SetsUpWithoutTheTurnOnceTheLimitRunsOut()
    {
        var path = Turn("office.example");
        using var before = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));

        using var turn = await TunnelTurn.TakeAsync(path, TimeSpan.FromMilliseconds(300));

        Assert.True(before.Held);
        Assert.False(turn.Held);
        Assert.True(turn.Waited >= TimeSpan.FromMilliseconds(300), $"waited {turn.Waited}");
    }

    [Fact]
    public async Task TunnelsOfOtherNames_DoNotWaitForEachOther()
    {
        using var first = await TunnelTurn.TakeAsync(Turn("office.example"), TimeSpan.FromSeconds(10));
        using var second = await TunnelTurn.TakeAsync(Turn("laptop"), TimeSpan.FromSeconds(10));

        Assert.True(first.Held);
        Assert.True(second.Held);
        Assert.Equal(TimeSpan.Zero, second.Waited);
    }

    [Fact]
    public void EveryTunnel_HasATurnOfItsOwn()
    {
        Assert.NotEqual(TunnelPaths.TurnFile("office.example"), TunnelPaths.TurnFile("laptop"));
        Assert.Equal(TunnelPaths.TurnFile("laptop"), TunnelPaths.TurnFile("laptop"));
        Assert.Equal(".lock", Path.GetExtension(TunnelPaths.TurnFile("office.example")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Turn(string name)
    {
        return Path.Combine(_root, Path.GetFileName(TunnelPaths.TurnFile(name)));
    }
}
