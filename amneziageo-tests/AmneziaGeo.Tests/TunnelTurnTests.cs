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

    [Fact]
    public async Task APassingTurn_TakesItsFileAwayAsItIsGivenUp()
    {
        var path = Turn("office.example");
        var turn = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10), passing: true);

        Assert.True(turn.Held);
        Assert.True(File.Exists(path));

        turn.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ASessionThatWaitedForAPassingTurn_HoldsAFileOfItsOwn()
    {
        var path = Turn("office.example");
        var before = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10), passing: true);
        var next = TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10), passing: true);
        await Task.Delay(300);
        before.Dispose();
        var turn = await next.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(turn.Held);
        Assert.True(File.Exists(path));

        turn.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ATurnThatDoesNotPass_KeepsItsFile()
    {
        var path = Turn("routes");
        var turn = await TunnelTurn.TakeAsync(path, TimeSpan.FromSeconds(10));
        turn.Dispose();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task TheFilesNobodyHolds_AreSweptAndTheHeldOneStays()
    {
        Directory.CreateDirectory(_root);
        var left = Turn("left");
        var other = Turn("other");
        var held = Turn("held");
        await File.WriteAllBytesAsync(left, []);
        await File.WriteAllBytesAsync(other, []);
        using var turn = await TunnelTurn.TakeAsync(held, TimeSpan.FromSeconds(10), passing: true);

        var gone = TunnelTurn.Sweep([left, other, held, Turn("never")]);

        Assert.Equal(2, gone);
        Assert.False(File.Exists(left));
        Assert.False(File.Exists(other));
        Assert.True(File.Exists(held));
        Assert.True(turn.Held);
    }

    [Fact]
    public void TheTurnTheTunnelsShare_IsNotAmongTheFilesOfTheSessions()
    {
        Assert.StartsWith("turn-", Path.GetFileName(TunnelPaths.TurnFile("laptop")), StringComparison.Ordinal);
        Assert.False(Path.GetFileName(TunnelPaths.RoutesTurnFile()).StartsWith("turn-", StringComparison.Ordinal));
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
