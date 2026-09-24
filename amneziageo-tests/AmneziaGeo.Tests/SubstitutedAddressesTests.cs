using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A corporate resolver answers a blocked site with an address of its own; the resolver behind the tunnel gives the
/// real ones. Only an answer that shares nothing with the real one is taken for a substitute.
/// </summary>
public sealed class SubstitutedAddressesTests
{
    private static readonly IPAddress Stub = IPAddress.Parse("188.186.154.88");
    private static readonly IPAddress Meta = IPAddress.Parse("57.144.222.34");

    [Fact]
    public void AnswerThatSharesNothingWithTheTunnelIsASubstitute()
    {
        Assert.Equal([Stub], SubstitutedAddresses.Of([Stub], [Meta]));
    }

    [Fact]
    public void AnswerThatSharesOneAddressIsReal()
    {
        var edge = IPAddress.Parse("162.159.140.229");

        Assert.Empty(SubstitutedAddresses.Of([edge, IPAddress.Parse("172.66.0.227")], [edge]));
    }

    [Fact]
    public void NothingFromTheTunnelProvesNothing()
    {
        Assert.Empty(SubstitutedAddresses.Of([Stub], []));
    }

    [Fact]
    public void PrivateAndReservedAddressesAreNeverTaken()
    {
        var own = IPAddress.Parse("10.0.10.20");
        var loop = IPAddress.Parse("127.0.0.1");
        var nothing = IPAddress.Parse("0.0.0.0");

        Assert.Empty(SubstitutedAddresses.Of([own, loop, nothing], [Meta]));
        Assert.False(new SubstitutedAddresses().Add(own));
    }

    [Fact]
    public void AnAddressIsTakenInOnce()
    {
        var substituted = new SubstitutedAddresses();
        var raised = new List<IPAddress>();
        substituted.Added += raised.Add;

        Assert.True(substituted.Add(Stub));
        Assert.False(substituted.Add(Stub));
        Assert.True(substituted.Contains(Stub));
        Assert.False(substituted.Contains(Meta));
        Assert.Equal([Stub], raised);
        Assert.False(substituted.Add(IPAddress.Parse("2a03:2880::1")));
    }
}
