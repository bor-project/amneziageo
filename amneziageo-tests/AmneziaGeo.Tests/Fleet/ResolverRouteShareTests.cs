using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// Резолвер конфигурации получает маршрут через туннель, только пока этот туннель сам работает с именами.
/// </summary>
public sealed class ResolverRouteShareTests
{
    private static readonly IReadOnlyList<string> Resolvers = ["192.168.1.1", "1.1.1.1"];

    [Fact]
    public void TheHolderOfLookups_RoutesItsResolvers()
    {
        Assert.Equal(Resolvers, TunnelRunner.RoutedResolvers(Resolvers, holdsResolver: true, carriesNames: false, probesLink: false));
    }

    [Fact]
    public void ANeutralTunnelWithoutNames_LeavesItsResolversToTheHolder()
    {
        Assert.Empty(TunnelRunner.RoutedResolvers(Resolvers, holdsResolver: false, carriesNames: false, probesLink: false));
    }

    [Fact]
    public void ATunnelCarryingNamesOfItsOwn_RoutesItsResolvers()
    {
        Assert.Equal(Resolvers, TunnelRunner.RoutedResolvers(Resolvers, holdsResolver: false, carriesNames: true, probesLink: false));
    }

    [Fact]
    public void ALinkProbedThroughTheResolvers_KeepsThemRouted()
    {
        Assert.Equal(Resolvers, TunnelRunner.RoutedResolvers(Resolvers, holdsResolver: false, carriesNames: false, probesLink: true));
    }
}
