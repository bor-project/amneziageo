using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What counts as proof that this app's own resolver answered. The reading that must never happen here is a
/// foreign resolver in its place reading as healthy: it answers the health name too, just with "no such name",
/// and calling that healthy leaves every rule by domain silently dead.
/// </summary>
public sealed class DnsHealthProbeTests
{
    [Fact]
    public void TheProxysOwnAnswer_IsRecognised()
    {
        var query = DnsHealthProbe.Query(0x1234);

        var answer = DnsMessage.BuildAAnswer(query, [DnsProxy.HealthAddress], 0);

        Assert.True(DnsHealthProbe.IsOurs(answer, 0x1234));
    }

    [Fact]
    public void AnswerForSomeoneElsesQuery_IsNotOurs()
    {
        var query = DnsHealthProbe.Query(0x1234);

        var answer = DnsMessage.BuildAAnswer(query, [DnsProxy.HealthAddress], 0);

        Assert.False(DnsHealthProbe.IsOurs(answer, 0x4321));
    }

    [Fact]
    public void ForeignResolverSayingNoSuchName_ReadsAsSilence()
    {
        var query = DnsHealthProbe.Query(0x1234);

        var answer = DnsMessage.BuildNxDomain(query);

        Assert.False(DnsHealthProbe.IsOurs(answer, 0x1234));
    }

    [Fact]
    public void ForeignResolverAnsweringADifferentAddress_ReadsAsSilence()
    {
        var query = DnsHealthProbe.Query(0x1234);

        var answer = DnsMessage.BuildAAnswer(query, ["10.11.12.13"], 0);

        Assert.False(DnsHealthProbe.IsOurs(answer, 0x1234));
    }

    [Fact]
    public void AnEmptyDatagram_ReadsAsSilence()
    {
        Assert.False(DnsHealthProbe.IsOurs([], 0x1234));
    }
}
