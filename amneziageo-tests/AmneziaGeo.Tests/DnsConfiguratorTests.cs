using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The resolvers an adapter answers with: its own even while the loopback redirect holds it, so a reconnect keeps
/// the own network's resolver.
/// </summary>
public sealed class DnsConfiguratorTests
{
    [Fact]
    public void AnAdapterOffTheRedirect_KeepsItsLiveResolvers()
    {
        Assert.Equal(["192.168.1.1"], DnsConfigurator.OwnResolvers(["192.168.1.1"], [], ["10.0.0.1"]));
    }

    [Fact]
    public void AnAdapterOnTheRedirect_AnswersWithItsDhcpResolvers()
    {
        Assert.Equal(["192.168.1.1"], DnsConfigurator.OwnResolvers(["127.0.0.1"], [], ["192.168.1.1"]));
    }

    [Fact]
    public void AnAdapterOnTheRedirect_PrefersTheServersItsRecordKept()
    {
        Assert.Equal(["8.8.8.8"], DnsConfigurator.OwnResolvers(["127.0.0.1"], ["8.8.8.8"], ["192.168.1.1"]));
    }

    [Fact]
    public void AnAdapterWithoutResolvers_GivesNone()
    {
        Assert.Empty(DnsConfigurator.OwnResolvers([], [], ["192.168.1.1"]));
    }
}
