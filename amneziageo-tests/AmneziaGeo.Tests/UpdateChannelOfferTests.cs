using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The pick of the channel offers the test channel only to a test build, a build of no kind and a device that reads
/// it; the default update source names the address its releases lie under.
/// </summary>
public sealed class UpdateChannelOfferTests
{
    [Fact]
    public void AReleaseOrABeta_LeavesTheTestChannelOut()
    {
        Assert.Equal([UpdateChannels.Stable, UpdateChannels.Beta], UpdateChannels.Offered(UpdateChannels.Stable, UpdateChannels.Stable));
        Assert.Equal([UpdateChannels.Stable, UpdateChannels.Beta], UpdateChannels.Offered(UpdateChannels.Beta, UpdateChannels.Beta));
        Assert.Equal([UpdateChannels.Stable, UpdateChannels.Beta], UpdateChannels.Offered(UpdateChannels.Beta, UpdateChannels.Stable));
    }

    [Fact]
    public void ATestBuild_ABuildOfNoKindAndAPickedTestChannel_OfferEveryChannel()
    {
        Assert.Equal(UpdateChannels.All, UpdateChannels.Offered(UpdateChannels.Test, UpdateChannels.Beta));
        Assert.Equal(UpdateChannels.All, UpdateChannels.Offered(string.Empty, UpdateChannels.Stable));
        Assert.Equal(UpdateChannels.All, UpdateChannels.Offered(UpdateChannels.Stable, UpdateChannels.Test));
    }

    [Fact]
    public void TheAddress_NamesTheReleasesOfTheRepositoryOrTheServerWithoutItsScheme()
    {
        Assert.Equal("github.com/bor-project/amneziageo/releases",
            UpdateSources.AddressOf("https://github.com/bor-project/amneziageo/releases/latest/download/update.json"));
        Assert.Equal("vpn.example.net:8443/u7f3", UpdateSources.AddressOf("https://vpn.example.net:8443/u7f3/"));
        Assert.Equal("srv.example.org/updates", UpdateSources.AddressOf("https://srv.example.org/updates/"));
        Assert.Equal("srv.example.org", UpdateSources.AddressOf("https://srv.example.org/"));
    }
}
