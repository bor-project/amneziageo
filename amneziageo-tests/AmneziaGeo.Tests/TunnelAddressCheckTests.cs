using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which addresses of the tunnel adapter are added again without the duplicate address check, and when the adapter
/// counts as ready.
/// </summary>
public sealed class TunnelAddressCheckTests
{
    private const int Tentative = 1;
    private const int Preferred = 4;

    private static readonly IPAddress _v4 = IPAddress.Parse("10.8.1.5");
    private static readonly IPAddress _v6 = IPAddress.Parse("fdcc:ad94::cafe:10");

    [Fact]
    public void TheConfiguredAddresses_AreReadWithoutTheirPrefix()
    {
        var expected = TunnelAddressCheck.Expected(["10.8.1.5/32", "fdcc:ad94::cafe:10/128", "garbage", "10.8.1.6"]);

        Assert.Equal(new[] { _v4, _v6, IPAddress.Parse("10.8.1.6") }, expected);
    }

    [Fact]
    public void OnlyConfiguredAddressesStillInTheCheck_AreAddedAgain()
    {
        var present = new List<(IPAddress Address, int DadState)>
        {
            (_v4, Tentative),
            (_v6, Preferred),
            (IPAddress.Parse("fe80::1"), Tentative),
        };

        Assert.Equal(new[] { _v4 }, TunnelAddressCheck.Tentative(present, [_v4, _v6]));
    }

    [Fact]
    public void TheAdapter_IsReadyOnceEveryAddressIsThereAndChecked()
    {
        IReadOnlyList<IPAddress> expected = [_v4, _v6];

        Assert.False(TunnelAddressCheck.Settled([(_v4, Preferred)], expected));
        Assert.False(TunnelAddressCheck.Settled([(_v4, Preferred), (_v6, Tentative)], expected));
        Assert.True(TunnelAddressCheck.Settled([(_v4, Preferred), (_v6, Preferred)], expected));
    }
}
