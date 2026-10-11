using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A route past the tunnel that stood there before the client asked for it belongs to whoever put it: the client
/// takes back only a route it put itself or one a tunnel of its own records.
/// </summary>
public sealed class RouteOwnershipTests
{
    private const uint Put = 0;
    private const uint AlreadyThere = 5010;
    private const uint Refused = 5;
    private const string Record = "203.0.113.7 19 192.168.1.1";

    private static Lazy<RouteStateFile.Held> Recorded(params string[] records)
    {
        return new Lazy<RouteStateFile.Held>(() =>
        {
            var held = new RouteStateFile.Held();
            held.Add(records);
            return held;
        });
    }

    [Fact]
    public void ARouteTheTunnelPut_IsItsOwn_AndNobodyIsAskedWhoRecordsIt()
    {
        var recorded = Recorded();

        Assert.True(RouteManager.Ours(Put, recorded, Record));
        Assert.False(recorded.IsValueCreated);
    }

    [Fact]
    public void ARouteThatStoodThereAlready_IsNotTheClients()
    {
        Assert.False(RouteManager.Ours(AlreadyThere, Recorded(), Record));
    }

    [Fact]
    public void ARouteThatStoodThereAlready_IsTheClients_WhenATunnelOfItRecordsTheRoute()
    {
        Assert.True(RouteManager.Ours(AlreadyThere, Recorded(Record), Record));
        Assert.True(RouteManager.Ours(AlreadyThere, Recorded("203.0.113.7"), Record));
    }

    [Fact]
    public void ARouteThatStoodThereAlready_IsNotTheClients_WhenItsTunnelsRecordAnotherHopOrAnotherDestination()
    {
        Assert.False(RouteManager.Ours(AlreadyThere, Recorded("203.0.113.7 19 192.168.1.254"), Record));
        Assert.False(RouteManager.Ours(AlreadyThere, Recorded("203.0.113.8 19 192.168.1.1"), Record));
    }

    [Fact]
    public void ARouteThatCouldNotBePut_IsNotTheClients()
    {
        Assert.False(RouteManager.Ours(Refused, Recorded(Record), Record));
    }
}
